using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Api.Processing;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.ReprocessPage;

internal static class ReprocessPageEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapPost("/{noteId:guid}/pages/{pageId:guid}/reprocess", HandleAsync)
            .WithName("ReprocessPage")
            .Produces<PageDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<Results<Accepted<PageDto>, NotFound, ProblemHttpResult>> HandleAsync(Guid noteId, Guid pageId,
        INotesRepository notes, ICurrentUser user, ProcessingQueue queue, CancellationToken ct)
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

        var (outcome, page) = await notes.ResetForReprocessingAsync(noteId, pageId, user.ToDatabaseUser(), ct);
        switch (outcome)
        {
            case ReprocessOutcome.NotFound:
                return TypedResults.NotFound();
            case ReprocessOutcome.AlreadyProcessing:
                return TypedResults.Problem("The page is already being processed.", statusCode: StatusCodes.Status409Conflict);
        }

        queue.Enqueue(page!.Id);
        return TypedResults.Accepted((string?)null, page);
    }
}
