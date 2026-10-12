using Notepal.Contracts;

namespace Notepal.Database;

public interface ISharesRepository
{
    Task<List<NoteShareDto>> ListSharesAsync(string ownerId, Guid noteId, CancellationToken ct);
    Task<(UpsertShareOutcome Outcome, NoteShareDto? Share)> UpsertShareAsync(string ownerId, Guid noteId, ShareDetails details, CancellationToken ct);
    Task<NoteShareDto?> UpdatePermissionAsync(string ownerId, Guid noteId, Guid shareId, SharePermission permission, CancellationToken ct);
    Task<bool> RemoveShareAsync(Guid noteId, Guid shareId, DatabaseUser user, CancellationToken ct);
    Task<int> LeaveAsync(IEnumerable<Guid> noteIds, DatabaseUser user, CancellationToken ct);
    Task<PagedResult<SharedNoteDto>> SharedByMeAsync(string ownerId, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<SharedNoteDto>> SharedWithMeAsync(DatabaseUser user, int page, int pageSize, CancellationToken ct);
}
