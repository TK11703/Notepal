using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Notepal.Api.Auth;
using Notepal.Api.Data;
using Notepal.Api.Processing;
using Notepal.Shared;

namespace Notepal.Api.Endpoints;

public static class NotesEndpoints
{
    private const int MaxTitleLength = 200;
    private const int MaxTextLength = 1_000_000;

    public static RouteGroupBuilder MapNotesEndpoints(this RouteGroupBuilder api)
    {
        var notes = api.MapGroup("/notes").WithTags("Notes");
        api.MapGet("/tags", ListTags).WithTags("Tags");

        notes.MapGet("/", ListNotes);
        notes.MapGet("/{noteId:guid}", GetNote);
        notes.MapPost("/", CreateNote).DisableAntiforgery();
        notes.MapPut("/{noteId:guid}", UpdateNote);
        notes.MapPut("/{noteId:guid}/tags", UpdateNoteTags);
        notes.MapDelete("/{noteId:guid}", DeleteNote);
        notes.MapGet("/{noteId:guid}/pages/{pageId:guid}/original", GetOriginal);
        notes.MapPut("/{noteId:guid}/pages/{pageId:guid}/text", UpdatePageText);
        notes.MapPost("/{noteId:guid}/pages/{pageId:guid}/reprocess", ReprocessPage);

        return api;
    }

