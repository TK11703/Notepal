using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.SharedWithMe;

internal static class SharedWithMeEndpoint
{
    public static void Map(RouteGroupBuilder shared) =>
        shared.MapGet("/with-me", HandleAsync)
            .WithName("SharedWithMe")
            .Produces<PagedResult<SharedNoteDto>>();

    private static async Task<Ok<PagedResult<SharedNoteDto>>> HandleAsync(
        ISharesRepository shares, ICurrentUser user, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        (page, pageSize) = Paging.Normalize(page, pageSize);
        return TypedResults.Ok(await shares.SharedWithMeAsync(user.ToDatabaseUser(), page, pageSize, ct));
    }
}
