using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.DeletePage;

internal static class DeletePageEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapDelete("/{noteId:guid}/pages/{pageId:guid}", HandleAsync).Produces<NoteDto>();

    private static async Task<IResult> HandleAsync(
        Guid noteId, Guid pageId, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (access, denied) = await NoteAuthorization.RequireEditableAsync(notes, noteId, user, ct);
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
}
