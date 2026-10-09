using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.UpdatePageText;

internal static class UpdatePageTextEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapPut("/{noteId:guid}/pages/{pageId:guid}/text", HandleAsync).Produces<PageDto>();

    private static async Task<IResult> HandleAsync(Guid noteId, Guid pageId, UpdatePageTextRequest body,
        NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (access, denied) = await NoteAuthorization.RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var page = await notes.UpdatePageTextAsync(noteId, pageId, user.ToDatabaseUser(), body.Text, ct);
        return page is null ? Results.NotFound() : Results.Ok(page);
    }
}
