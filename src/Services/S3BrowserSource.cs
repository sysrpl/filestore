using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using filestore.Models;
using RegionEndpoint = Amazon.RegionEndpoint;

namespace filestore.Services;

/// <summary>
/// Browses S3 with the active profile's credentials.
///
/// Paths look like "s3://" (the bucket list), "s3://bucket/" and "s3://bucket/folder/".
/// Folders are the "CommonPrefixes" S3 returns when listing with a "/" delimiter.
/// </summary>
/// <summary>What a bucket holds, for the delete warning.</summary>
/// <param name="Files">Current files.</param>
/// <param name="Bytes">Size of the current files.</param>
/// <param name="OlderVersions">Older versions and delete markers (versioned buckets).</param>
/// <param name="Incomplete">True if counting stopped at the limit, so there is more.</param>
public sealed record BucketContents(int Files, long Bytes, int OlderVersions, bool Incomplete);

public sealed class S3BrowserSource : IBrowserSource, IDisposable
{
    public const string Root = "s3://";

    /// <summary>The longest a temporary link can last when signed with access keys (S3's limit).</summary>
    public static readonly TimeSpan MaxTemporaryUrlLifetime = TimeSpan.FromDays(7);

    // DeleteObjects takes at most this many keys per request.
    private const int DeleteBatchSize = 1000;

    // Per-file ACL checks: how many at once, and the most done for one folder.
    private const int AclConcurrency = 8;
    private const int MaxAclChecks = 1000;

    private const string AllUsersGroup = "http://acs.amazonaws.com/groups/global/AllUsers";
    private const string AuthenticatedUsersGroup = "http://acs.amazonaws.com/groups/global/AuthenticatedUsers";

    private readonly ProfileService _profiles;

    // Clients and bucket regions belong to one profile; they're thrown away when it changes.
    private Profile? _clientProfile;
    private CloudFrontLookup? _cloudFront;
    private readonly Dictionary<string, AmazonS3Client> _clients = new();
    private readonly Dictionary<string, string> _bucketRegions = new();

    public S3BrowserSource(ProfileService profiles)
    {
        _profiles = profiles;
    }

    public string HomePath => Root;

    /// <summary>Why CloudFront distributions couldn't be listed, or null.</summary>
    public string? CloudFrontError => _cloudFront?.LastError;

    public string NormalizePath(string path)
    {
        path = path.Trim();
        path = path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
            ? Root + path[Root.Length..]
            : Root + path.TrimStart('/');

        if (path.Length > Root.Length && !path.EndsWith('/'))
            path += "/";
        return path;
    }

    public string? GetParent(string path)
    {
        if (path.Length <= Root.Length)
            return null;

        var trimmed = path.TrimEnd('/');
        var parent = trimmed[..(trimmed.LastIndexOf('/') + 1)];
        return parent.Length < Root.Length ? Root : parent;
    }

    public async Task<IReadOnlyList<BrowserItem>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var profile = EnsureProfile();
        if (path.Length <= Root.Length)
            return await ListBucketsAsync(profile, cancellationToken);

        var (bucket, prefix) = SplitPath(path);
        var region = await GetBucketRegionAsync(bucket, profile, cancellationToken);
        var client = GetClient(profile, region);

