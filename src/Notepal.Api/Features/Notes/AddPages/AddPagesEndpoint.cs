using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Api.Processing;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.AddPages;

internal static class AddPagesEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapPost("/{noteId:guid}/pages", HandleAsync)
            .DisableAntiforgery()
            .WithName("AddPages")
            .Produces<NoteDto>();

    private static async Task<Results<Ok<NoteDto>, ValidationProblem, NotFound, ProblemHttpResult>> HandleAsync(Guid noteId, IFormFileCollection files,
        INotesRepository notes, ICurrentUser user, ProcessingQueue queue, CancellationToken ct)
    {
        var (pages, error) = await AddPagesValidator.ValidateAsync(files, ct);
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

        var result = await notes.AddPagesAsync(noteId, user.ToDatabaseUser(), access!, pages, ct);
        switch (result.Outcome)
        {
            case AddPagesOutcome.NotFound:
                return TypedResults.NotFound();
            case AddPagesOutcome.TooMany:
                return NoteUploads.FilesProblem(result.Remaining == 0
                    ? $"This note already has the maximum of {UploadLimits.MaxFilesPerNote} pages."
                    : $"A note can contain at most {UploadLimits.MaxFilesPerNote} pages. You can add {result.Remaining} more.");
        }

        foreach (var pageId in result.PageIds!)
        {
            queue.Enqueue(pageId);
        }

        return TypedResults.Ok(result.Note!);
    }
}
