using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.RemoveShare;

internal static class RemoveShareEndpoint
{
    public static void Map(RouteGroupBuilder shares) =>
        shares.MapDelete("/{shareId:guid}", HandleAsync).Produces(StatusCodes.Status204NoContent);

    private static async Task<IResult> HandleAsync(
        Guid noteId, Guid shareId, NotesRepository notes, SharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        if (await shares.RemoveShareAsync(noteId, shareId, user.ToDatabaseUser(), ct))
        {
            return Results.NoContent();
        }

        return await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct) is { Role: not NoteRole.Owner }
            ? NoteAuthorization.OwnerOnly("change who it is shared with")
            : Results.NotFound();
    }
}
