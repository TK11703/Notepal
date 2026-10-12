using Dapper;
using Npgsql;
using Notepal.Contracts;

namespace Notepal.Database;

public enum UpsertShareOutcome
{
    NotFound,
    TooMany,
    Created,
    Updated,
}

public sealed record ShareDetails(string Email, string? RecipientId, string? RecipientName, SharePermission Permission, string? OwnerName, string? OwnerEmail);

/// <summary>
/// Data access for sharing. Managing shares is scoped to the note's owner (<c>owner_id</c>); recipients can only see
/// and remove the shares addressed to them.
/// </summary>
public sealed class SharesRepository(NpgsqlDataSource db, ISqlFileProvider sql) : ISharesRepository
{
    public async Task<List<NoteShareDto>> ListSharesAsync(string ownerId, Guid noteId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var shares = await connection.QueryAsync<NoteShare>(new CommandDefinition(
            sql.Get("Commands/Sharing/ListShares.sql"),
            new { NoteId = noteId, OwnerId = ownerId }, cancellationToken: ct));
        return shares.Select(s => s.ToDto()).ToList();
    }

    /// <summary>Shares a note; sharing with someone again (by address or by directory id) updates their existing share.</summary>
    public async Task<(UpsertShareOutcome Outcome, NoteShareDto? Share)> UpsertShareAsync(
        string ownerId, Guid noteId, ShareDetails details, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Locking the note serializes concurrent changes to its shares.
        var owned = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            sql.Get("Commands/Sharing/LockOwnedNote.sql"),
            new { NoteId = noteId, OwnerId = ownerId }, transaction, cancellationToken: ct));
        if (owned is null)
        {
            return (UpsertShareOutcome.NotFound, null);
        }

        var existing = (await connection.QueryAsync<NoteShare>(new CommandDefinition(
            sql.Get("Commands/Sharing/GetExistingShares.sql"),
            new { NoteId = noteId, OwnerId = ownerId }, transaction, cancellationToken: ct))).AsList();

        var share = existing.FirstOrDefault(s => s.RecipientEmail == details.Email)
            ?? (details.RecipientId is null ? null : existing.FirstOrDefault(s => s.RecipientId == details.RecipientId));
        var created = share is null;
        if (share is null)
        {
            if (existing.Count >= ShareLimits.MaxSharesPerNote)
            {
                return (UpsertShareOutcome.TooMany, null);
            }

            share = new NoteShare { Id = Guid.NewGuid(), NoteId = noteId, OwnerId = ownerId, CreatedAt = DateTimeOffset.UtcNow };
        }

        share.RecipientEmail = details.Email;
        share.RecipientId = details.RecipientId ?? share.RecipientId;
        share.RecipientName = details.RecipientName ?? share.RecipientName;
        share.Permission = details.Permission;
        share.OwnerName = details.OwnerName;
        share.OwnerEmail = details.OwnerEmail;

        await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Sharing/UpsertShare.sql"), share, transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);
        return (created ? UpsertShareOutcome.Created : UpsertShareOutcome.Updated, share.ToDto());
    }

    public async Task<NoteShareDto?> UpdatePermissionAsync(string ownerId, Guid noteId, Guid shareId, SharePermission permission, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var share = await connection.QuerySingleOrDefaultAsync<NoteShare>(new CommandDefinition(
            sql.Get("Commands/Sharing/UpdatePermission.sql"),
            new { ShareId = shareId, NoteId = noteId, OwnerId = ownerId, Permission = permission }, cancellationToken: ct));
        return share?.ToDto();
    }

    /// <summary>The owner removes someone; a recipient may also remove their own share to leave a note.</summary>
    public async Task<bool> RemoveShareAsync(Guid noteId, Guid shareId, DatabaseUser user, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Sharing/RemoveShare.sql"),
            new { ShareId = shareId, NoteId = noteId, user.UserId, user.Email }, cancellationToken: ct)) > 0;
    }

    /// <summary>Removes the caller's own shares of the given notes. Returns the number of notes they no longer have access to.</summary>
    public async Task<int> LeaveAsync(IEnumerable<Guid> noteIds, DatabaseUser user, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql.Get("Commands/Sharing/LeaveSharedNotes.sql"),
            new { NoteIds = noteIds.ToArray(), user.UserId, user.Email }, cancellationToken: ct));
    }

    /// <summary>Notes the caller owns and has shared with at least one person, most recently updated first.</summary>
    public async Task<PagedResult<SharedNoteDto>> SharedByMeAsync(string ownerId, int page, int pageSize, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        long total;
        List<NoteSummaryRow> rows;
        using (var results = await connection.QueryMultipleAsync(new CommandDefinition(
            sql.Get("Commands/Sharing/SharedByMe.sql"),
            new { OwnerId = ownerId, Limit = pageSize, Offset = NotesRepository.Offset(page, pageSize) }, cancellationToken: ct)))
        {
            total = await results.ReadSingleAsync<long>();
            rows = (await results.ReadAsync<NoteSummaryRow>()).AsList();
        }

        var shares = (await connection.QueryAsync<NoteShare>(new CommandDefinition(
                sql.Get("Commands/Sharing/GetSharesForNotes.sql"),
                new { OwnerId = ownerId, NoteIds = rows.Select(r => r.Id).ToArray() }, cancellationToken: ct)))
            .ToLookup(s => s.NoteId);

        var items = rows
            .Where(r => shares[r.Id].Any())
            .Select(r => new SharedNoteDto(
                r.ToDto(),
                NoteRole.Owner,
                null,
                shares[r.Id].Max(s => s.CreatedAt),
                shares[r.Id].Select(s => s.ToDto()).ToList()))
            .ToList();

        return new PagedResult<SharedNoteDto>(items, (int)total, page, pageSize);
    }

    /// <summary>Notes other people have shared with the caller, most recently updated first.</summary>
    public async Task<PagedResult<SharedNoteDto>> SharedWithMeAsync(DatabaseUser user, int page, int pageSize, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            sql.Get("Commands/Sharing/SharedWithMe.sql"),
            new { user.UserId, user.Email, Contributor = SharePermission.Contributor, Limit = pageSize, Offset = NotesRepository.Offset(page, pageSize) }, cancellationToken: ct));

        var total = await results.ReadSingleAsync<long>();
        var items = (await results.ReadAsync<SharedWithMeRow>())
            .Select(r => new SharedNoteDto(r.ToDto(), r.IsContributor ? NoteRole.Contributor : NoteRole.Reader, r.SharedBy, r.SharedAt, []))
            .ToList();

        return new PagedResult<SharedNoteDto>(items, (int)total, page, pageSize);
    }

    private sealed class SharedWithMeRow : NoteSummaryRow
    {
        public bool IsContributor { get; set; }
        public string? SharedBy { get; set; }
        public DateTimeOffset SharedAt { get; set; }
    }
}
