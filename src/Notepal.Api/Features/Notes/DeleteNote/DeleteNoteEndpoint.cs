using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.DeleteNote;

internal static class DeleteNoteEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapDelete("/{noteId:guid}", HandleAsync).Produces(StatusCodes.Status204NoContent);

    private static async Task<IResult> HandleAsync(Guid noteId, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        if (await notes.DeleteNoteAsync(user.UserId!, noteId, ct))
        {
            return Results.NoContent();
        }

        return await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct) is null
            ? Results.NotFound() : NoteAuthorization.OwnerOnly("delete it");
    }
}
