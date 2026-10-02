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
    string? Preview);

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
    IReadOnlyList<PageDto> Pages);

public sealed record UpdateNoteRequest(
    [Required, MaxLength(NoteLimits.MaxTitleLength)] string Title);

public sealed record UpdatePageTextRequest(
    [Required(AllowEmptyStrings = true), MaxLength(NoteLimits.MaxTextLength)] string Text);

public sealed record SearchResultDto(
    Guid NoteId,
    string Title,
    Guid? PageId,
    int? PageNumber,
    string Snippet,
    DateTimeOffset UpdatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

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
