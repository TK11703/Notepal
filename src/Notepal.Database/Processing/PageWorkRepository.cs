using Dapper;
using Npgsql;
using Notepal.Contracts;

namespace Notepal.Database;

public sealed class PageWork
{
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public int Attempts { get; set; }
    public byte[] Data { get; set; } = null!;
}

/// <summary>
/// Data access for the background extraction worker. It runs outside of any user request and only ever addresses
/// pages by id, so it is intentionally not owner-scoped and must never be used from an endpoint.
/// </summary>
public sealed class PageWorkRepository(NpgsqlDataSource db, ISqlFileProvider sql) : IPageWorkRepository
{
    public async Task<List<Guid>> GetUnfinishedPageIdsAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            sql.Get("Commands/Processing/GetUnfinishedPages.sql"),
            new { Pending = ProcessingStatus.Pending, Processing = ProcessingStatus.Processing }, cancellationToken: ct));
        return ids.AsList();
    }

    /// <summary>Marks a pending (or interrupted) page as processing and returns its content, or <c>null</c> if there is nothing to do.</summary>
    public async Task<PageWork?> TryStartAsync(Guid pageId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PageWork>(new CommandDefinition(
            sql.Get("Commands/Processing/StartPage.sql"), new
            {
                PageId = pageId,
                Pending = ProcessingStatus.Pending,
                Processing = ProcessingStatus.Processing,
                Now = DateTimeOffset.UtcNow,
            }, cancellationToken: ct));
    }

    public async Task CompleteAsync(Guid pageId, string text, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Processing/CompletePage.sql"),
            new { PageId = pageId, Status = ProcessingStatus.Completed, Text = text, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));
    }

    public async Task FailAsync(Guid pageId, ProcessingStatus status, string error, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Processing/FailPage.sql"),
            new { PageId = pageId, Status = status, Error = error, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));
    }
}