    private static async Task<IResult> ListNotes(NotepalDbContext db, ICurrentUser user, [FromQuery(Name = "tag")] string[]? tags, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var ownerId = user.UserId!;
        (page, pageSize) = Paging.Normalize(page, pageSize);

        var query = db.Notes.Where(n => n.OwnerId == ownerId).WithAllTags(tags);
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(n => n.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new
            {
                n.Id,
                n.Title,
                n.CreatedAt,
                n.UpdatedAt,
                n.Tags,
                Statuses = n.Pages.Select(p => p.Status).ToList(),
                Preview = n.Pages.OrderBy(p => p.PageNumber)
                    .Select(p => (p.EditedText ?? p.ExtractedText)!.Substring(0, 240))
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new NoteSummaryDto(
                r.Id, r.Title, r.CreatedAt, r.UpdatedAt, r.Statuses.Count, Mapping.AggregateStatus(r.Statuses), r.Preview, r.Tags))
            .ToList();

        return Results.Ok(new PagedResult<NoteSummaryDto>(items, total, page, pageSize));
    }

    private static async Task<IResult> GetNote(Guid noteId, NotepalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        var note = await db.Notes.AsNoTracking().Include(n => n.Pages)
            .FirstOrDefaultAsync(n => n.Id == noteId && n.OwnerId == ownerId, ct);

        return note is null ? Results.NotFound() : Results.Ok(note.ToDto());
    }

    private static async Task<IResult> CreateNote(HttpRequest request, NotepalDbContext db, ICurrentUser user, ProcessingQueue queue, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        if (!request.HasFormContentType)
        {
            return Results.Problem("Expected a multipart/form-data request.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        var form = await request.ReadFormAsync(ct);
        var files = form.Files;
        if (files.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["files"] = ["Upload at least one file."] });
        }

        if (files.Count > UploadLimits.MaxFilesPerNote)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["files"] = [$"A note can contain at most {UploadLimits.MaxFilesPerNote} files."] });
        }

        var now = DateTimeOffset.UtcNow;
        var title = form["title"].ToString().Trim();
        if (string.IsNullOrEmpty(title))
        {
            title = files.Count == 1 ? Path.GetFileNameWithoutExtension(files[0].FileName) : $"Note {now:yyyy-MM-dd HH:mm}";
        }

        var tags = TagLimits.NormalizeAll(form["tags"]);
        if (tags.Count > TagLimits.MaxTagsPerNote)
        {
            return TooManyTags();
        }

        var note = new Note
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Title = Truncate(title, MaxTitleLength),
            CreatedAt = now,
            UpdatedAt = now,
            Tags = tags,
        };

        var errors = new List<string>();
        var number = 1;
        foreach (var file in files)
        {
            var fileName = Truncate(Path.GetFileName(file.FileName), 260);
            if (file.Length == 0 || file.Length > UploadLimits.MaxFileBytes)
            {
                errors.Add($"'{fileName}' must be between 1 byte and {UploadLimits.MaxFileBytes / (1024 * 1024)} MB.");
                continue;
            }

            byte[] data;
            using (var buffer = new MemoryStream((int)file.Length))
            {
                await file.CopyToAsync(buffer, ct);
                data = buffer.ToArray();
            }

            if (!FileTypes.TryResolve(fileName, data, out var contentType))
            {
                errors.Add($"'{fileName}' is not a supported file. Upload JPEG, PNG, WebP or GIF images, PDF or Word (.docx) documents.");
                continue;
            }

            note.Pages.Add(new Page
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                PageNumber = number++,
                FileName = fileName,
                ContentType = contentType,
                SizeBytes = data.LongLength,
                Status = ProcessingStatus.Pending,
                UpdatedAt = now,
                Content = new PageContent { Data = data },
            });
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["files"] = [.. errors] });
        }

        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        foreach (var page in note.Pages)
        {
            queue.Enqueue(page.Id);
        }

        return Results.Created($"/api/notes/{note.Id}", note.ToDto());
    }

    private static async Task<IResult> UpdateNote(Guid noteId, UpdateNoteRequest body, NotepalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        var title = body.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > MaxTitleLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [$"Title is required and must be at most {MaxTitleLength} characters."] });
        }

        var note = await db.Notes.Include(n => n.Pages).FirstOrDefaultAsync(n => n.Id == noteId && n.OwnerId == ownerId, ct);
        if (note is null)
        {
            return Results.NotFound();
        }

        note.Title = title;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(note.ToDto());
    }

    private static async Task<IResult> UpdateNoteTags(Guid noteId, UpdateNoteTagsRequest body, NotepalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        var tags = TagLimits.NormalizeAll(body.Tags);
        if (tags.Count > TagLimits.MaxTagsPerNote)
        {
            return TooManyTags();
        }

        var note = await db.Notes.Include(n => n.Pages).FirstOrDefaultAsync(n => n.Id == noteId && n.OwnerId == ownerId, ct);
        if (note is null)
        {
            return Results.NotFound();
        }

        note.Tags = tags;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(note.ToDto());
    }

    /// <summary>All tags the caller has used, most used first, so the UI can suggest them.</summary>
    private static async Task<IResult> ListTags(NotepalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        var tagArrays = await db.Notes
            .Where(n => n.OwnerId == ownerId && n.Tags.Count > 0)
            .Select(n => n.Tags)
            .ToListAsync(ct);

        var tags = tagArrays
            .SelectMany(t => t)
            .GroupBy(t => t, StringComparer.Ordinal)
            .Select(g => new TagDto(g.Key, g.Count()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        return Results.Ok(tags);
    }

    private static IResult TooManyTags() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["tags"] = [$"A note can have at most {TagLimits.MaxTagsPerNote} tags."] });

    private static async Task<IResult> DeleteNote(Guid noteId, NotepalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        var deleted = await db.Notes.Where(n => n.Id == noteId && n.OwnerId == ownerId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> GetOriginal(Guid noteId, Guid pageId, NotepalDbContext db, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        var original = await db.Pages
            .Where(p => p.Id == pageId && p.NoteId == noteId && p.OwnerId == ownerId)
            .Select(p => new { p.FileName, p.ContentType, p.UpdatedAt, Data = p.Content!.Data })
            .FirstOrDefaultAsync(ct);

        if (original is null)
        {
            return Results.NotFound();
        }

        // Images and PDFs are shown inline in the viewer; Word documents can only be downloaded.
        var inline = FileTypes.IsImage(original.ContentType) || original.ContentType == FileTypes.Pdf;
        var disposition = new ContentDispositionHeaderValue(inline ? DispositionTypeNames.Inline : DispositionTypeNames.Attachment);
        disposition.SetHttpFileName(original.FileName);
        http.Response.Headers.ContentDisposition = disposition.ToString();
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, max-age=3600";

        return Results.Bytes(original.Data, original.ContentType);
    }

    private static async Task<IResult> UpdatePageText(Guid noteId, Guid pageId, UpdatePageTextRequest body, NotepalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        if (body.Text is null || body.Text.Length > MaxTextLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [$"Text is required and must be at most {MaxTextLength} characters."] });
        }

        var page = await db.Pages.Include(p => p.Note)
            .FirstOrDefaultAsync(p => p.Id == pageId && p.NoteId == noteId && p.OwnerId == ownerId, ct);
        if (page is null)
        {
            return Results.NotFound();
        }

        // Saving text identical to the extraction clears the correction so future re-processing shows through.
        page.EditedText = string.Equals(body.Text, page.ExtractedText, StringComparison.Ordinal) ? null : body.Text;
        page.UpdatedAt = page.Note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(page.ToDto());
    }

    private static async Task<IResult> ReprocessPage(Guid noteId, Guid pageId, NotepalDbContext db, ICurrentUser user, ProcessingQueue queue, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == pageId && p.NoteId == noteId && p.OwnerId == ownerId, ct);
        if (page is null)
        {
            return Results.NotFound();
        }

        if (page.Status is ProcessingStatus.Pending or ProcessingStatus.Processing)
        {
            return Results.Conflict(new { message = "The page is already being processed." });
        }

        page.Status = ProcessingStatus.Pending;
        page.Attempts = 0;
        page.Error = null;
        page.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        queue.Enqueue(page.Id);

        return Results.Accepted(value: page.ToDto());
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

internal static class TagFilter
{
    /// <summary>Keeps notes that carry every one of the given tags (after normalisation). No tags means no filter.</summary>
    public static IQueryable<Note> WithAllTags(this IQueryable<Note> notes, IEnumerable<string?>? tags)
    {
        var wanted = TagLimits.NormalizeAll(tags);
        return wanted.Count == 0 ? notes : notes.Where(n => wanted.All(t => n.Tags.Contains(t)));
    }
}

internal static class Paging
{
    public static (int Page, int PageSize) Normalize(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));
}
