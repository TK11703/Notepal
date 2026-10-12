using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.DeleteNote;

internal static class DeleteNoteEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapDelete("/{noteId:guid}", HandleAsync)
            .WithName("DeleteNote")
            .Produces(StatusCodes.Status204NoContent);

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> HandleAsync(
        Guid noteId, INotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        if (await notes.DeleteNoteAsync(user.UserId!, noteId, ct))
        {
            return TypedResults.NoContent();
        }

        return await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct) is null
            ? TypedResults.NotFound()
            : NoteAuthorization.OwnerOnly("delete it");
    }
}
