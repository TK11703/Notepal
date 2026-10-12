using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.RemoveShare;

internal static class RemoveShareEndpoint
{
    public static void Map(RouteGroupBuilder shares) =>
        shares.MapDelete("/{shareId:guid}", HandleAsync)
            .WithName("RemoveShare")
            .Produces(StatusCodes.Status204NoContent);

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> HandleAsync(
        Guid noteId, Guid shareId, INotesRepository notes, ISharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        if (await shares.RemoveShareAsync(noteId, shareId, user.ToDatabaseUser(), ct))
        {
            return TypedResults.NoContent();
        }

        return await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct) is { Role: not NoteRole.Owner }
            ? NoteAuthorization.OwnerOnly("change who it is shared with")
            : TypedResults.NotFound();
    }
}
