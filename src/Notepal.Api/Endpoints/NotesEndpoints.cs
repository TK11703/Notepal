using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Notepal.Api.Auth;
using Notepal.Api.Data;
using Notepal.Api.Processing;
using Notepal.Shared;

namespace Notepal.Api.Endpoints;

public static class NotesEndpoints
{
    private const int MaxFileNameLength = 260;

    public static RouteGroupBuilder MapNotesEndpoints(this RouteGroupBuilder api)
    {
        var notes = api.MapGroup("/notes").WithTags("Notes");

        notes.MapGet("/", ListNotes);
        notes.MapGet("/{noteId:guid}", GetNote);
        notes.MapPost("/", CreateNote).DisableAntiforgery();
        notes.MapPut("/{noteId:guid}", UpdateNote);
        notes.MapDelete("/{noteId:guid}", DeleteNote);
        notes.MapGet("/{noteId:guid}/pages/{pageId:guid}/original", GetOriginal);
        notes.MapPut("/{noteId:guid}/pages/{pageId:guid}/text", UpdatePageText);
        notes.MapPost("/{noteId:guid}/pages/{pageId:guid}/reprocess", ReprocessPage);

        return api;
    }

    private static async Task<IResult> ListNotes(
        NotesRepository notes,
        ICurrentUser user,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, NoteLimits.MaxPageSize)] int pageSize = 20,
        CancellationToken ct = default) =>
        Results.Ok(await notes.ListNotesAsync(user.UserId!, page, pageSize, ct));

    private static async Task<IResult> GetNote(Guid noteId, NotesRepository notes, ICurrentUser user, CancellationToken ct) =>
        await notes.GetNoteAsync(user.UserId!, noteId, ct) is { } note ? Results.Ok(note) : Results.NotFound();

    private static async Task<IResult> CreateNote(
        [FromForm, MaxLength(NoteLimits.MaxTitleLength)] string? title,
        IFormFileCollection files,
        NotesRepository notes,
        ICurrentUser user,
        ProcessingQueue queue,
        CancellationToken ct)
    {
        // File contents can't be checked declaratively: sizes and signatures are validated here.
        if (files.Count == 0)
        {
            return FilesProblem("Upload at least one file.");
        }

        if (files.Count > UploadLimits.MaxFilesPerNote)
        {
            return FilesProblem($"A note can contain at most {UploadLimits.MaxFilesPerNote} files.");
        }

        var errors = new List<string>();
        var pages = new List<NewPage>(files.Count);
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

        if (errors.Count > 0)
        {
            return FilesProblem([.. errors]);
        }

        title = title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            title = files.Count == 1
                ? Truncate(Path.GetFileNameWithoutExtension(files[0].FileName), NoteLimits.MaxTitleLength)
                : $"Note {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}";
        }

        var note = await notes.CreateNoteAsync(user.UserId!, title, pages, ct);
        foreach (var page in note.Pages)
        {
            queue.Enqueue(page.Id);
        }

        return Results.Created($"/api/notes/{note.Id}", note);
    }

    private static async Task<IResult> UpdateNote(Guid noteId, UpdateNoteRequest body, NotesRepository notes, ICurrentUser user, CancellationToken ct) =>
        await notes.RenameNoteAsync(user.UserId!, noteId, body.Title.Trim(), ct) is { } note ? Results.Ok(note) : Results.NotFound();

    private static async Task<IResult> DeleteNote(Guid noteId, NotesRepository notes, ICurrentUser user, CancellationToken ct) =>
        await notes.DeleteNoteAsync(user.UserId!, noteId, ct) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> GetOriginal(Guid noteId, Guid pageId, NotesRepository notes, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var original = await notes.GetOriginalAsync(user.UserId!, noteId, pageId, ct);
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

    private static async Task<IResult> UpdatePageText(Guid noteId, Guid pageId, UpdatePageTextRequest body, NotesRepository notes, ICurrentUser user, CancellationToken ct) =>
        await notes.UpdatePageTextAsync(user.UserId!, noteId, pageId, body.Text, ct) is { } page ? Results.Ok(page) : Results.NotFound();

    private static async Task<IResult> ReprocessPage(Guid noteId, Guid pageId, NotesRepository notes, ICurrentUser user, ProcessingQueue queue, CancellationToken ct)
    {
        var (outcome, page) = await notes.ResetForReprocessingAsync(user.UserId!, noteId, pageId, ct);
        switch (outcome)
        {
            case ReprocessOutcome.NotFound:
                return Results.NotFound();
            case ReprocessOutcome.AlreadyProcessing:
                return Results.Problem("The page is already being processed.", statusCode: StatusCodes.Status409Conflict);
            default:
                queue.Enqueue(pageId);
                return Results.Accepted(value: page);
        }
    }

    private static IResult FilesProblem(params string[] errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["files"] = errors });

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
