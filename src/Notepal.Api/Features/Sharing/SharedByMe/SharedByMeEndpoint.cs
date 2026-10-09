using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.SharedByMe;

internal static class SharedByMeEndpoint
{
    public static void Map(RouteGroupBuilder shared) =>
        shared.MapGet("/by-me", HandleAsync).Produces<PagedResult<SharedNoteDto>>();

    private static async Task<IResult> HandleAsync(
        SharesRepository shares, ICurrentUser user, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        (page, pageSize) = Paging.Normalize(page, pageSize);
        return Results.Ok(await shares.SharedByMeAsync(user.UserId!, page, pageSize, ct));
    }
}
