using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.MovePage;

internal static class MovePageEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapPut("/{noteId:guid}/pages/{pageId:guid}/position", HandleAsync).Produces<NoteDto>();

    private static async Task<IResult> HandleAsync(Guid noteId, Guid pageId, MovePageRequest body,
        NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (access, denied) = await NoteAuthorization.RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var (outcome, note) = await notes.MovePageAsync(noteId, pageId, body.PageNumber, user.ToDatabaseUser(), access, ct);
        return outcome == PageChangeOutcome.Changed ? Results.Ok(note) : Results.NotFound();
    }
}
