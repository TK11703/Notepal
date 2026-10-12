using Dapper;
using Npgsql;
using Notepal.Contracts;

namespace Notepal.Database;

/// <summary>Row type of a note in a list (<see cref="NoteSummaryDto"/>).</summary>
internal class NoteSummaryRow
{
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
        return new NoteSummaryDto(Id, Title, CreatedAt, UpdatedAt, statuses.Count, DatabaseMapping.AggregateStatus(statuses), Preview, Tags);
    }
}

/// <summary>
/// Data access for user requests. Lists and search only cover the caller's own notes (<c>owner_id</c>); single note
/// statements are additionally allowed for people the note is shared with, and every statement carries that check so a
/// caller can never read or change rows they have no access to, even when they guess the ids.
/// </summary>
public sealed class NotesRepository(NpgsqlDataSource db, ISqlFileProvider sql) : INotesRepository
{
    /// <summary>Resolves the caller's access to a note, or <c>null</c> when the note does not exist or is not visible to them.</summary>
    public async Task<NoteAccess?> GetAccessAsync(Guid noteId, DatabaseUser user, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<AccessRow>(new CommandDefinition(
            sql.Get("Commands/Notes/GetNoteAccess.sql"),
            new { NoteId = noteId, user.UserId, user.Email }, cancellationToken: ct))).AsList();

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
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            sql.Get("Commands/Notes/ListNotes.sql"),
            new { OwnerId = ownerId, Tags = tags.ToArray(), Limit = pageSize, Offset = Offset(page, pageSize) }, cancellationToken: ct));

        var total = await results.ReadSingleAsync<long>();
        var items = (await results.ReadAsync<NoteSummaryRow>()).Select(r => r.ToDto()).ToList();
        return new PagedResult<NoteSummaryDto>(items, (int)total, page, pageSize);
    }

    /// <summary>Number of the caller's own notes carrying all of <paramref name="tags"/>, and their pages.</summary>
    public async Task<NoteStatsDto> GetStatsAsync(string ownerId, IEnumerable<string> tags, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var (notes, pages) = await connection.QuerySingleAsync<(int Notes, int Pages)>(new CommandDefinition(
            sql.Get("Commands/Notes/GetNoteStats.sql"),
            new { OwnerId = ownerId, Tags = tags.ToArray() }, cancellationToken: ct));

        return new NoteStatsDto(notes, pages);
    }

    public async Task<NoteDto?> GetNoteAsync(Guid noteId, DatabaseUser user, NoteAccess access, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            sql.Get("Commands/Notes/GetNote.sql"),
            new { NoteId = noteId, user.UserId, user.Email }, cancellationToken: ct));

        var note = await results.ReadSingleOrDefaultAsync<Note>();
        var pages = await results.ReadAsync<Page>();
        var shareCount = await results.ReadSingleAsync<int>();
        return note?.ToDto(pages, access, shareCount);
    }

    public async Task<NoteDto> CreateNoteAsync(string ownerId, string title, IEnumerable<string> tags, IReadOnlyList<NewPage> files, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Note { Id = Guid.NewGuid(), OwnerId = ownerId, Title = title, CreatedAt = now, UpdatedAt = now, Tags = tags.ToArray() };

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Notes/InsertNote.sql"), note, transaction, cancellationToken: ct));

        var pages = await InsertPagesAsync(connection, transaction, note.Id, ownerId, 1, files, now, ct);

        await transaction.CommitAsync(ct);
        return note.ToDto(pages);
    }

    public async Task<NoteDto?> RenameNoteAsync(Guid noteId, DatabaseUser user, NoteAccess access, string title, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Notes/RenameNote.sql"),
            new { NoteId = noteId, user.UserId, user.Email, Contributor = SharePermission.Contributor, Title = title, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));

        return updated == 0 ? null : await GetNoteAsync(noteId, user, access, ct);
    }

    public async Task<NoteDto?> SetTagsAsync(Guid noteId, DatabaseUser user, NoteAccess access, IEnumerable<string> tags, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Tags/SetNoteTags.sql"),
            new { NoteId = noteId, user.UserId, user.Email, Contributor = SharePermission.Contributor, Tags = tags.ToArray(), Now = DateTimeOffset.UtcNow }, cancellationToken: ct));

        return updated == 0 ? null : await GetNoteAsync(noteId, user, access, ct);
    }

    /// <summary>Appends pages at the end of a note. Pages always belong to the note's owner, also when a contributor adds them.</summary>
    public async Task<AddPagesResult> AddPagesAsync(Guid noteId, DatabaseUser user, NoteAccess access, IReadOnlyList<NewPage> files, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Locking the note serializes concurrent uploads so page numbers and the page limit stay consistent.
        var ownerId = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            sql.Get("Commands/Notes/LockEditableNoteOwner.sql"),
            new { NoteId = noteId, user.UserId, user.Email, Contributor = SharePermission.Contributor }, transaction, cancellationToken: ct));
        if (ownerId is null)
        {
            return new AddPagesResult(AddPagesOutcome.NotFound);
        }

        var (count, last) = await connection.QuerySingleAsync<(int Count, int Last)>(new CommandDefinition(
            sql.Get("Commands/Pages/GetPageCountAndLastNumber.sql"),
            new { NoteId = noteId }, transaction, cancellationToken: ct));

        var remaining = UploadLimits.MaxFilesPerNote - count;
        if (files.Count > remaining)
        {
            return new AddPagesResult(AddPagesOutcome.TooMany, Remaining: Math.Max(0, remaining));
        }

        var pages = await InsertPagesAsync(connection, transaction, noteId, ownerId, last + 1, files, now, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Notes/TouchNote.sql"), new { NoteId = noteId, Now = now }, transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);

        var note = await GetNoteAsync(noteId, user, access, ct);
        return note is null
            ? new AddPagesResult(AddPagesOutcome.NotFound)
            : new AddPagesResult(AddPagesOutcome.Added, note, pages.Select(p => p.Id).ToList());
    }

    /// <summary>Deletes a page and renumbers the remaining ones. The last page of a note can't be deleted.</summary>
    public Task<(PageChangeOutcome Outcome, NoteDto? Note)> DeletePageAsync(Guid noteId, Guid pageId, DatabaseUser user, NoteAccess access, CancellationToken ct) =>
        ChangePageAsync(noteId, pageId, null, user, access, ct);

    /// <summary>Moves a page to <paramref name="pageNumber"/> (clamped to the note's pages) and renumbers the others.</summary>
    public Task<(PageChangeOutcome Outcome, NoteDto? Note)> MovePageAsync(Guid noteId, Guid pageId, int pageNumber, DatabaseUser user, NoteAccess access, CancellationToken ct) =>
        ChangePageAsync(noteId, pageId, pageNumber, user, access, ct);

    /// <summary>Deletes (<paramref name="moveTo"/> is <c>null</c>) or moves a page, then numbers all pages 1..n.</summary>
    private async Task<(PageChangeOutcome Outcome, NoteDto? Note)> ChangePageAsync(
        Guid noteId, Guid pageId, int? moveTo, DatabaseUser user, NoteAccess access, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Locking the note serializes page changes and uploads so page numbers stay consistent.
        var locked = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            sql.Get("Commands/Notes/LockEditableNote.sql"),
            new { NoteId = noteId, user.UserId, user.Email, Contributor = SharePermission.Contributor }, transaction, cancellationToken: ct));
        if (locked is null)
        {
            return (PageChangeOutcome.NotFound, null);
        }

        var order = (await connection.QueryAsync<Guid>(new CommandDefinition(
            sql.Get("Commands/Pages/GetPageOrder.sql"),
            new { NoteId = noteId }, transaction, cancellationToken: ct))).AsList();
        var index = order.IndexOf(pageId);
        if (index < 0)
        {
            return (PageChangeOutcome.NotFound, null);
        }

        order.RemoveAt(index);
        if (moveTo is null)
        {
            if (order.Count == 0)
            {
                return (PageChangeOutcome.LastPage, null);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                sql.Get("Commands/Pages/DeletePage.sql"),
                new { PageId = pageId, NoteId = noteId }, transaction, cancellationToken: ct));
        }
        else
        {
            var target = Math.Clamp(moveTo.Value, 1, order.Count + 1) - 1;
            if (target == index)
            {
                return (PageChangeOutcome.Changed, await GetNoteAsync(noteId, user, access, ct));
            }

            order.Insert(target, pageId);
        }

        await RenumberAsync(connection, transaction, noteId, order, DateTimeOffset.UtcNow, ct);

        await transaction.CommitAsync(ct);
        var note = await GetNoteAsync(noteId, user, access, ct);
        return note is null ? (PageChangeOutcome.NotFound, null) : (PageChangeOutcome.Changed, note);
    }

    /// <summary>
    /// Moves a page (keeping its original, extracted text and corrections) to the end of another note. The caller must own
    /// both notes. Returns the source note.
    /// </summary>
    public async Task<(PageChangeOutcome Outcome, NoteDto? Note)> TransferPageAsync(
        Guid noteId, Guid pageId, Guid targetNoteId, DatabaseUser user, NoteAccess access, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Lock both notes in a fixed order so concurrent transfers between the same notes can't deadlock.
        var locked = (await connection.QueryAsync<Guid>(new CommandDefinition(
            sql.Get("Commands/Notes/LockOwnedNotes.sql"),
            new { Ids = new[] { noteId, targetNoteId }, user.UserId }, transaction, cancellationToken: ct))).AsList();
        if (locked.Count != 2)
        {
            return (PageChangeOutcome.NotFound, null);
        }

        var order = (await connection.QueryAsync<Guid>(new CommandDefinition(
            sql.Get("Commands/Pages/GetPageOrder.sql"),
            new { NoteId = noteId }, transaction, cancellationToken: ct))).AsList();
        if (!order.Remove(pageId))
        {
            return (PageChangeOutcome.NotFound, null);
        }

        if (order.Count == 0)
        {
            return (PageChangeOutcome.LastPage, null);
        }

        var (count, last) = await connection.QuerySingleAsync<(int Count, int Last)>(new CommandDefinition(
            sql.Get("Commands/Pages/GetPageCountAndLastNumber.sql"),
            new { NoteId = targetNoteId }, transaction, cancellationToken: ct));
        if (count >= UploadLimits.MaxFilesPerNote)
        {
            return (PageChangeOutcome.TargetFull, null);
        }

        var now = DateTimeOffset.UtcNow;
        await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Pages/TransferPage.sql"),
            new { PageId = pageId, NoteId = noteId, TargetNoteId = targetNoteId, PageNumber = last + 1, Now = now }, transaction, cancellationToken: ct));

        await RenumberAsync(connection, transaction, noteId, order, now, ct);

        await transaction.CommitAsync(ct);
        var note = await GetNoteAsync(noteId, user, access, ct);
        return note is null ? (PageChangeOutcome.NotFound, null) : (PageChangeOutcome.Changed, note);
    }

    /// <summary>Numbers a note's pages 1..n in the given order.</summary>
    private async Task RenumberAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid noteId, List<Guid> order, DateTimeOffset now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Pages/RenumberPages.sql"),
            new { NoteId = noteId, Ids = order.ToArray(), Now = now }, transaction, cancellationToken: ct));
    }

    public async Task<bool> DeleteNoteAsync(string ownerId, Guid noteId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Notes/DeleteNote.sql"),
            new { NoteId = noteId, OwnerId = ownerId }, cancellationToken: ct)) > 0;
    }

    public async Task<PageOriginal?> GetOriginalAsync(Guid noteId, Guid pageId, DatabaseUser user, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PageOriginal>(new CommandDefinition(
            sql.Get("Commands/Pages/GetOriginal.sql"),
            new { PageId = pageId, NoteId = noteId, user.UserId, user.Email }, cancellationToken: ct));
    }

    public async Task<PageDto?> UpdatePageTextAsync(Guid noteId, Guid pageId, DatabaseUser user, string text, CancellationToken ct)
    {
        // Saving text identical to the extraction clears the correction so future re-processing shows through.
        await using var connection = await db.OpenConnectionAsync(ct);
        var page = await connection.QuerySingleOrDefaultAsync<Page>(new CommandDefinition(
            sql.Get("Commands/Pages/UpdatePageText.sql"),
            new { PageId = pageId, NoteId = noteId, user.UserId, user.Email, Contributor = SharePermission.Contributor, Text = text, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));

        return page?.ToDto();
    }

    public async Task<(ReprocessOutcome Outcome, PageDto? Page)> ResetForReprocessingAsync(
        Guid noteId, Guid pageId, DatabaseUser user, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            sql.Get("Commands/Pages/ResetForReprocessing.sql"), new
            {
                PageId = pageId,
                NoteId = noteId,
                user.UserId,
                user.Email,
                Contributor = SharePermission.Contributor,
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
        var rows = await connection.QueryAsync<(string Name, int Count)>(new CommandDefinition(
            sql.Get("Commands/Tags/ListTags.sql"), new { OwnerId = ownerId }, cancellationToken: ct));

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
        await using var connection = await db.OpenConnectionAsync(ct);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            sql.Get("Commands/Search/SearchNotes.sql"), new
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

    private async Task<List<Page>> InsertPagesAsync(
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

        await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Pages/InsertPage.sql"), pages, transaction, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            sql.Get("Commands/Pages/InsertPageContent.sql"), contents, transaction, cancellationToken: ct));

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
