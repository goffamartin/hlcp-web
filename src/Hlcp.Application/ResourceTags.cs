namespace Hlcp.Application;

public static class ResourceTags
{
    public static string[] Validate(IEnumerable<string>? tags)
    {
        var values = tags?.Take(17).ToArray() ?? [];
        if (values.Length > 16 || values.Any(tag => !IsValid(tag)) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Use at most 16 unique lowercase tags of at most 64 characters.", nameof(tags));
        return values;
    }

    public static bool IsValid(string? tag) =>
        tag is { Length: > 0 and <= 64 } && IsLetterOrDigit(tag[0]) &&
        tag.All(c => IsLetterOrDigit(c) || c is '-' or '_' or '.');

    public static bool Matches(IEnumerable<string>? tags, IReadOnlyCollection<string> required) =>
        required.All(wanted => tags?.Contains(wanted, StringComparer.Ordinal) == true);

    private static bool IsLetterOrDigit(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
