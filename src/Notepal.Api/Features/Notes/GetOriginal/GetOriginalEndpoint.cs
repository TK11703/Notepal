using System.Net.Mime;
using Microsoft.Net.Http.Headers;
using Notepal.Api.Auth;
using Notepal.Api.Processing;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.GetOriginal;

internal static class GetOriginalEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapGet("/{noteId:guid}/pages/{pageId:guid}/original", HandleAsync);

    private static async Task<IResult> HandleAsync(
        Guid noteId, Guid pageId, NotesRepository notes, ICurrentUser user, HttpContext http, CancellationToken ct)
    {
        var original = await notes.GetOriginalAsync(noteId, pageId, user.ToDatabaseUser(), ct);
        if (original is null)
        {
            return Results.NotFound();
        }

        // Images and PDFs are shown inline; Word documents can only be downloaded.
        var inline = FileTypes.IsImage(original.ContentType) || original.ContentType == FileTypes.Pdf;
        var disposition = new ContentDispositionHeaderValue(inline ? DispositionTypeNames.Inline : DispositionTypeNames.Attachment);
        disposition.SetHttpFileName(original.FileName);
        http.Response.Headers.ContentDisposition = disposition.ToString();
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, max-age=3600";

        return Results.Bytes(original.Data, original.ContentType);
    }
}
