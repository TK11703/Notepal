using System.ComponentModel.DataAnnotations;

namespace Notepal.Contracts;

public sealed record UpdateNoteTagsRequest([Required] IReadOnlyList<string> Tags);

/// <summary>A tag the user has used, with the number of their notes that carry it.</summary>
public sealed record TagDto(string Name, int Count);

/// <summary>Rules shared by the API and the web app so tags look the same everywhere.</summary>
public static class TagLimits
{
    public const int MaxTagsPerNote = 20;
    public const int MaxTagLength = 40;

    /// <summary>
    /// Normalizes a tag: trims it, removes a leading '#', treats commas as spaces, collapses whitespace to single spaces and lower-cases it.
    /// Returns <c>null</c> when nothing usable is left.
    /// </summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var value = string.Join(' ', tag.Replace(',', ' ').Trim().TrimStart('#').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
        if (value.Length > MaxTagLength)
        {
            value = value[..MaxTagLength].TrimEnd();
        }

        return value.Length == 0 ? null : value;
    }

    /// <summary>Normalizes, de-duplicates and sorts a set of tags.</summary>
    public static List<string> NormalizeAll(IEnumerable<string?>? tags) =>
        (tags ?? []).Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
