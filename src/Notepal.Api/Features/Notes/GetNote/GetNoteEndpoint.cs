using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.GetNote;

internal static class GetNoteEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapGet("/{noteId:guid}", HandleAsync)
            .WithName("GetNote")
            .Produces<NoteDto>();

    private static async Task<Results<Ok<NoteDto>, NotFound>> HandleAsync(
        Guid noteId, INotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var caller = user.ToDatabaseUser();
        var access = await notes.GetAccessAsync(noteId, caller, ct);
        var note = access is null ? null : await notes.GetNoteAsync(noteId, caller, access, ct);
        return note is null ? TypedResults.NotFound() : TypedResults.Ok(note);
    }
}
