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
            .Produces<PageDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> HandleAsync(Guid noteId, Guid pageId,
        NotesRepository notes, ICurrentUser user, ProcessingQueue queue, CancellationToken ct)
    {
        var (access, denied) = await NoteAuthorization.RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var (outcome, page) = await notes.ResetForReprocessingAsync(noteId, pageId, user.ToDatabaseUser(), ct);
        switch (outcome)
        {
            case ReprocessOutcome.NotFound:
                return Results.NotFound();
            case ReprocessOutcome.AlreadyProcessing:
                return Results.Problem("The page is already being processed.", statusCode: StatusCodes.Status409Conflict);
        }

        queue.Enqueue(page!.Id);
        return Results.Accepted(value: page);
    }
}
