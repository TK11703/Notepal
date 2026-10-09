namespace Notepal.Contracts;

public sealed record SearchResultDto(
    Guid NoteId,
    string Title,
    Guid? PageId,
    int? PageNumber,
    string Snippet,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Tags);
