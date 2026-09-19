namespace filestore.Models;

/// <summary>One set of AWS credentials, identified by a friendly name.</summary>
public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";

    public override string ToString() => Name;
}

/// <summary>Everything stored in the encrypted profiles file.</summary>
public sealed class ProfileData
{
    public Guid? ActiveProfileId { get; set; }
    public List<Profile> Profiles { get; set; } = new();
}
