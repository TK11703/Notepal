using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.RenameNote;

internal static class RenameNoteEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapPut("/{noteId:guid}", HandleAsync)
            .WithName("RenameNote")
            .Produces<NoteDto>();

    private static async Task<Results<Ok<NoteDto>, ValidationProblem, NotFound, ProblemHttpResult>> HandleAsync(
        Guid noteId, UpdateNoteRequest body, INotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (title, error) = RenameNoteValidator.Validate(body);
        if (error is not null)
        {
            return error;
        }

        var (access, forbidden, notFound) = await NoteAuthorization.RequireEditableAsync(notes, noteId, user, ct);
        if (notFound)
        {
            return TypedResults.NotFound();
        }
        if (forbidden is not null)
        {
            return forbidden;
        }

        var note = await notes.RenameNoteAsync(noteId, user.ToDatabaseUser(), access!, title, ct);
        return note is null ? TypedResults.NotFound() : TypedResults.Ok(note);
    }
}
