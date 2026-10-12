using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Tags.UpdateNoteTags;

internal static class UpdateNoteTagsEndpoint
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapPut("/notes/{noteId:guid}/tags", HandleAsync)
            .WithName("UpdateNoteTags")
            .Produces<NoteDto>();

    private static async Task<Results<Ok<NoteDto>, ValidationProblem, NotFound, ProblemHttpResult>> HandleAsync(Guid noteId, UpdateNoteTagsRequest body,
        INotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (tags, error) = UpdateNoteTagsValidator.Validate(body);
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

        var note = await notes.SetTagsAsync(noteId, user.ToDatabaseUser(), access!, tags, ct);
        return note is null ? TypedResults.NotFound() : TypedResults.Ok(note);
    }
}
