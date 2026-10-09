using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Notepal.Api.Auth;
using Notepal.Api.Processing;
using Notepal.Database;
using Notepal.Shared;

namespace Notepal.Api.Endpoints;

public static class NotesEndpoints
{
    private const int MaxFileNameLength = 260;

    public static RouteGroupBuilder MapNotesEndpoints(this RouteGroupBuilder api)
    {
        var notes = api.MapGroup("/notes").WithTags("Notes");
        api.MapGet("/tags", ListTags).WithTags("Tags");

        notes.MapGet("/", ListNotes);
        notes.MapGet("/stats", GetStats);
        notes.MapGet("/{noteId:guid}", GetNote);
        notes.MapPost("/", CreateNote).DisableAntiforgery();
        notes.MapPut("/{noteId:guid}", UpdateNote);
        notes.MapPut("/{noteId:guid}/tags", UpdateNoteTags);
        notes.MapPost("/{noteId:guid}/pages", AddPages).DisableAntiforgery();
        notes.MapDelete("/{noteId:guid}", DeleteNote);
        notes.MapGet("/{noteId:guid}/pages/{pageId:guid}/original", GetOriginal);
        notes.MapPut("/{noteId:guid}/pages/{pageId:guid}/text", UpdatePageText);
        notes.MapPost("/{noteId:guid}/pages/{pageId:guid}/reprocess", ReprocessPage);
        notes.MapDelete("/{noteId:guid}/pages/{pageId:guid}", DeletePage);
        notes.MapPut("/{noteId:guid}/pages/{pageId:guid}/position", MovePage);
        notes.MapPost("/{noteId:guid}/pages/{pageId:guid}/transfer", TransferPage);

        return api;
    }

    private static async Task<IResult> ListNotes(NotesRepository notes, ICurrentUser user, [FromQuery(Name = "tag")] string[]? tags, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        (page, pageSize) = Paging.Normalize(page, pageSize);
        return Results.Ok(await notes.ListNotesAsync(user.UserId!, TagLimits.NormalizeAll(tags), page, pageSize, ct));
    }

    private static async Task<IResult> GetStats(NotesRepository notes, ICurrentUser user, [FromQuery(Name = "tag")] string[]? tags, CancellationToken ct) =>
        Results.Ok(await notes.GetStatsAsync(user.UserId!, TagLimits.NormalizeAll(tags), ct));

