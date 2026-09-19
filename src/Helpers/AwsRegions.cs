using RegionEndpoint = Amazon.RegionEndpoint;

namespace filestore.Helpers;

/// <summary>The AWS regions offered in the region lists.</summary>
public static class AwsRegions
{
    /// <summary>The region used when none has been chosen.</summary>
    public const string Default = "us-east-1";

    /// <summary>Every region's system name (e.g. "eu-west-2"), sorted.</summary>
    public static IReadOnlyList<string> Names { get; } = RegionEndpoint.EnumerableAllRegions
        .Select(r => r.SystemName)
        .Order()
        .ToList();
}
