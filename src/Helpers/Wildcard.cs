using System.Text.RegularExpressions;

namespace filestore.Helpers;

/// <summary>
/// File name patterns like "*.jpg" or "fam*.mp4", several separated by ";" (e.g. "*.jpg;*.png"):
/// a name matches if it matches any of them. * matches any run of characters, ? any one character,
/// and case doesn't matter. A pattern with no wildcards matches names containing it.
/// </summary>
public sealed class Wildcard
{
    private readonly Regex _regex;

    public Wildcard(string patterns)
    {
        var parts = patterns.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ToRegex);
        _regex = new Regex($"^(?:{string.Join("|", parts)})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    public bool IsMatch(string name) => _regex.IsMatch(name);

    private static string ToRegex(string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            pattern = $"*{pattern}*";
        return Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
    }
}