    private static async Task<IResult> GetNote(Guid noteId, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var access = await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct);
        var note = access is null ? null : await notes.GetNoteAsync(noteId, user.ToDatabaseUser(), access, ct);
        return note is null ? Results.NotFound() : Results.Ok(note);
    }

    private static async Task<IResult> CreateNote(
        IFormCollection form,
        NotesRepository notes,
        ICurrentUser user,
        ProcessingQueue queue,
        CancellationToken ct)
    {
        var files = form.Files;
        var title = form["title"].ToString().Trim();
        if (title.Length > NoteLimits.MaxTitleLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [$"Title must be at most {NoteLimits.MaxTitleLength} characters."] });
        }

        // File contents can't be checked declaratively: sizes and signatures are validated here.
        if (files.Count == 0)
        {
            return FilesProblem("Upload at least one file.");
        }

        if (files.Count > UploadLimits.MaxFilesPerNote)
        {
            return FilesProblem($"A note can contain at most {UploadLimits.MaxFilesPerNote} files.");
        }

        var normalizedTags = TagLimits.NormalizeAll(form["tags"]);
        if (normalizedTags.Count > TagLimits.MaxTagsPerNote)
        {
            return TooManyTags();
        }

        var (pages, errors) = await ReadFilesAsync(files, ct);
        if (errors.Count > 0)
        {
            return FilesProblem([.. errors]);
        }

        if (title.Length == 0)
        {
            title = files.Count == 1 ? Path.GetFileNameWithoutExtension(files[0].FileName) : $"Note {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}";
        }

        var note = await notes.CreateNoteAsync(user.UserId!, Truncate(title, NoteLimits.MaxTitleLength), normalizedTags, pages, ct);
        foreach (var page in note.Pages)
        {
            queue.Enqueue(page.Id);
        }

        return Results.Created($"/api/notes/{note.Id}", note);
    }

    private static async Task<IResult> UpdateNote(Guid noteId, UpdateNoteRequest body, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var title = body.Title.Trim();
        if (title.Length == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Title is required."] });
        }

        var (access, denied) = await RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var note = await notes.RenameNoteAsync(noteId, user.ToDatabaseUser(), access, title, ct);
        return note is null ? Results.NotFound() : Results.Ok(note);
    }

    private static async Task<IResult> UpdateNoteTags(Guid noteId, UpdateNoteTagsRequest body, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var tags = TagLimits.NormalizeAll(body.Tags);
        if (tags.Count > TagLimits.MaxTagsPerNote)
        {
            return TooManyTags();
        }

        var (access, denied) = await RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var note = await notes.SetTagsAsync(noteId, user.ToDatabaseUser(), access, tags, ct);
        return note is null ? Results.NotFound() : Results.Ok(note);
    }

    /// <summary>Appends uploaded files as new pages at the end of an existing note.</summary>
    private static async Task<IResult> AddPages(Guid noteId, IFormFileCollection files, NotesRepository notes, ICurrentUser user, ProcessingQueue queue, CancellationToken ct)
    {
        var (access, denied) = await RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        if (files.Count == 0)
        {
            return FilesProblem("Upload at least one file.");
        }

        if (files.Count > UploadLimits.MaxFilesPerNote)
        {
            return FilesProblem($"A note can contain at most {UploadLimits.MaxFilesPerNote} pages.");
        }

        var (pages, errors) = await ReadFilesAsync(files, ct);
        if (errors.Count > 0)
        {
            return FilesProblem([.. errors]);
        }

        var result = await notes.AddPagesAsync(noteId, user.ToDatabaseUser(), access, pages, ct);
        switch (result.Outcome)
        {
            case AddPagesOutcome.NotFound:
                return Results.NotFound();
            case AddPagesOutcome.TooMany:
                return FilesProblem(result.Remaining == 0
                    ? $"This note already has the maximum of {UploadLimits.MaxFilesPerNote} pages."
                    : $"A note can contain at most {UploadLimits.MaxFilesPerNote} pages. You can add {result.Remaining} more.");
        }

        foreach (var pageId in result.PageIds!)
        {
            queue.Enqueue(pageId);
        }

        return Results.Ok(result.Note);
    }

    private static async Task<IResult> DeleteNote(Guid noteId, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        if (await notes.DeleteNoteAsync(user.UserId!, noteId, ct))
        {
            return Results.NoContent();
        }

        // People a note is shared with can see it, but only its owner can delete it.
        return await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct) is null ? Results.NotFound() : NoteAccessResults.OwnerOnly("delete it");
    }

    private static async Task<IResult> GetOriginal(Guid noteId, Guid pageId, NotesRepository notes, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var original = await notes.GetOriginalAsync(noteId, pageId, user.ToDatabaseUser(), ct);
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

    private static async Task<IResult> UpdatePageText(Guid noteId, Guid pageId, UpdatePageTextRequest body, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (access, denied) = await RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var page = await notes.UpdatePageTextAsync(noteId, pageId, user.ToDatabaseUser(), body.Text, ct);
        return page is null ? Results.NotFound() : Results.Ok(page);
    }

    private static async Task<IResult> ReprocessPage(Guid noteId, Guid pageId, NotesRepository notes, ICurrentUser user, ProcessingQueue queue, CancellationToken ct)
    {
        var (access, denied) = await RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var (outcome, page) = await notes.ResetForReprocessingAsync(noteId, pageId, user.ToDatabaseUser(), ct);
        switch (outcome)
        {
            case ReprocessOutcome.NotFound:
                return Results.NotFound();
            case ReprocessOutcome.AlreadyProcessing:
                return Results.Conflict(new { message = "The page is already being processed." });
        }

        queue.Enqueue(page!.Id);
        return Results.Accepted(value: page);
    }

    private static async Task<IResult> DeletePage(Guid noteId, Guid pageId, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (access, denied) = await RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var (outcome, note) = await notes.DeletePageAsync(noteId, pageId, user.ToDatabaseUser(), access, ct);
        return outcome switch
        {
            PageChangeOutcome.LastPage => Results.Conflict(new { message = "A note needs at least one page. Delete the note instead." }),
            PageChangeOutcome.Changed => Results.Ok(note),
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> MovePage(Guid noteId, Guid pageId, MovePageRequest body, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (access, denied) = await RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var (outcome, note) = await notes.MovePageAsync(noteId, pageId, body.PageNumber, user.ToDatabaseUser(), access, ct);
        return outcome == PageChangeOutcome.Changed ? Results.Ok(note) : Results.NotFound();
    }

    /// <summary>Moves a page to the end of another of the caller's notes, so it doesn't have to be uploaded and processed again.</summary>
    private static async Task<IResult> TransferPage(Guid noteId, Guid pageId, TransferPageRequest body, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        if (body.TargetNoteId == noteId)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["targetNoteId"] = ["Choose a different note."] });
        }

        var access = await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct);
        if (access is null)
        {
            return Results.NotFound();
        }

        // Pages belong to the note's owner, so only the owner can move them between notes.
        if (access.Role != NoteRole.Owner)
        {
            return NoteAccessResults.OwnerOnly("move its pages to another note");
        }

        var (outcome, note) = await notes.TransferPageAsync(noteId, pageId, body.TargetNoteId, user.ToDatabaseUser(), access, ct);
        return outcome switch
        {
            PageChangeOutcome.Changed => Results.Ok(note),
            PageChangeOutcome.LastPage => Results.Conflict(new { message = "A note needs at least one page. Add another page first, or delete the note instead." }),
            PageChangeOutcome.TargetFull => Results.Conflict(new { message = $"The other note already has the maximum of {UploadLimits.MaxFilesPerNote} pages." }),
            _ => Results.NotFound(),
        };
    }

    /// <summary>All tags the caller has used, most used first, so the UI can suggest them.</summary>
    private static async Task<IResult> ListTags(NotesRepository notes, ICurrentUser user, CancellationToken ct) =>
        Results.Ok(await notes.ListTagsAsync(user.UserId!, ct));

    /// <summary>Resolves the caller's access when they own the note or are a contributor on it; otherwise returns the error result.</summary>
    private static async Task<(NoteAccess? Access, IResult Denied)> RequireEditableAsync(NotesRepository notes, Guid noteId, ICurrentUser user, CancellationToken ct)
    {
        var access = await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct);
        return access is null ? (null, Results.NotFound())
            : access.CanEdit ? (access, Results.Empty)
            : (null, NoteAccessResults.ReadOnly());
    }

    /// <summary>Validates uploaded files (size, extension and signature).</summary>
    private static async Task<(List<NewPage> Pages, List<string> Errors)> ReadFilesAsync(IFormFileCollection files, CancellationToken ct)
    {
        var pages = new List<NewPage>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            var fileName = Truncate(Path.GetFileName(file.FileName), MaxFileNameLength);
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

            pages.Add(new NewPage(fileName, contentType, data));
        }

        return (pages, errors);
    }

    private static IResult FilesProblem(params string[] errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["files"] = errors });

    private static IResult TooManyTags() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["tags"] = [$"A note can have at most {TagLimits.MaxTagsPerNote} tags."] });

    internal static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

internal static class Paging
{
    public static (int Page, int PageSize) Normalize(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, NoteLimits.MaxPageSize));
}
