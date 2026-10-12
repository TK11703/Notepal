using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.SharedByMe;

internal static class SharedByMeEndpoint
{
    public static void Map(RouteGroupBuilder shared) =>
        shared.MapGet("/by-me", HandleAsync)
            .WithName("SharedByMe")
            .Produces<PagedResult<SharedNoteDto>>();

    private static async Task<Ok<PagedResult<SharedNoteDto>>> HandleAsync(
        ISharesRepository shares, ICurrentUser user, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        (page, pageSize) = Paging.Normalize(page, pageSize);
        return TypedResults.Ok(await shares.SharedByMeAsync(user.UserId!, page, pageSize, ct));
    }
}
