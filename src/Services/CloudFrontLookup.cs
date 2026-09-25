using System.Text.RegularExpressions;
using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.Runtime;
using RegionEndpoint = Amazon.RegionEndpoint;

namespace filestore.Services;

/// <summary>A CloudFront distribution that serves a bucket.</summary>
/// <param name="Id">The distribution's ID, e.g. "E2QWRUHAPOMQZL".</param>
/// <param name="Domain">The first alternate domain name (CNAME), or the dxxxx.cloudfront.net name.</param>
/// <param name="OriginPath">The distribution's origin path, e.g. "/site", or "".</param>
/// <param name="RequiresSignedUrls">True when plain links won't work (trusted key groups / signers).</param>
public sealed record BucketDistribution(string Id, string Domain, string OriginPath, bool RequiresSignedUrls)
{
    /// <summary>The CloudFront URL for an object key, or null if the key is outside the origin path.</summary>
    public string? UrlFor(string key) => PathFor(key) is { } path ? $"https://{Domain}{path}" : null;

    /// <summary>
    /// The URL path ("/a/b.jpg") CloudFront serves an object key on, or null if the key is outside
    /// the origin path.
    /// </summary>
    public string? PathFor(string key)
    {
        // Origin path "/site" means https://domain/x serves the key "site/x".
        var prefix = OriginPath.Trim('/');
        if (prefix.Length > 0)
        {
            if (!key.StartsWith(prefix + "/", StringComparison.Ordinal))
                return null;
            key = key[(prefix.Length + 1)..];
        }
        return "/" + S3BrowserSource.EncodeKey(key);
    }
}

/// <summary>
/// Finds the CloudFront distribution in front of each bucket, by listing the account's distributions
/// and matching the origin of each one's default behavior to an S3 bucket. Results are cached for a
/// few minutes. Needs the cloudfront:ListDistributions permission; without it no distributions are found.
/// </summary>
public sealed class CloudFrontLookup
{
    // bucket.s3.amazonaws.com, bucket.s3.us-east-1.amazonaws.com, bucket.s3-website-us-east-1.amazonaws.com, ...
    private static readonly Regex S3OriginDomain = new(
        @"^(?<bucket>.+?)\.s3([.-][a-z0-9-]+)*\.amazonaws\.com(\.cn)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly AWSCredentials _credentials;
    // Each bucket's distributions, best first (see Rank).
    private Task<Dictionary<string, List<BucketDistribution>>>? _distributions;
    private DateTime _loadedAt;

    public CloudFrontLookup(AWSCredentials credentials)
    {
        _credentials = credentials;
    }

    /// <summary>Why the last lookup found nothing (e.g. no permission), or null if it worked.</summary>
    public string? LastError { get; private set; }

    /// <summary>The distribution whose links to use for a bucket, or null if none serves it.</summary>
    public async Task<BucketDistribution?> FindAsync(string bucket, CancellationToken cancellationToken) =>
        (await FindAllAsync(bucket, cancellationToken)).FirstOrDefault();

    /// <summary>Every enabled distribution that serves a bucket, best first.</summary>
    public async Task<IReadOnlyList<BucketDistribution>> FindAllAsync(string bucket, CancellationToken cancellationToken)
    {
        if (_distributions is null || DateTime.UtcNow - _loadedAt > CacheFor)
        {
            // One shared load; a caller cancelling doesn't cancel it for everyone else.
            _distributions = LoadAsync();
            _loadedAt = DateTime.UtcNow;
        }

        var map = await _distributions.WaitAsync(cancellationToken);
        return map.GetValueOrDefault(bucket) ?? [];
    }

    /// <summary>
    /// Asks a distribution to drop its cached copies of these URL paths (see
    /// <see cref="BucketDistribution.PathFor"/>), so the next request fetches them from S3.
    /// Returns the invalidation's ID. Needs the cloudfront:CreateInvalidation permission.
    /// </summary>
    public async Task<string> InvalidateAsync(
        BucketDistribution distribution, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        using var client = new AmazonCloudFrontClient(_credentials, RegionEndpoint.USEast1);
        var response = await client.CreateInvalidationAsync(new CreateInvalidationRequest
        {
            DistributionId = distribution.Id,
            InvalidationBatch = new InvalidationBatch
            {
                // Must be unique per request, or CloudFront treats it as a repeat of an earlier one.
                CallerReference = Guid.NewGuid().ToString("N"),
                Paths = new Paths { Quantity = paths.Count, Items = paths.ToList() },
            },
        }, cancellationToken);
        return response.Invalidation.Id;
    }

    private async Task<Dictionary<string, List<BucketDistribution>>> LoadAsync()
    {
        var map = new Dictionary<string, List<BucketDistribution>>(StringComparer.Ordinal);
        try
        {
            // CloudFront is a global service; its API lives in us-east-1.
            using var client = new AmazonCloudFrontClient(_credentials, RegionEndpoint.USEast1);
            var request = new ListDistributionsRequest();
            DistributionList? list;
            do
            {
                list = (await client.ListDistributionsAsync(request, CancellationToken.None)).DistributionList;
                foreach (var distribution in list?.Items ?? Enumerable.Empty<DistributionSummary>())
                    Add(map, distribution);
                request.Marker = list?.NextMarker;
            }
            while (list?.IsTruncated == true);

            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex is AmazonServiceException { ErrorCode: "AccessDenied" }
                ? "no permission to list CloudFront distributions (cloudfront:ListDistributions)"
                : ex.Message;
        }
        return map;
    }

    private static void Add(Dictionary<string, List<BucketDistribution>> map, DistributionSummary distribution)
    {
        if (distribution.Enabled != true)
            return;

        var behavior = distribution.DefaultCacheBehavior;
        var origin = distribution.Origins?.Items?.FirstOrDefault(o => o.Id == behavior?.TargetOriginId);
        var match = S3OriginDomain.Match(origin?.DomainName ?? "");
        if (origin is null || !match.Success)
            return;

        var bucket = match.Groups["bucket"].Value.ToLowerInvariant();
        var aliases = distribution.Aliases?.Items?.Where(a => !a.StartsWith('*')).ToList() ?? [];
        // A bucket named after a domain (cache.example.com) is served on that domain, even when the
        // distribution lists another one (example.com) first.
        var alias = aliases.FirstOrDefault(a => a.Equals(bucket, StringComparison.OrdinalIgnoreCase))
            ?? aliases.FirstOrDefault();
        var found = new BucketDistribution(
            distribution.Id,
            alias ?? distribution.DomainName,
            origin.OriginPath ?? "",
            behavior?.TrustedKeyGroups?.Enabled == true || behavior?.TrustedSigners?.Enabled == true);

        // Several distributions for one bucket: prefer the bucket's own domain, then any custom domain.
        if (!map.TryGetValue(bucket, out var list))
            map[bucket] = list = [];
        var index = list.FindIndex(d => Rank(found, bucket) > Rank(d, bucket));
        list.Insert(index < 0 ? list.Count : index, found);
    }

    /// <summary>2 for the bucket's own domain, 1 for another custom domain, 0 for dxxxx.cloudfront.net.</summary>
    private static int Rank(BucketDistribution distribution, string bucket) =>
        distribution.Domain.Equals(bucket, StringComparison.OrdinalIgnoreCase) ? 2
        : distribution.Domain.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase) ? 0
        : 1;
}