        var items = new List<BrowserItem>();
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, Delimiter = "/" };
        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request, cancellationToken);

            foreach (var folder in response.CommonPrefixes ?? Enumerable.Empty<string>())
            {
                items.Add(new BrowserItem
                {
                    Name = folder[prefix.Length..].TrimEnd('/'),
                    Path = $"{Root}{bucket}/{folder}",
                    Kind = BrowserItemKind.Folder,
                });
            }

            foreach (var obj in response.S3Objects ?? Enumerable.Empty<S3Object>())
            {
                // Skip the empty "folder/" marker object for the folder we're in.
                if (obj.Key == prefix)
                    continue;

                items.Add(new BrowserItem
                {
                    Name = obj.Key[prefix.Length..],
                    Path = $"{Root}{bucket}/{obj.Key}",
                    Kind = BrowserItemKind.File,
                    Size = obj.Size,
                    Modified = ToLocal(obj.LastModified),
                });
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return items;
    }

    /// <summary>
    /// Fills in each file's access (below) and share URL. The share URL is the CloudFront URL when a
    /// distribution serves the bucket (without signed URLs), otherwise the S3 URL if the file is public.
    ///
    /// Works out whether each file in a listed S3 folder is private or public:
    /// 1. Bucket policy public -> every file is flagged public.
    /// 2. ACLs disabled (bucket owner enforced) or ignored (Block Public Access) -> every file is private.
    /// 3. Otherwise each file's ACL is read (up to <see cref="MaxAclChecks"/>, a few at a time).
    /// Not covered: Block Public Access set for the whole AWS account, which can override a public ACL.
    /// </summary>
    public async Task LoadDetailsAsync(IReadOnlyList<BrowserItem> items, CancellationToken cancellationToken)
    {
        var files = items.Where(i => i.Kind == BrowserItemKind.File).ToList();
        if (files.Count == 0)
            return;

        var profile = EnsureProfile();
        var bucket = SplitPath(files[0].Path).Bucket;
        var region = await GetBucketRegionAsync(bucket, profile, cancellationToken);
        var client = GetClient(profile, region);

        _cloudFront ??= new CloudFrontLookup(Credentials(profile));
        var distribution = await _cloudFront.FindAsync(bucket, cancellationToken);
        if (distribution?.RequiresSignedUrls == true)
            distribution = null; // plain links wouldn't open

        void SetShareUrl(BrowserItem file)
        {
            var key = SplitPath(file.Path).Key;
            file.ShareUrl = distribution?.UrlFor(key)
                ?? (file.IsPublic ? PublicObjectUrl(bucket, region, key) : null);
        }

        // CloudFront links don't depend on the S3 access checks, so they're available straight away.
        foreach (var file in files)
            SetShareUrl(file);

        var (bucketAccess, bucketNote) = await GetBucketAccessAsync(client, bucket, cancellationToken);
        if (bucketAccess is { } access)
        {
            foreach (var file in files)
            {
                file.SetAccess(access, bucketNote);
                SetShareUrl(file);
            }
            return;
        }

        foreach (var file in files.Skip(MaxAclChecks))
            file.SetAccess(ObjectAccess.Unknown, $"Not checked: only the first {MaxAclChecks} files in a folder are checked.");

        using var throttle = new SemaphoreSlim(AclConcurrency);
        await Task.WhenAll(files.Take(MaxAclChecks).Select(async file =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var (fileAccess, note) = await GetObjectAccessAsync(client, bucket, SplitPath(file.Path).Key, cancellationToken);
                file.SetAccess(fileAccess, note + bucketNote);
                SetShareUrl(file);
            }
            catch (AmazonS3Exception ex)
            {
                file.SetAccess(ObjectAccess.Unknown, ex.ErrorCode == "AccessDenied"
                    ? "Couldn't check: no permission to read this file's ACL (s3:GetObjectAcl)."
                    : $"Couldn't check: {ex.Message}");
            }
            finally
            {
                throttle.Release();
            }
        }));
    }

    /// <summary>
    /// Settings that decide access for the whole bucket. Returns an access when every file shares it,
    /// otherwise null (check each file) plus a note to add to each file's explanation.
    /// </summary>
    private static async Task<(ObjectAccess? Access, string Note)> GetBucketAccessAsync(
        IAmazonS3 client, string bucket, CancellationToken cancellationToken)
    {
        // Does the bucket policy grant public access? null = couldn't tell.
        bool? policyPublic;
        try
        {
            var status = await client.GetBucketPolicyStatusAsync(
                new GetBucketPolicyStatusRequest { BucketName = bucket }, cancellationToken);
            policyPublic = status.PolicyStatus?.IsPublic == true;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucketPolicy")
        {
            policyPublic = false;
        }
        catch (AmazonS3Exception)
        {
            policyPublic = null;
        }

        if (policyPublic == true)
        {
            return (ObjectAccess.Public,
                "Public: the bucket policy allows public access (it may cover only some paths).");
        }

        // Can ACLs make anything public?
        var aclsOff = false;
        try
        {
            var block = await client.GetPublicAccessBlockAsync(
                new GetPublicAccessBlockRequest { BucketName = bucket }, cancellationToken);
            aclsOff = block.PublicAccessBlockConfiguration?.IgnorePublicAcls == true;
        }
        catch (AmazonS3Exception)
        {
            // No Block Public Access settings on the bucket, or no permission to read them.
        }

        if (!aclsOff)
        {
            try
            {
                var ownership = await client.GetBucketOwnershipControlsAsync(
                    new GetBucketOwnershipControlsRequest { BucketName = bucket }, cancellationToken);
                aclsOff = ownership.OwnershipControls?.Rules?
                    .Any(r => r.ObjectOwnership == ObjectOwnership.BucketOwnerEnforced) == true;
            }
            catch (AmazonS3Exception)
            {
                // No ownership controls (ACLs in use), or no permission to read them.
            }
        }

        var policyNote = policyPublic is null
            ? " (The bucket policy couldn't be checked; it could still make files public.)"
            : "";

        return aclsOff
            ? (ObjectAccess.Private, "Private: this bucket doesn't allow public ACLs and its policy isn't public." + policyNote)
            : (null, policyNote);
    }

    private static async Task<(ObjectAccess Access, string Note)> GetObjectAccessAsync(
        IAmazonS3 client, string bucket, string key, CancellationToken cancellationToken)
    {
        var acl = await client.GetObjectAclAsync(
            new GetObjectAclRequest { BucketName = bucket, Key = key }, cancellationToken);

        var isPublic = (acl.Grants ?? Enumerable.Empty<S3Grant>()).Any(g =>
            g.Grantee?.URI is AllUsersGroup or AuthenticatedUsersGroup
            && (g.Permission == S3Permission.READ || g.Permission == S3Permission.FULL_CONTROL));

        return isPublic
            ? (ObjectAccess.Public, "Public: this file's ACL lets anyone read it.")
            : (ObjectAccess.Private, "Private: only the bucket owner and AWS accounts it grants can read this file.");
    }

    /// <summary>
    /// A new client for the bucket's region, using the active profile. The caller disposes it,
    /// so it keeps working even if the pane switches profile meanwhile.
    /// </summary>
    public async Task<AmazonS3Client> CreateClientForBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        var profile = EnsureProfile();
        var region = await GetBucketRegionAsync(bucket, profile, cancellationToken);
        return NewClient(profile, region);
    }

    public bool CanModify(string folderPath) => folderPath.Length > Root.Length;

    /// <summary>The active profile's default region, used as the suggestion for new buckets.</summary>
    public string? DefaultRegion => _profiles.ActiveProfile?.Region;

    /// <summary>
    /// Creates a bucket in the given region with the AWS defaults (private, ACLs off). With
    /// <paramref name="allowPublicAcls"/>, ACLs are then turned on so single files can be made public.
    /// </summary>
    public async Task CreateBucketAsync(string name, string region, bool allowPublicAcls, CancellationToken cancellationToken)
    {
        var profile = EnsureProfile();
        // Sent to the bucket's own region; the SDK then sets the location constraint (none for us-east-1).
        await GetClient(profile, region).PutBucketAsync(
            new PutBucketRequest { BucketName = name, UseClientRegion = true }, cancellationToken);
        _bucketRegions[name] = region;

        if (allowPublicAcls)
            await AllowPublicAclsAsync(name, cancellationToken);
    }

    /// <summary>
    /// Counts a bucket's current files, their size, and older versions, stopping after
    /// <paramref name="limit"/> entries. Needs s3:ListBucketVersions.
    /// </summary>
    public async Task<BucketContents> SummarizeBucketAsync(string bucket, int limit, CancellationToken cancellationToken)
    {
        var client = await GetClientForBucketAsync(bucket, cancellationToken);
        int files = 0, older = 0, seen = 0;
        long bytes = 0;

        var request = new ListVersionsRequest { BucketName = bucket };
        ListVersionsResponse response;
        do
        {
            response = await client.ListVersionsAsync(request, cancellationToken);
            foreach (var version in response.Versions ?? Enumerable.Empty<S3ObjectVersion>())
            {
                if (version.IsLatest == true && version.IsDeleteMarker != true)
                {
                    files++;
                    bytes += SizeOf(version.Size);
                }
                else
                {
                    older++;
                }

                if (++seen >= limit)
                    return new BucketContents(files, bytes, older, Incomplete: true);
            }
            request.KeyMarker = response.NextKeyMarker;
            request.VersionIdMarker = response.NextVersionIdMarker;
        }
        while (response.IsTruncated == true);

        return new BucketContents(files, bytes, older, Incomplete: false);
    }

    /// <summary>
    /// Deletes every object in the bucket - all versions and delete markers, so versioned buckets
    /// empty too - then the bucket itself. <paramref name="onDeleted"/> gets the running count.
    /// </summary>
    public async Task DeleteBucketAsync(string bucket, Action<int>? onDeleted, CancellationToken cancellationToken)
    {
        var client = await GetClientForBucketAsync(bucket, cancellationToken);
        var deleted = 0;

        var request = new ListVersionsRequest { BucketName = bucket };
        ListVersionsResponse response;
        do
        {
            response = await client.ListVersionsAsync(request, cancellationToken);
            var versions = (response.Versions ?? Enumerable.Empty<S3ObjectVersion>()).ToList();
            foreach (var batch in versions.Chunk(DeleteBatchSize))
            {
                var delete = new DeleteObjectsRequest { BucketName = bucket, Quiet = true };
                foreach (var version in batch)
                    delete.AddKey(version.Key, version.VersionId);

                var result = await client.DeleteObjectsAsync(delete, cancellationToken);
                if (result.DeleteErrors is { Count: > 0 } errors)
                {
                    throw new IOException(
                        $"{errors.Count} object(s) couldn't be deleted, e.g. {errors[0].Key}: {errors[0].Message}");
                }

                deleted += batch.Length;
                onDeleted?.Invoke(deleted);
            }
            request.KeyMarker = response.NextKeyMarker;
            request.VersionIdMarker = response.NextVersionIdMarker;
        }
        while (response.IsTruncated == true);

        await client.DeleteBucketAsync(bucket, cancellationToken);
        _bucketRegions.Remove(bucket);
    }

    /// <summary>The domain of the CloudFront distribution serving the bucket, or null.</summary>
    public async Task<string?> GetDistributionDomainAsync(string bucket, CancellationToken cancellationToken)
    {
        var profile = EnsureProfile();
        _cloudFront ??= new CloudFrontLookup(Credentials(profile));
        return (await _cloudFront.FindAsync(bucket, cancellationToken))?.Domain;
    }

    /// <summary>
    /// Lets files in the bucket be made public with an ACL: turns ACLs on (Object Ownership: bucket
    /// owner preferred) and unticks the two ACL options in the bucket's Block Public Access settings.
    /// The bucket-policy options are left as they are. Account-wide Block Public Access can still block ACLs.
    /// </summary>
    public async Task AllowPublicAclsAsync(string bucket, CancellationToken cancellationToken)
    {
        var client = await GetClientForBucketAsync(bucket, cancellationToken);

        await client.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = bucket,
            OwnershipControls = new OwnershipControls
            {
                Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.BucketOwnerPreferred }],
            },
        }, cancellationToken);

        PublicAccessBlockConfiguration? current = null;
        try
        {
            current = (await client.GetPublicAccessBlockAsync(
                new GetPublicAccessBlockRequest { BucketName = bucket }, cancellationToken)).PublicAccessBlockConfiguration;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchPublicAccessBlockConfiguration")
        {
            // No bucket-level Block Public Access: nothing to change.
        }

        if (current is not null && (current.BlockPublicAcls == true || current.IgnorePublicAcls == true))
        {
            await client.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
            {
                BucketName = bucket,
                PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
                {
                    BlockPublicAcls = false,
                    IgnorePublicAcls = false,
                    BlockPublicPolicy = current.BlockPublicPolicy,
                    RestrictPublicBuckets = current.RestrictPublicBuckets,
                },
            }, cancellationToken);
        }
    }

    /// <summary>Why a bucket name breaks the S3 naming rules, or null if it's fine.</summary>
    public static string? ValidateBucketName(string name)
    {
        if (name.Length is < 3 or > 63)
            return "Bucket names must be 3 to 63 characters long.";
        if (name.Any(c => c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-')))
            return "Use only lowercase letters, numbers, dots (.) and hyphens (-).";
        if (!char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))
            return "Bucket names must start and end with a letter or number.";
        if (name.Contains("..") || name.Contains(".-") || name.Contains("-."))
            return "Dots can't be next to other dots or hyphens.";
        if (System.Net.IPAddress.TryParse(name, out _) && name.Count(c => c == '.') == 3)
            return "Bucket names can't look like an IP address.";
        if (name.StartsWith("xn--") || name.StartsWith("sthree-") || name.StartsWith("amzn-s3-demo-")
            || name.EndsWith("-s3alias") || name.EndsWith("--ol-s3") || name.EndsWith(".mrap")
            || name.EndsWith("--x-s3") || name.EndsWith("--table-s3"))
            return "That prefix or suffix is reserved by AWS.";
        return null;
    }

    public string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name.";
        if (name is "." or "..")
            return "That name isn't allowed.";
        if (name.Contains('/'))
            return "Names can't contain /.";
        return null;
    }

    /// <summary>S3 has no real folders: this creates an empty "name/" object, like the AWS console does.</summary>
    public async Task CreateFolderAsync(string folderPath, string name, CancellationToken cancellationToken)
    {
        var (bucket, prefix) = SplitPath(folderPath);
        var client = await GetClientForBucketAsync(bucket, cancellationToken);
        if (await NameTakenAsync(client, bucket, prefix + name, cancellationToken))
            throw new IOException($"\"{name}\" already exists.");

        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = prefix + name + "/",
            ContentBody = "",
        }, cancellationToken);
    }

    /// <summary>
    /// S3 can't rename: each object is copied to the new name, then the old one is deleted.
    /// A public file stays public. For a folder, every object under it is moved (their ACLs aren't
    /// carried over). Objects over 5 GB can't be copied this way.
    /// </summary>
    public async Task RenameAsync(BrowserItem item, string newName, CancellationToken cancellationToken)
    {
        var (bucket, key) = SplitPath(item.Path);
        var client = await GetClientForBucketAsync(bucket, cancellationToken);

        // "a/b/file.txt" -> parent "a/b/"; "a/b/folder/" -> parent "a/b/".
        var trimmed = key.TrimEnd('/');
        var parent = trimmed[..(trimmed.LastIndexOf('/') + 1)];
        if (await NameTakenAsync(client, bucket, parent + newName, cancellationToken))
            throw new IOException($"\"{newName}\" already exists.");

        if (item.Kind == BrowserItemKind.File)
        {
            var newKey = parent + newName;
            await CopyAsync(client, bucket, key, newKey, keepPublic: item.IsPublic, cancellationToken);
            await client.DeleteObjectAsync(bucket, key, cancellationToken);
            return;
        }

        var newPrefix = parent + newName + "/";
        var objects = await ListAllAsync(client, bucket, key, cancellationToken);
        foreach (var obj in objects)
            await CopyAsync(client, bucket, obj.Key, newPrefix + obj.Key[key.Length..], keepPublic: false, cancellationToken);
        await DeleteKeysAsync(client, bucket, objects.Select(o => o.Key).ToList(), cancellationToken);
    }

    public async Task DeleteAsync(BrowserItem item, CancellationToken cancellationToken)
    {
        var (bucket, key) = SplitPath(item.Path);
        var client = await GetClientForBucketAsync(bucket, cancellationToken);

        if (item.Kind == BrowserItemKind.File)
        {
            await client.DeleteObjectAsync(bucket, key, cancellationToken);
            return;
        }

        var keys = (await ListAllAsync(client, bucket, key, cancellationToken)).Select(o => o.Key).ToList();
        await DeleteKeysAsync(client, bucket, keys, cancellationToken);
    }

    /// <summary>Every object under a prefix, following continuation tokens.</summary>
    public static async Task<List<S3Object>> ListAllAsync(
        IAmazonS3 client, string bucket, string prefix, CancellationToken cancellationToken)
    {
        var objects = new List<S3Object>();
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request, cancellationToken);
            objects.AddRange(response.S3Objects ?? Enumerable.Empty<S3Object>());
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
        return objects;
    }

    private async Task<AmazonS3Client> GetClientForBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        var profile = EnsureProfile();
        return GetClient(profile, await GetBucketRegionAsync(bucket, profile, cancellationToken));
    }

    /// <summary>True if a file "key" or a folder "key/" already exists.</summary>
    private static async Task<bool> NameTakenAsync(IAmazonS3 client, string bucket, string key, CancellationToken cancellationToken)
    {
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = key,
            Delimiter = "/",
            MaxKeys = 100,
        }, cancellationToken);

        return (response.S3Objects ?? Enumerable.Empty<S3Object>()).Any(o => o.Key == key)
            || (response.CommonPrefixes ?? Enumerable.Empty<string>()).Any(p => p == key + "/");
    }

    private static Task CopyAsync(
        IAmazonS3 client, string bucket, string sourceKey, string destinationKey, bool keepPublic,
        CancellationToken cancellationToken)
    {
        var request = new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = sourceKey,
            DestinationBucket = bucket,
            DestinationKey = destinationKey,
        };
        if (keepPublic)
            request.CannedACL = S3CannedACL.PublicRead;
        return client.CopyObjectAsync(request, cancellationToken);
    }

    /// <summary>Deletes keys in batches; fails if S3 reports any key it couldn't delete.</summary>
    private static async Task DeleteKeysAsync(
        IAmazonS3 client, string bucket, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        foreach (var batch in keys.Chunk(DeleteBatchSize))
        {
            var request = new DeleteObjectsRequest { BucketName = bucket, Quiet = true };
            foreach (var key in batch)
                request.AddKey(key);

            var response = await client.DeleteObjectsAsync(request, cancellationToken);
            if (response.DeleteErrors is { Count: > 0 } errors)
            {
                throw new IOException(
                    $"{errors.Count} object(s) couldn't be deleted, e.g. {errors[0].Key}: {errors[0].Message}");
            }
        }
    }

    /// <summary>
    /// A presigned HTTPS link that lets anyone download the file until it expires. It's signed
    /// locally with the active profile's keys (no request to AWS), so it also stops working if those
    /// keys are deactivated or deleted.
    /// </summary>
    public async Task<string> CreateTemporaryUrlAsync(string path, TimeSpan validFor, CancellationToken cancellationToken)
    {
        if (validFor <= TimeSpan.Zero || validFor > MaxTemporaryUrlLifetime)
            throw new ArgumentOutOfRangeException(nameof(validFor), "A temporary link can last from 1 minute to 7 days.");

        var profile = EnsureProfile();
        var (bucket, key) = SplitPath(path);
        var region = await GetBucketRegionAsync(bucket, profile, cancellationToken);

        // Bucket names with dots need path-style links, or the HTTPS certificate won't match.
        using var client = new AmazonS3Client(Credentials(profile), new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            ForcePathStyle = bucket.Contains('.'),
        });
        return await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Protocol = Protocol.HTTPS,
            Expires = DateTime.UtcNow + validFor,
        });
    }

    /// <summary>Changes one file's ACL to private or public-read.</summary>
    public async Task SetObjectAccessAsync(string path, UploadAccess access, CancellationToken cancellationToken)
    {
        var profile = EnsureProfile();
        var (bucket, key) = SplitPath(path);
        var client = GetClient(profile, await GetBucketRegionAsync(bucket, profile, cancellationToken));
        await client.PutObjectAclAsync(new PutObjectAclRequest
        {
            BucketName = bucket,
            Key = key,
            ACL = access == UploadAccess.PublicRead ? S3CannedACL.PublicRead : S3CannedACL.Private,
        }, cancellationToken);
    }

    /// <summary>
    /// The plain S3 URL of an object. Bucket names with dots use the path style, since the
    /// virtual-hosted style's HTTPS certificate doesn't cover them.
    /// </summary>
    public static string PublicObjectUrl(string bucket, string region, string key) =>
        bucket.Contains('.')
            ? $"https://s3.{region}.amazonaws.com/{bucket}/{EncodeKey(key)}"
            : $"https://{bucket}.s3.{region}.amazonaws.com/{EncodeKey(key)}";

    /// <summary>URL-encodes each part of an object key, keeping the "/" separators.</summary>
    public static string EncodeKey(string key) =>
        string.Join("/", key.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// Why this bucket's own settings stop files being made public with an ACL, or null if they don't.
    /// Account-wide Block Public Access can't be seen here; <see cref="DescribePublicAclError"/>
    /// explains it when S3 refuses.
    /// </summary>
    public async Task<string?> GetPublicAclBlockerAsync(string bucket, CancellationToken cancellationToken)
    {
        var client = await GetClientForBucketAsync(bucket, cancellationToken);
        try
        {
            var block = await client.GetPublicAccessBlockAsync(
                new GetPublicAccessBlockRequest { BucketName = bucket }, cancellationToken);
            var config = block.PublicAccessBlockConfiguration;
            if (config?.BlockPublicAcls == true || config?.IgnorePublicAcls == true)
            {
                return "This bucket's Block Public Access settings block public ACLs, so files can't be made public yet.";
            }
        }
        catch (AmazonS3Exception)
        {
            // No Block Public Access settings on the bucket, or no permission to read them.
        }

        try
        {
            var ownership = await client.GetBucketOwnershipControlsAsync(
                new GetBucketOwnershipControlsRequest { BucketName = bucket }, cancellationToken);
            if (ownership.OwnershipControls?.Rules?.Any(r => r.ObjectOwnership == ObjectOwnership.BucketOwnerEnforced) == true)
            {
                return "ACLs are turned off for this bucket (Object Ownership: bucket owner enforced), so files can't be made public yet.";
            }
        }
        catch (AmazonS3Exception)
        {
            // No ownership controls (ACLs in use), or no permission to read them.
        }
        return null;
    }

    /// <summary>
    /// Like <see cref="DescribeError"/>, for a request that sets a public-read ACL. S3 answers
    /// "access denied" - even to the account's root user - when Block Public Access forbids it.
    /// </summary>
    public static string DescribePublicAclError(Exception ex) => ex is AmazonS3Exception { ErrorCode: "AccessDenied" }
        ? "S3 refused the public ACL. This is almost always Block Public Access, on this bucket or for the whole account " +
          "(S3 console > Block Public Access settings for this account), which blocks public ACLs even for the root user. " +
          $"S3 said: {ex.Message}"
        : DescribeError(ex);

    /// <summary>A readable message for an S3 error, with advice for the common ACL problem.</summary>
    public static string DescribeError(Exception ex) => ex switch
    {
        AmazonS3Exception { ErrorCode: "BucketAlreadyExists" } =>
            "that bucket name is already used by another AWS account. Bucket names are global; try another.",
        AmazonS3Exception { ErrorCode: "BucketAlreadyOwnedByYou" } =>
            "you already have a bucket with that name.",
        AmazonS3Exception { ErrorCode: "AccessControlListNotSupported" } =>
            "this bucket has ACLs turned off, so files can't be made public or private one at a time. " +
            "Use locked (private) uploads, or control access with a bucket policy.",
        _ => ex.Message,
    };

    /// <summary>Splits "s3://bucket/some/key" into ("bucket", "some/key"). "s3://" gives ("", "").</summary>
    public static (string Bucket, string Key) SplitPath(string path)
    {
        var rest = path[Root.Length..];
        var slash = rest.IndexOf('/');
        return slash < 0 ? (rest, "") : (rest[..slash], rest[(slash + 1)..]);
    }

    public void Dispose() => ResetClients();

    private async Task<IReadOnlyList<BrowserItem>> ListBucketsAsync(Profile profile, CancellationToken cancellationToken)
    {
        var response = await GetClient(profile, profile.Region).ListBucketsAsync(cancellationToken);
        return (response.Buckets ?? Enumerable.Empty<S3Bucket>())
            .Select(b => new BrowserItem
            {
                Name = b.BucketName,
                Path = $"{Root}{b.BucketName}/",
                Kind = BrowserItemKind.Bucket,
                Modified = ToLocal(b.CreationDate),
            })
            .ToList();
    }

    /// <summary>Buckets live in a specific region; requests must go to that region.</summary>
    private async Task<string> GetBucketRegionAsync(string bucket, Profile profile, CancellationToken cancellationToken)
    {
        if (_bucketRegions.TryGetValue(bucket, out var region))
            return region;

        try
        {
            var response = await GetClient(profile, profile.Region).GetBucketLocationAsync(bucket, cancellationToken);
            region = response.Location?.Value switch
            {
                null or "" => "us-east-1",  // S3 reports us-east-1 as an empty location
                "EU" => "eu-west-1",        // legacy name
                var value => value,
            };
        }
        catch (AmazonS3Exception)
        {
            // No permission for GetBucketLocation: try the profile's region.
            region = profile.Region;
        }

        _bucketRegions[bucket] = region;
        return region;
    }

    private Profile EnsureProfile()
    {
        var profile = _profiles.ActiveProfile
            ?? throw new InvalidOperationException(
                "No AWS profile selected. Add one under Profiles > Manage Profiles...");

        // Editing a profile replaces the object, so a different reference means new keys or a new profile.
        if (!ReferenceEquals(profile, _clientProfile))
        {
            ResetClients();
            _clientProfile = profile;
        }
        return profile;
    }

    private AmazonS3Client GetClient(Profile profile, string region)
    {
        if (!_clients.TryGetValue(region, out var client))
        {
            client = NewClient(profile, region);
            _clients[region] = client;
        }
        return client;
    }

    private static AmazonS3Client NewClient(Profile profile, string region) =>
        new(Credentials(profile), RegionEndpoint.GetBySystemName(region));

    private static BasicAWSCredentials Credentials(Profile profile) =>
        new(profile.AccessKeyId, profile.SecretAccessKey);

    private void ResetClients()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
        _bucketRegions.Clear();
        _cloudFront = null;
        _clientProfile = null;
    }

    private static DateTime? ToLocal(DateTime? value) => value?.ToLocalTime();

    // Takes long? so it works whether the SDK declares Size as long or long?.
    private static long SizeOf(long? size) => size ?? 0;
}
