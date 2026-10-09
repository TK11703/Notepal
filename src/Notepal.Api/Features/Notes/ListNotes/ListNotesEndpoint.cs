using Microsoft.AspNetCore.Mvc;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.ListNotes;

internal static class ListNotesEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapGet("/", HandleAsync).Produces<PagedResult<NoteSummaryDto>>();

    private static async Task<IResult> HandleAsync(NotesRepository notes, ICurrentUser user,
        [FromQuery(Name = "tag")] string[]? tags, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        (page, pageSize) = Paging.Normalize(page, pageSize);
        return Results.Ok(await notes.ListNotesAsync(user.UserId!, TagLimits.NormalizeAll(tags), page, pageSize, ct));
    }
}
