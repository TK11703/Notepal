namespace Notepal.Shared;

/// <summary>Processing state of a captured page (and, by aggregation, of a note).</summary>
public enum ProcessingStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
}

public sealed record NoteSummaryDto(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int PageCount,
    ProcessingStatus Status,
    string? Preview,
    IReadOnlyList<string> Tags);

public sealed record PageDto(
    Guid Id,
    int PageNumber,
    string FileName,
    string ContentType,
    long SizeBytes,
    ProcessingStatus Status,
    string? ExtractedText,
    string? Text,
    bool IsEdited,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed record NoteDto(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ProcessingStatus Status,
    IReadOnlyList<PageDto> Pages,
    IReadOnlyList<string> Tags);

public sealed record UpdateNoteRequest(string Title);

public sealed record UpdatePageTextRequest(string Text);

public sealed record UpdateNoteTagsRequest(IReadOnlyList<string> Tags);

/// <summary>A tag the user has used, with the number of their notes that carry it.</summary>
public sealed record TagDto(string Name, int Count);

public sealed record SearchResultDto(
    Guid NoteId,
    string Title,
    Guid? PageId,
    int? PageNumber,
    string Snippet,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Tags);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class UploadLimits
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    public const int MaxFilesPerNote = 20;
    public const long MaxRequestBytes = 100 * 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    };

    public const string AcceptAttribute = "image/*,.pdf,.docx,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document";
}

/// <summary>Rules shared by the API and the web app so tags look the same everywhere.</summary>
public static class TagLimits
{
    public const int MaxTagsPerNote = 20;
    public const int MaxTagLength = 40;

    /// <summary>
    /// Normalises a tag: trims it, removes a leading '#', treats commas as spaces, collapses whitespace to single spaces and lower-cases it.
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

    /// <summary>Normalises, de-duplicates and sorts a set of tags.</summary>
    public static List<string> NormalizeAll(IEnumerable<string?>? tags) =>
        (tags ?? []).Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
