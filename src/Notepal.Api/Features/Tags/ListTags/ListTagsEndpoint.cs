using Notepal.Api.Auth;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Tags.ListTags;

internal static class ListTagsEndpoint
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/tags", HandleAsync).Produces<List<TagDto>>();

    private static async Task<IResult> HandleAsync(NotesRepository notes, ICurrentUser user, CancellationToken ct) =>
        Results.Ok(await notes.ListTagsAsync(user.UserId!, ct));
}
