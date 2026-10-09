using System.ComponentModel.DataAnnotations;

namespace Notepal.Contracts;

/// <summary>Processing state of a captured page (and, by aggregation, of a note).</summary>
public enum ProcessingStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
}

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

public sealed record UpdatePageTextRequest(
    [Required(AllowEmptyStrings = true), MaxLength(NoteLimits.MaxTextLength)] string Text);

/// <summary>Moves a page to <see cref="PageNumber"/> (1-based); the other pages shift to make room.</summary>
public sealed record MovePageRequest([Range(1, UploadLimits.MaxFilesPerNote)] int PageNumber);

/// <summary>Moves a page, with its extracted text and corrections, to the end of another note owned by the same person.</summary>
public sealed record TransferPageRequest(Guid TargetNoteId);
