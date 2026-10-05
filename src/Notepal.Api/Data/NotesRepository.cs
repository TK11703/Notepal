using Dapper;
using Npgsql;
using Notepal.Api.Auth;
using Notepal.Api.Endpoints;
using Notepal.Shared;

namespace Notepal.Api.Data;

public sealed record NewPage(string FileName, string ContentType, byte[] Data);

public sealed class PageOriginal
{
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public byte[] Data { get; set; } = null!;
}

public enum ReprocessOutcome
{
    NotFound,
    AlreadyProcessing,
    Queued,
}

public enum AddPagesOutcome
{
    NotFound,
    TooMany,
    Added,
}

public sealed record AddPagesResult(AddPagesOutcome Outcome, NoteDto? Note = null, IReadOnlyList<Guid>? PageIds = null, int Remaining = 0);

/// <summary>Columns and row type of a note in a list (<see cref="NoteSummaryDto"/>). The note table must be aliased <c>n</c>.</summary>
internal class NoteSummaryRow
{
    public const string Columns = """
        n.id, n.title, n.created_at, n.updated_at, n.tags,
        coalesce((SELECT array_agg(p.status) FROM pages p WHERE p.note_id = n.id), '{}'::integer[]) AS statuses,
        (SELECT left(coalesce(p.edited_text, p.extracted_text), 240)
           FROM pages p WHERE p.note_id = n.id ORDER BY p.page_number LIMIT 1) AS preview
        """;

    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string[] Tags { get; set; } = [];
    public int[] Statuses { get; set; } = [];
    public string? Preview { get; set; }

    public NoteSummaryDto ToDto()
    {
        var statuses = Statuses.Select(s => (ProcessingStatus)s).ToList();
        return new NoteSummaryDto(Id, Title, CreatedAt, UpdatedAt, statuses.Count, Mapping.AggregateStatus(statuses), Preview, Tags);
    }
}

/// <summary>
/// Data access for user requests. Lists and search only cover the caller's own notes (<c>owner_id</c>); single note
/// statements are additionally allowed for people the note is shared with, and every statement carries that check so a
/// caller can never read or change rows they have no access to, even when they guess the ids.
/// </summary>
public sealed class NotesRepository(NpgsqlDataSource db)
{
    private const string PageColumns =
        "p.id, p.note_id, p.owner_id, p.page_number, p.file_name, p.content_type, p.size_bytes, p.status, p.attempts, p.error, p.extracted_text, p.edited_text, p.updated_at";

    private const string SearchConfig = "english";

    /// <summary>Matches shares (aliased <c>s</c>) addressed to the caller by object id or, for shares created by e-mail address, by sign-in address.</summary>
    internal const string IsRecipient = "(s.recipient_id = @UserId OR s.recipient_email = @Email)";

    /// <summary>The note (aliased <c>n</c>) is owned by or shared with the caller.</summary>
    private const string Visible =
        $"(n.owner_id = @UserId OR EXISTS (SELECT 1 FROM note_shares s WHERE s.note_id = n.id AND {IsRecipient}))";

    /// <summary>The note (aliased <c>n</c>) is owned by the caller or shared with them as a contributor.</summary>
    private static readonly string Editable =
        $"(n.owner_id = @UserId OR EXISTS (SELECT 1 FROM note_shares s WHERE s.note_id = n.id AND s.permission = {(int)SharePermission.Contributor} AND {IsRecipient}))";

    /// <summary>Resolves the caller's access to a note, or <c>null</c> when the note does not exist or is not visible to them.</summary>
    public async Task<NoteAccess?> GetAccessAsync(Guid noteId, ICurrentUser user, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<AccessRow>(new CommandDefinition($"""
            SELECT n.owner_id, s.permission, s.owner_name, s.owner_email
              FROM notes n
              LEFT JOIN note_shares s ON s.note_id = n.id AND {IsRecipient}
             WHERE n.id = @NoteId
             ORDER BY s.created_at
            """, new { NoteId = noteId, user.UserId, user.Email }, cancellationToken: ct))).AsList();

        if (rows.Count == 0)
        {
            return null;
        }

        if (rows[0].OwnerId == user.UserId)
        {
            return new NoteAccess(NoteRole.Owner, null);
        }

        var shares = rows.Where(r => r.Permission is not null).ToList();
        if (shares.Count == 0)
        {
            return null;
        }

