using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.MovePage;

internal static class MovePageEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapPut("/{noteId:guid}/pages/{pageId:guid}/position", HandleAsync)
            .WithName("MovePage")
            .Produces<NoteDto>();

    private static async Task<Results<Ok<NoteDto>, NotFound, ProblemHttpResult>> HandleAsync(Guid noteId, Guid pageId, MovePageRequest body,
        INotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (access, forbidden, notFound) = await NoteAuthorization.RequireEditableAsync(notes, noteId, user, ct);
        if (notFound)
        {
            return TypedResults.NotFound();
        }
        if (forbidden is not null)
        {
            return forbidden;
        }

        var (outcome, note) = await notes.MovePageAsync(noteId, pageId, body.PageNumber, user.ToDatabaseUser(), access!, ct);
        return outcome == PageChangeOutcome.Changed ? TypedResults.Ok(note!) : TypedResults.NotFound();
    }
}
