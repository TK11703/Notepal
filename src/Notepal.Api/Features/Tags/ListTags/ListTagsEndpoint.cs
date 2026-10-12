using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Tags.ListTags;

internal static class ListTagsEndpoint
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/tags", HandleAsync)
            .WithName("ListTags")
            .Produces<List<TagDto>>();

    private static async Task<Ok<List<TagDto>>> HandleAsync(INotesRepository notes, ICurrentUser user, CancellationToken ct) =>
        TypedResults.Ok(await notes.ListTagsAsync(user.UserId!, ct));
}
