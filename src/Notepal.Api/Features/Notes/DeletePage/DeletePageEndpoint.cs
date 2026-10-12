using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.DeletePage;

internal static class DeletePageEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapDelete("/{noteId:guid}/pages/{pageId:guid}", HandleAsync)
            .WithName("DeletePage")
            .Produces<NoteDto>()
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<Results<Ok<NoteDto>, NotFound, ProblemHttpResult>> HandleAsync(
        Guid noteId, Guid pageId, INotesRepository notes, ICurrentUser user, CancellationToken ct)
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

        var (outcome, note) = await notes.DeletePageAsync(noteId, pageId, user.ToDatabaseUser(), access!, ct);
        return outcome switch
        {
            PageChangeOutcome.LastPage => TypedResults.Problem("A note needs at least one page. Delete the note instead.", statusCode: StatusCodes.Status409Conflict),
            PageChangeOutcome.Changed => TypedResults.Ok(note!),
            _ => TypedResults.NotFound(),
        };
    }
}
