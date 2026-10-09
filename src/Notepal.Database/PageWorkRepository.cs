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
public sealed class PageWorkRepository(NpgsqlDataSource db)
{
    public async Task<List<Guid>> GetUnfinishedPageIdsAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM pages WHERE status IN (@Pending, @Processing) ORDER BY updated_at",
            new { Pending = ProcessingStatus.Pending, Processing = ProcessingStatus.Processing }, cancellationToken: ct));
        return ids.AsList();
    }

    /// <summary>Marks a pending (or interrupted) page as processing and returns its content, or <c>null</c> if there is nothing to do.</summary>
    public async Task<PageWork?> TryStartAsync(Guid pageId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PageWork>(new CommandDefinition("""
            UPDATE pages p
               SET status = @Processing, attempts = p.attempts + 1, updated_at = @Now
              FROM page_contents c
             WHERE p.id = @PageId AND c.page_id = p.id AND p.status IN (@Pending, @Processing)
            RETURNING p.file_name, p.content_type, p.attempts, c.data
            """, new
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
            "UPDATE pages SET status = @Status, extracted_text = @Text, error = NULL, updated_at = @Now WHERE id = @PageId",
            new { PageId = pageId, Status = ProcessingStatus.Completed, Text = text, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));
    }

    public async Task FailAsync(Guid pageId, ProcessingStatus status, string error, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pages SET status = @Status, error = @Error, updated_at = @Now WHERE id = @PageId",
            new { PageId = pageId, Status = status, Error = error, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));
    }
}
