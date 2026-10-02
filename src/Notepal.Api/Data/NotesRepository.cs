using Dapper;
using Npgsql;
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

/// <summary>
/// Data access for user requests. Every statement is filtered on <c>owner_id</c>, so a caller can never read or
/// change another user's rows even when it guesses their ids.
/// </summary>
public sealed class NotesRepository(NpgsqlDataSource db)
{
    private const string PageColumns =
        "id, note_id, owner_id, page_number, file_name, content_type, size_bytes, status, attempts, error, extracted_text, edited_text, updated_at";

    private const string SearchConfig = "english";

    public async Task<PagedResult<NoteSummaryDto>> ListNotesAsync(string ownerId, int page, int pageSize, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT count(*) FROM notes WHERE owner_id = @OwnerId;

            SELECT n.id, n.title, n.created_at, n.updated_at,
                   coalesce((SELECT array_agg(p.status) FROM pages p WHERE p.note_id = n.id), '{}'::integer[]) AS statuses,
                   (SELECT left(coalesce(p.edited_text, p.extracted_text), 240)
                      FROM pages p WHERE p.note_id = n.id ORDER BY p.page_number LIMIT 1) AS preview
              FROM notes n
             WHERE n.owner_id = @OwnerId
             ORDER BY n.updated_at DESC
             LIMIT @Limit OFFSET @Offset
            """, new { OwnerId = ownerId, Limit = pageSize, Offset = Offset(page, pageSize) }, cancellationToken: ct));

        var total = await results.ReadSingleAsync<long>();
        var items = (await results.ReadAsync<NoteSummaryRow>())
            .Select(r =>
            {
                var statuses = r.Statuses.Select(s => (ProcessingStatus)s).ToList();
                return new NoteSummaryDto(r.Id, r.Title, r.CreatedAt, r.UpdatedAt, statuses.Count, Mapping.AggregateStatus(statuses), r.Preview);
            })
            .ToList();

        return new PagedResult<NoteSummaryDto>(items, (int)total, page, pageSize);
    }

    public async Task<NoteDto?> GetNoteAsync(string ownerId, Guid noteId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT id, owner_id, title, created_at, updated_at FROM notes WHERE id = @NoteId AND owner_id = @OwnerId;
            SELECT {PageColumns} FROM pages WHERE note_id = @NoteId AND owner_id = @OwnerId ORDER BY page_number;
            """, new { NoteId = noteId, OwnerId = ownerId }, cancellationToken: ct));

        var note = await results.ReadSingleOrDefaultAsync<Note>();
        return note?.ToDto(await results.ReadAsync<Page>());
    }

    public async Task<NoteDto> CreateNoteAsync(string ownerId, string title, IReadOnlyList<NewPage> files, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Note { Id = Guid.NewGuid(), OwnerId = ownerId, Title = title, CreatedAt = now, UpdatedAt = now };
        var pages = files.Select((f, i) => new Page
        {
            Id = Guid.NewGuid(),
            NoteId = note.Id,
            OwnerId = ownerId,
            PageNumber = i + 1,
            FileName = f.FileName,
            ContentType = f.ContentType,
            SizeBytes = f.Data.LongLength,
            Status = ProcessingStatus.Pending,
            UpdatedAt = now,
        }).ToList();
        var contents = pages.Select((p, i) => new PageContent { PageId = p.Id, Data = files[i].Data }).ToList();

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO notes (id, owner_id, title, created_at, updated_at)
            VALUES (@Id, @OwnerId, @Title, @CreatedAt, @UpdatedAt)
            """, note, transaction, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pages (id, note_id, owner_id, page_number, file_name, content_type, size_bytes, status, attempts, updated_at)
            VALUES (@Id, @NoteId, @OwnerId, @PageNumber, @FileName, @ContentType, @SizeBytes, @Status, @Attempts, @UpdatedAt)
            """, pages, transaction, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO page_contents (page_id, data) VALUES (@PageId, @Data)", contents, transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);
        return note.ToDto(pages);
    }

    public async Task<NoteDto?> RenameNoteAsync(string ownerId, Guid noteId, string title, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE notes SET title = @Title, updated_at = @Now WHERE id = @NoteId AND owner_id = @OwnerId",
            new { NoteId = noteId, OwnerId = ownerId, Title = title, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));

        return updated == 0 ? null : await GetNoteAsync(ownerId, noteId, ct);
    }

    public async Task<bool> DeleteNoteAsync(string ownerId, Guid noteId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM notes WHERE id = @NoteId AND owner_id = @OwnerId",
            new { NoteId = noteId, OwnerId = ownerId }, cancellationToken: ct)) > 0;
    }

    public async Task<PageOriginal?> GetOriginalAsync(string ownerId, Guid noteId, Guid pageId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PageOriginal>(new CommandDefinition("""
            SELECT p.file_name, p.content_type, c.data
              FROM pages p
              JOIN page_contents c ON c.page_id = p.id
             WHERE p.id = @PageId AND p.note_id = @NoteId AND p.owner_id = @OwnerId
            """, new { PageId = pageId, NoteId = noteId, OwnerId = ownerId }, cancellationToken: ct));
    }

    public async Task<PageDto?> UpdatePageTextAsync(string ownerId, Guid noteId, Guid pageId, string text, CancellationToken ct)
    {
        // Saving text identical to the extraction clears the correction so future re-processing shows through.
        await using var connection = await db.OpenConnectionAsync(ct);
        var page = await connection.QuerySingleOrDefaultAsync<Page>(new CommandDefinition($"""
            WITH page AS (
                UPDATE pages
                   SET edited_text = CASE WHEN extracted_text = @Text THEN NULL ELSE @Text END,
                       updated_at = @Now
                 WHERE id = @PageId AND note_id = @NoteId AND owner_id = @OwnerId
                RETURNING {PageColumns}
            ), note AS (
                UPDATE notes SET updated_at = @Now
                 WHERE id = @NoteId AND owner_id = @OwnerId AND EXISTS (SELECT 1 FROM page)
            )
            SELECT {PageColumns} FROM page
            """, new { PageId = pageId, NoteId = noteId, OwnerId = ownerId, Text = text, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));

        return page?.ToDto();
    }

    public async Task<(ReprocessOutcome Outcome, PageDto? Page)> ResetForReprocessingAsync(
        string ownerId, Guid noteId, Guid pageId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition($"""
            UPDATE pages
               SET status = @Pending, attempts = 0, error = NULL, updated_at = @Now
             WHERE id = @PageId AND note_id = @NoteId AND owner_id = @OwnerId AND status NOT IN (@Pending, @Processing)
            RETURNING {PageColumns};

            SELECT EXISTS (SELECT 1 FROM pages WHERE id = @PageId AND note_id = @NoteId AND owner_id = @OwnerId);
            """, new
            {
                PageId = pageId,
                NoteId = noteId,
                OwnerId = ownerId,
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

    /// <summary>
    /// Full text search (stemmed, ranked) over corrected/extracted text and note titles, plus a substring match so
    /// partial words still find something.
    /// </summary>
    public async Task<PagedResult<SearchResultDto>> SearchAsync(
        string ownerId, string term, string headlineOptions, int page, int pageSize, CancellationToken ct)
    {
        const string matches = $"""
              FROM pages p
              JOIN notes n ON n.id = p.note_id AND n.owner_id = p.owner_id
             CROSS JOIN websearch_to_tsquery('{SearchConfig}', @Term) AS q(query)
             WHERE p.owner_id = @OwnerId
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
                   n.updated_at
            {matches}
             ORDER BY ts_rank(p.search_vector, q.query) + ts_rank(n.title_search, q.query) * 2 DESC,
                      n.updated_at DESC,
                      p.page_number
             LIMIT @Limit OFFSET @Offset
            """, new
            {
                OwnerId = ownerId,
                Term = term,
                Pattern = $"%{EscapeLike(term)}%",
                HeadlineOptions = headlineOptions,
                Limit = pageSize,
                Offset = Offset(page, pageSize),
            }, cancellationToken: ct));

        var total = await results.ReadSingleAsync<long>();
        var items = (await results.ReadAsync<SearchRow>())
            .Select(r => new SearchResultDto(r.NoteId, r.Title, r.PageId, r.PageNumber, r.Snippet, r.UpdatedAt))
            .ToList();

        return new PagedResult<SearchResultDto>(items, (int)total, page, pageSize);
    }

    private static long Offset(int page, int pageSize) => (long)(page - 1) * pageSize;

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class NoteSummaryRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = null!;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int[] Statuses { get; set; } = [];
        public string? Preview { get; set; }
    }

    private sealed class SearchRow
    {
        public Guid NoteId { get; set; }
        public string Title { get; set; } = null!;
        public Guid PageId { get; set; }
        public int PageNumber { get; set; }
        public string Snippet { get; set; } = null!;
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
