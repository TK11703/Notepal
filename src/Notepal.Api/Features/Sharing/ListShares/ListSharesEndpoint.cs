using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.ListShares;

internal static class ListSharesEndpoint
{
    public static void Map(RouteGroupBuilder shares) =>
        shares.MapGet("/", HandleAsync).Produces<List<NoteShareDto>>();

    private static async Task<IResult> HandleAsync(
        Guid noteId, NotesRepository notes, SharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        var denied = await NoteAuthorization.RequireOwnerAsync(notes, noteId, user, ct);
        if (denied is not null)
        {
            return denied;
        }

        return Results.Ok(await shares.ListSharesAsync(user.UserId!, noteId, ct));
    }
}
