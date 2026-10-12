using Notepal.Contracts;

namespace Notepal.Database;

/// <summary>Unscoped background extraction persistence; never use from user-facing endpoints.</summary>
public interface IPageWorkRepository
{
    Task<List<Guid>> GetUnfinishedPageIdsAsync(CancellationToken ct);
    Task<PageWork?> TryStartAsync(Guid pageId, CancellationToken ct);
    Task CompleteAsync(Guid pageId, string text, CancellationToken ct);
    Task FailAsync(Guid pageId, ProcessingStatus status, string error, CancellationToken ct);
}
