using System.ComponentModel.DataAnnotations;

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
    IReadOnlyList<string> Tags,
    NoteRole Role = NoteRole.Owner,
    string? SharedBy = null,
    int ShareCount = 0);

public sealed record UpdateNoteRequest(
    [Required, MaxLength(NoteLimits.MaxTitleLength)] string Title);

public sealed record UpdatePageTextRequest(
    [Required(AllowEmptyStrings = true), MaxLength(NoteLimits.MaxTextLength)] string Text);

public sealed record UpdateNoteTagsRequest(IReadOnlyList<string> Tags);

/// <summary>Moves a page to <see cref="PageNumber"/> (1-based); the other pages shift to make room.</summary>
public sealed record MovePageRequest([Range(1, UploadLimits.MaxFilesPerNote)] int PageNumber);

/// <summary>Moves a page, with its extracted text and corrections, to the end of another note owned by the same person.</summary>
public sealed record TransferPageRequest(Guid TargetNoteId);

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

/// <summary>What the caller may do with a note.</summary>
public enum NoteRole
{
    /// <summary>Created the note: full control, including sharing and deleting it.</summary>
    Owner = 0,

    /// <summary>Shared with edit rights: can rename, tag, correct text and add, remove and reorder pages, but not delete or share.</summary>
    Contributor = 1,

    /// <summary>Shared read-only.</summary>
    Reader = 2,
}

/// <summary>Permission granted to a person a note is shared with.</summary>
public enum SharePermission
{
    Reader = 0,
    Contributor = 1,
}

/// <summary>A person a note is shared with.</summary>
public sealed record NoteShareDto(
    Guid Id,
    string Email,
    string? DisplayName,
    SharePermission Permission,
    DateTimeOffset CreatedAt);

/// <summary>
/// Shares a note with someone. <see cref="UserId"/> (the Entra object id) is set when the person was picked from the directory;
/// otherwise the share is matched by email address when they sign in.
/// </summary>
public sealed record AddNoteShareRequest(string Email, string? DisplayName = null, string? UserId = null, SharePermission Permission = SharePermission.Reader);

public sealed record UpdateNoteShareRequest(SharePermission Permission);

/// <summary>
/// A note on the Shared notes page. For notes the caller shared, <see cref="SharedWith"/> lists the people;
/// for notes shared with the caller, <see cref="SharedBy"/> names the owner and <see cref="Role"/> is the caller's permission.
/// </summary>
public sealed record SharedNoteDto(
    NoteSummaryDto Note,
    NoteRole Role,
    string? SharedBy,
    DateTimeOffset SharedAt,
    IReadOnlyList<NoteShareDto> SharedWith);

/// <summary>Removes the caller's access to notes other people shared with them (the notes themselves are not changed).</summary>
public sealed record LeaveSharedNotesRequest(IReadOnlyList<Guid> NoteIds);

/// <summary>How many of the requested notes the caller no longer has access to.</summary>
public sealed record LeaveSharedNotesResult(int Left);

/// <summary>A person found in the organization's directory.</summary>
public sealed record DirectoryUserDto(string Id, string DisplayName, string Email);

public static class ShareLimits
{
    public const int MaxSharesPerNote = 50;
    public const int MaxEmailLength = 254;
    public const int MaxDisplayNameLength = 200;
    public const int MaxNotesPerLeave = 100;

    /// <summary>Trims and lower-cases an email address; returns <c>null</c> unless it looks like <c>name@domain.tld</c>.</summary>
    public static string? NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value.Length > MaxEmailLength || value.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@'))
        {
            return null;
        }

        var domain = value[(at + 1)..];
        var dot = domain.IndexOf('.');
        return dot > 0 && dot < domain.Length - 1 ? value : null;
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>Totals over the caller's own notes (optionally only those carrying a set of tags).</summary>
public sealed record NoteStatsDto(int NoteCount, int PageCount)
{
    public double AveragePagesPerNote => NoteCount == 0 ? 0 : (double)PageCount / NoteCount;
}

public static class NoteLimits
{
    public const int MaxTitleLength = 200;
    public const int MaxTextLength = 1_000_000;
    public const int MaxSearchLength = 200;
    public const int MaxPageSize = 100;
}

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