        // A person can match more than one share (by id and by address); the most generous permission wins.
        var contributor = shares.Any(s => s.Permission == SharePermission.Contributor);
        var sharer = shares.Select(s => s.OwnerName ?? s.OwnerEmail).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        return new NoteAccess(contributor ? NoteRole.Contributor : NoteRole.Reader, sharer);
    }

    /// <summary>The caller's own notes carrying all of <paramref name="tags"/>, most recently updated first.</summary>
    public async Task<PagedResult<NoteSummaryDto>> ListNotesAsync(string ownerId, IEnumerable<string> tags, int page, int pageSize, CancellationToken ct)
    {
        const string filter = "FROM notes n WHERE n.owner_id = @OwnerId AND n.tags @> @Tags";

        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT count(*) {filter};

            SELECT {NoteSummaryRow.Columns}
              {filter}
             ORDER BY n.updated_at DESC
             LIMIT @Limit OFFSET @Offset
            """, new { OwnerId = ownerId, Tags = tags.ToArray(), Limit = pageSize, Offset = Offset(page, pageSize) }, cancellationToken: ct));

        var total = await results.ReadSingleAsync<long>();
        var items = (await results.ReadAsync<NoteSummaryRow>()).Select(r => r.ToDto()).ToList();
        return new PagedResult<NoteSummaryDto>(items, (int)total, page, pageSize);
    }

    public async Task<NoteDto?> GetNoteAsync(Guid noteId, ICurrentUser user, NoteAccess access, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT n.id, n.owner_id, n.title, n.created_at, n.updated_at, n.tags
              FROM notes n
             WHERE n.id = @NoteId AND {Visible};

            SELECT {PageColumns}
              FROM pages p
              JOIN notes n ON n.id = p.note_id
             WHERE p.note_id = @NoteId AND {Visible};
            """, new { NoteId = noteId, user.UserId, user.Email }, cancellationToken: ct));

        var note = await results.ReadSingleOrDefaultAsync<Note>();
        return note?.ToDto(await results.ReadAsync<Page>(), access);
    }

    public async Task<NoteDto> CreateNoteAsync(string ownerId, string title, IEnumerable<string> tags, IReadOnlyList<NewPage> files, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Note { Id = Guid.NewGuid(), OwnerId = ownerId, Title = title, CreatedAt = now, UpdatedAt = now, Tags = tags.ToArray() };

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO notes (id, owner_id, title, created_at, updated_at, tags)
            VALUES (@Id, @OwnerId, @Title, @CreatedAt, @UpdatedAt, @Tags)
            """, note, transaction, cancellationToken: ct));

        var pages = await InsertPagesAsync(connection, transaction, note.Id, ownerId, 1, files, now, ct);

        await transaction.CommitAsync(ct);
        return note.ToDto(pages);
    }

    public async Task<NoteDto?> RenameNoteAsync(Guid noteId, ICurrentUser user, NoteAccess access, string title, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE notes n SET title = @Title, updated_at = @Now WHERE n.id = @NoteId AND {Editable}",
            new { NoteId = noteId, user.UserId, user.Email, Title = title, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));

        return updated == 0 ? null : await GetNoteAsync(noteId, user, access, ct);
    }

    public async Task<NoteDto?> SetTagsAsync(Guid noteId, ICurrentUser user, NoteAccess access, IEnumerable<string> tags, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE notes n SET tags = @Tags, updated_at = @Now WHERE n.id = @NoteId AND {Editable}",
            new { NoteId = noteId, user.UserId, user.Email, Tags = tags.ToArray(), Now = DateTimeOffset.UtcNow }, cancellationToken: ct));

        return updated == 0 ? null : await GetNoteAsync(noteId, user, access, ct);
    }

    /// <summary>Appends pages at the end of a note. Pages always belong to the note's owner, also when a contributor adds them.</summary>
    public async Task<AddPagesResult> AddPagesAsync(Guid noteId, ICurrentUser user, NoteAccess access, IReadOnlyList<NewPage> files, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Locking the note serialises concurrent uploads so page numbers and the page limit stay consistent.
        var ownerId = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            $"SELECT n.owner_id FROM notes n WHERE n.id = @NoteId AND {Editable} FOR UPDATE",
            new { NoteId = noteId, user.UserId, user.Email }, transaction, cancellationToken: ct));
        if (ownerId is null)
        {
            return new AddPagesResult(AddPagesOutcome.NotFound);
        }

        var (count, last) = await connection.QuerySingleAsync<(int Count, int Last)>(new CommandDefinition(
            "SELECT count(*)::integer, coalesce(max(page_number), 0) FROM pages WHERE note_id = @NoteId",
            new { NoteId = noteId }, transaction, cancellationToken: ct));

        var remaining = UploadLimits.MaxFilesPerNote - count;
        if (files.Count > remaining)
        {
            return new AddPagesResult(AddPagesOutcome.TooMany, Remaining: Math.Max(0, remaining));
        }

        var pages = await InsertPagesAsync(connection, transaction, noteId, ownerId, last + 1, files, now, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE notes SET updated_at = @Now WHERE id = @NoteId", new { NoteId = noteId, Now = now }, transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);

        var note = await GetNoteAsync(noteId, user, access, ct);
        return note is null
            ? new AddPagesResult(AddPagesOutcome.NotFound)
            : new AddPagesResult(AddPagesOutcome.Added, note, pages.Select(p => p.Id).ToList());
    }

    public async Task<bool> DeleteNoteAsync(string ownerId, Guid noteId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM notes WHERE id = @NoteId AND owner_id = @OwnerId",
            new { NoteId = noteId, OwnerId = ownerId }, cancellationToken: ct)) > 0;
    }

    public async Task<PageOriginal?> GetOriginalAsync(Guid noteId, Guid pageId, ICurrentUser user, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PageOriginal>(new CommandDefinition($"""
            SELECT p.file_name, p.content_type, c.data
              FROM pages p
              JOIN page_contents c ON c.page_id = p.id
              JOIN notes n ON n.id = p.note_id
             WHERE p.id = @PageId AND p.note_id = @NoteId AND {Visible}
            """, new { PageId = pageId, NoteId = noteId, user.UserId, user.Email }, cancellationToken: ct));
    }

    public async Task<PageDto?> UpdatePageTextAsync(Guid noteId, Guid pageId, ICurrentUser user, string text, CancellationToken ct)
    {
        // Saving text identical to the extraction clears the correction so future re-processing shows through.
        await using var connection = await db.OpenConnectionAsync(ct);
        var page = await connection.QuerySingleOrDefaultAsync<Page>(new CommandDefinition($"""
            WITH page AS (
                UPDATE pages p
                   SET edited_text = CASE WHEN p.extracted_text = @Text THEN NULL ELSE @Text END,
                       updated_at = @Now
                  FROM notes n
                 WHERE p.id = @PageId AND p.note_id = @NoteId AND n.id = p.note_id AND {Editable}
                RETURNING {PageColumns}
            ), note AS (
                UPDATE notes SET updated_at = @Now
                 WHERE id = @NoteId AND EXISTS (SELECT 1 FROM page)
            )
            SELECT * FROM page
            """, new { PageId = pageId, NoteId = noteId, user.UserId, user.Email, Text = text, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));

        return page?.ToDto();
    }

    public async Task<(ReprocessOutcome Outcome, PageDto? Page)> ResetForReprocessingAsync(
        Guid noteId, Guid pageId, ICurrentUser user, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition($"""
            UPDATE pages p
               SET status = @Pending, attempts = 0, error = NULL, updated_at = @Now
              FROM notes n
             WHERE p.id = @PageId AND p.note_id = @NoteId AND n.id = p.note_id AND {Editable}
               AND p.status NOT IN (@Pending, @Processing)
            RETURNING {PageColumns};

            SELECT EXISTS (
                SELECT 1 FROM pages p JOIN notes n ON n.id = p.note_id
                 WHERE p.id = @PageId AND p.note_id = @NoteId AND {Editable});
            """, new
            {
                PageId = pageId,
                NoteId = noteId,
                user.UserId,
                user.Email,
                Pending = ProcessingStatus.Pending,
                Processing = ProcessingStatus.Processing,
                Now = DateTimeOffset.UtcNow,
            }, cancellationToken: ct));

        var page = await results.ReadSingleOrDefaultAsync<Page>();
        var exists = await results.ReadSingleAsync<bool>();

        if (page is not null)
        {
            return (ReprocessOutcome.Queued, page.ToDto());
        }

        return (exists ? ReprocessOutcome.AlreadyProcessing : ReprocessOutcome.NotFound, null);
    }

    /// <summary>All tags the caller has used on their own notes, most used first.</summary>
    public async Task<List<TagDto>> ListTagsAsync(string ownerId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<(string Name, int Count)>(new CommandDefinition("""
            SELECT t.tag, count(*)::integer
              FROM notes n
             CROSS JOIN unnest(n.tags) AS t(tag)
             WHERE n.owner_id = @OwnerId
             GROUP BY t.tag
            """, new { OwnerId = ownerId }, cancellationToken: ct));

        return rows
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new TagDto(t.Name, t.Count))
            .ToList();
    }

    /// <summary>
    /// Full text search (stemmed, ranked) over the caller's corrected/extracted text and note titles, plus a substring
    /// match so partial words still find something. Only notes carrying all of <paramref name="tags"/> are searched.
    /// </summary>
    public async Task<PagedResult<SearchResultDto>> SearchAsync(
        string ownerId, string term, IEnumerable<string> tags, string headlineOptions, int page, int pageSize, CancellationToken ct)
    {
        const string matches = $"""
              FROM pages p
              JOIN notes n ON n.id = p.note_id AND n.owner_id = p.owner_id
             CROSS JOIN websearch_to_tsquery('{SearchConfig}', @Term) AS q(query)
             WHERE p.owner_id = @OwnerId
               AND n.tags @> @Tags
               AND (p.search_vector @@ q.query
                    OR n.title_search @@ q.query
                    OR n.title ILIKE @Pattern ESCAPE '\'
                    OR coalesce(p.edited_text, p.extracted_text, '') ILIKE @Pattern ESCAPE '\')
            """;

        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT count(*) {matches};

            SELECT p.note_id, n.title, p.id AS page_id, p.page_number,
                   ts_headline('{SearchConfig}', coalesce(p.edited_text, p.extracted_text, ''), q.query, @HeadlineOptions) AS snippet,
                   n.updated_at, n.tags
            {matches}
             ORDER BY ts_rank(p.search_vector, q.query) + ts_rank(n.title_search, q.query) * 2 DESC,
                      n.updated_at DESC,
                      p.page_number
             LIMIT @Limit OFFSET @Offset
            """, new
            {
                OwnerId = ownerId,
                Term = term,
                Tags = tags.ToArray(),
                Pattern = $"%{EscapeLike(term)}%",
                HeadlineOptions = headlineOptions,
                Limit = pageSize,
                Offset = Offset(page, pageSize),
            }, cancellationToken: ct));

        var total = await results.ReadSingleAsync<long>();
        var items = (await results.ReadAsync<SearchRow>())
            .Select(r => new SearchResultDto(r.NoteId, r.Title, r.PageId, r.PageNumber, r.Snippet, r.UpdatedAt, r.Tags))
            .ToList();

        return new PagedResult<SearchResultDto>(items, (int)total, page, pageSize);
    }

    /// <summary>Filtering by tags without search terms: one result per matching note, newest first.</summary>
    public async Task<PagedResult<SearchResultDto>> SearchByTagsAsync(string ownerId, IEnumerable<string> tags, int page, int pageSize, CancellationToken ct)
    {
        var notes = await ListNotesAsync(ownerId, tags, page, pageSize, ct);
        var items = notes.Items
            .Select(n => new SearchResultDto(n.Id, n.Title, null, null, n.Preview ?? "", n.UpdatedAt, n.Tags))
            .ToList();
        return new PagedResult<SearchResultDto>(items, notes.Total, page, pageSize);
    }

    internal static long Offset(int page, int pageSize) => (long)(page - 1) * pageSize;

    private static async Task<List<Page>> InsertPagesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid noteId, string ownerId, int firstNumber,
        IReadOnlyList<NewPage> files, DateTimeOffset now, CancellationToken ct)
    {
        var pages = files.Select((f, i) => new Page
        {
            Id = Guid.NewGuid(),
            NoteId = noteId,
            OwnerId = ownerId,
            PageNumber = firstNumber + i,
            FileName = f.FileName,
            ContentType = f.ContentType,
            SizeBytes = f.Data.LongLength,
            Status = ProcessingStatus.Pending,
            UpdatedAt = now,
        }).ToList();
        var contents = pages.Select((p, i) => new PageContent { PageId = p.Id, Data = files[i].Data }).ToList();

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pages (id, note_id, owner_id, page_number, file_name, content_type, size_bytes, status, attempts, updated_at)
            VALUES (@Id, @NoteId, @OwnerId, @PageNumber, @FileName, @ContentType, @SizeBytes, @Status, @Attempts, @UpdatedAt)
            """, pages, transaction, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO page_contents (page_id, data) VALUES (@PageId, @Data)", contents, transaction, cancellationToken: ct));

        return pages;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class AccessRow
    {
        public string OwnerId { get; set; } = null!;
        public SharePermission? Permission { get; set; }
        public string? OwnerName { get; set; }
        public string? OwnerEmail { get; set; }
    }

    private sealed class SearchRow
    {
        public Guid NoteId { get; set; }
        public string Title { get; set; } = null!;
        public Guid PageId { get; set; }
        public int PageNumber { get; set; }
        public string Snippet { get; set; } = null!;
        public DateTimeOffset UpdatedAt { get; set; }
        public string[] Tags { get; set; } = [];
    }
}
