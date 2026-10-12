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
public sealed class SharesRepository(NpgsqlDataSource db)
{
    private const string ShareColumns =
        "s.id, s.note_id, s.owner_id, s.owner_name, s.owner_email, s.recipient_id, s.recipient_email, s.recipient_name, s.permission, s.created_at";

    public async Task<List<NoteShareDto>> ListSharesAsync(string ownerId, Guid noteId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var shares = await connection.QueryAsync<NoteShare>(new CommandDefinition(
            $"SELECT {ShareColumns} FROM note_shares s WHERE s.note_id = @NoteId AND s.owner_id = @OwnerId ORDER BY s.created_at",
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
            "SELECT 1 FROM notes WHERE id = @NoteId AND owner_id = @OwnerId FOR UPDATE",
            new { NoteId = noteId, OwnerId = ownerId }, transaction, cancellationToken: ct));
        if (owned is null)
        {
            return (UpsertShareOutcome.NotFound, null);
        }

        var existing = (await connection.QueryAsync<NoteShare>(new CommandDefinition(
            $"SELECT {ShareColumns} FROM note_shares s WHERE s.note_id = @NoteId AND s.owner_id = @OwnerId",
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

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO note_shares (id, note_id, owner_id, owner_name, owner_email, recipient_id, recipient_email, recipient_name, permission, created_at)
            VALUES (@Id, @NoteId, @OwnerId, @OwnerName, @OwnerEmail, @RecipientId, @RecipientEmail, @RecipientName, @Permission, @CreatedAt)
            ON CONFLICT (id) DO UPDATE
               SET owner_name = excluded.owner_name,
                   owner_email = excluded.owner_email,
                   recipient_id = excluded.recipient_id,
                   recipient_email = excluded.recipient_email,
                   recipient_name = excluded.recipient_name,
                   permission = excluded.permission
            """, share, transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);
        return (created ? UpsertShareOutcome.Created : UpsertShareOutcome.Updated, share.ToDto());
    }

    public async Task<NoteShareDto?> UpdatePermissionAsync(string ownerId, Guid noteId, Guid shareId, SharePermission permission, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var share = await connection.QuerySingleOrDefaultAsync<NoteShare>(new CommandDefinition($"""
            UPDATE note_shares s SET permission = @Permission
             WHERE s.id = @ShareId AND s.note_id = @NoteId AND s.owner_id = @OwnerId
            RETURNING {ShareColumns}
            """, new { ShareId = shareId, NoteId = noteId, OwnerId = ownerId, Permission = permission }, cancellationToken: ct));
        return share?.ToDto();
    }

    /// <summary>The owner removes someone; a recipient may also remove their own share to leave a note.</summary>
    public async Task<bool> RemoveShareAsync(Guid noteId, Guid shareId, DatabaseUser user, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition($"""
            DELETE FROM note_shares s
             WHERE s.id = @ShareId AND s.note_id = @NoteId
               AND (s.owner_id = @UserId OR {NotesRepository.IsRecipient})
            """, new { ShareId = shareId, NoteId = noteId, user.UserId, user.Email }, cancellationToken: ct)) > 0;
    }

    /// <summary>Removes the caller's own shares of the given notes. Returns the number of notes they no longer have access to.</summary>
    public async Task<int> LeaveAsync(IEnumerable<Guid> noteIds, DatabaseUser user, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition($"""
            WITH removed AS (
                DELETE FROM note_shares s
                 WHERE s.note_id = ANY(@NoteIds) AND s.owner_id <> @UserId AND {NotesRepository.IsRecipient}
                RETURNING s.note_id
            )
            SELECT count(DISTINCT note_id)::integer FROM removed
            """, new { NoteIds = noteIds.ToArray(), user.UserId, user.Email }, cancellationToken: ct));
    }

    /// <summary>Notes the caller owns and has shared with at least one person, most recently updated first.</summary>
    public async Task<PagedResult<SharedNoteDto>> SharedByMeAsync(string ownerId, int page, int pageSize, CancellationToken ct)
    {
        const string filter = "FROM notes n WHERE n.owner_id = @OwnerId AND EXISTS (SELECT 1 FROM note_shares s WHERE s.note_id = n.id)";

        await using var connection = await db.OpenConnectionAsync(ct);
        long total;
        List<NoteSummaryRow> rows;
        using (var results = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT count(*) {filter};

            SELECT {NoteSummaryRow.Columns}
              {filter}
             ORDER BY n.updated_at DESC
             LIMIT @Limit OFFSET @Offset
            """, new { OwnerId = ownerId, Limit = pageSize, Offset = NotesRepository.Offset(page, pageSize) }, cancellationToken: ct)))
        {
            total = await results.ReadSingleAsync<long>();
            rows = (await results.ReadAsync<NoteSummaryRow>()).AsList();
        }

        var shares = (await connection.QueryAsync<NoteShare>(new CommandDefinition(
                $"SELECT {ShareColumns} FROM note_shares s WHERE s.owner_id = @OwnerId AND s.note_id = ANY(@NoteIds) ORDER BY s.created_at",
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
        const string mine = $"FROM note_shares s WHERE s.note_id = n.id AND {NotesRepository.IsRecipient}";
        const string filter = $"FROM notes n WHERE n.owner_id <> @UserId AND EXISTS (SELECT 1 {mine})";

        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT count(*) {filter};

            SELECT {NoteSummaryRow.Columns},
                   (SELECT bool_or(s.permission = {(int)SharePermission.Contributor}) {mine}) AS is_contributor,
                   (SELECT coalesce(s.owner_name, s.owner_email) {mine}
                       AND coalesce(s.owner_name, s.owner_email) <> '' ORDER BY s.created_at LIMIT 1) AS shared_by,
                   (SELECT min(s.created_at) {mine}) AS shared_at
              {filter}
             ORDER BY n.updated_at DESC
             LIMIT @Limit OFFSET @Offset
            """, new { user.UserId, user.Email, Limit = pageSize, Offset = NotesRepository.Offset(page, pageSize) }, cancellationToken: ct));

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
