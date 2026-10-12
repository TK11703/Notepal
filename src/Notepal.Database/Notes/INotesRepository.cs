using Notepal.Contracts;

namespace Notepal.Database;

/// <summary>User-facing persistence with ownership and sharing enforced in SQL.</summary>
public interface INotesRepository
{
    Task<NoteAccess?> GetAccessAsync(Guid noteId, DatabaseUser user, CancellationToken ct);
    Task<PagedResult<NoteSummaryDto>> ListNotesAsync(string ownerId, IEnumerable<string> tags, int page, int pageSize, CancellationToken ct);
    Task<NoteStatsDto> GetStatsAsync(string ownerId, IEnumerable<string> tags, CancellationToken ct);
    Task<NoteDto?> GetNoteAsync(Guid noteId, DatabaseUser user, NoteAccess access, CancellationToken ct);
    Task<NoteDto> CreateNoteAsync(string ownerId, string title, IEnumerable<string> tags, IReadOnlyList<NewPage> files, CancellationToken ct);
    Task<NoteDto?> RenameNoteAsync(Guid noteId, DatabaseUser user, NoteAccess access, string title, CancellationToken ct);
    Task<NoteDto?> SetTagsAsync(Guid noteId, DatabaseUser user, NoteAccess access, IEnumerable<string> tags, CancellationToken ct);
    Task<AddPagesResult> AddPagesAsync(Guid noteId, DatabaseUser user, NoteAccess access, IReadOnlyList<NewPage> files, CancellationToken ct);
    Task<(PageChangeOutcome Outcome, NoteDto? Note)> DeletePageAsync(Guid noteId, Guid pageId, DatabaseUser user, NoteAccess access, CancellationToken ct);
    Task<(PageChangeOutcome Outcome, NoteDto? Note)> MovePageAsync(Guid noteId, Guid pageId, int pageNumber, DatabaseUser user, NoteAccess access, CancellationToken ct);
    Task<(PageChangeOutcome Outcome, NoteDto? Note)> TransferPageAsync(Guid noteId, Guid pageId, Guid targetNoteId, DatabaseUser user, NoteAccess access, CancellationToken ct);
    Task<bool> DeleteNoteAsync(string ownerId, Guid noteId, CancellationToken ct);
    Task<PageOriginal?> GetOriginalAsync(Guid noteId, Guid pageId, DatabaseUser user, CancellationToken ct);
    Task<PageDto?> UpdatePageTextAsync(Guid noteId, Guid pageId, DatabaseUser user, string text, CancellationToken ct);
    Task<(ReprocessOutcome Outcome, PageDto? Page)> ResetForReprocessingAsync(Guid noteId, Guid pageId, DatabaseUser user, CancellationToken ct);
    Task<List<TagDto>> ListTagsAsync(string ownerId, CancellationToken ct);
    Task<PagedResult<SearchResultDto>> SearchAsync(string ownerId, string term, IEnumerable<string> tags, string headlineOptions, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<SearchResultDto>> SearchByTagsAsync(string ownerId, IEnumerable<string> tags, int page, int pageSize, CancellationToken ct);
}
