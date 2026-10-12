using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.ListShares;

internal static class ListSharesEndpoint
{
    public static void Map(RouteGroupBuilder shares) =>
        shares.MapGet("/", HandleAsync)
            .WithName("ListShares")
            .Produces<List<NoteShareDto>>();

    private static async Task<Results<Ok<List<NoteShareDto>>, NotFound, ProblemHttpResult>> HandleAsync(
        Guid noteId, INotesRepository notes, ISharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        var (allowed, forbidden, notFound) = await NoteAuthorization.RequireOwnerAsync(notes, noteId, user, ct);
        if (notFound)
        {
            return TypedResults.NotFound();
        }
        if (forbidden is not null)
        {
            return forbidden;
        }

        return TypedResults.Ok(await shares.ListSharesAsync(user.UserId!, noteId, ct));
    }
}
