using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Notepal.Api.Auth;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.GetNoteStats;

internal static class GetNoteStatsEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapGet("/stats", HandleAsync)
            .WithName("GetNoteStats")
            .Produces<NoteStatsDto>();

    private static async Task<Ok<NoteStatsDto>> HandleAsync(INotesRepository notes, ICurrentUser user,
        [FromQuery(Name = "tag")] string[]? tags, CancellationToken ct) =>
        TypedResults.Ok(await notes.GetStatsAsync(user.UserId!, TagLimits.NormalizeAll(tags), ct));
}
