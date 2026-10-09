using Microsoft.AspNetCore.Mvc;
using Notepal.Api.Auth;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.GetNoteStats;

internal static class GetNoteStatsEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapGet("/stats", HandleAsync).Produces<NoteStatsDto>();

    private static async Task<IResult> HandleAsync(NotesRepository notes, ICurrentUser user,
        [FromQuery(Name = "tag")] string[]? tags, CancellationToken ct) =>
        Results.Ok(await notes.GetStatsAsync(user.UserId!, TagLimits.NormalizeAll(tags), ct));
}
