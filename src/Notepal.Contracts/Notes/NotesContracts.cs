using System.ComponentModel.DataAnnotations;

namespace Notepal.Contracts;

public sealed record NoteSummaryDto(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int PageCount,
    ProcessingStatus Status,
    string? Preview,
    IReadOnlyList<string> Tags);

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
