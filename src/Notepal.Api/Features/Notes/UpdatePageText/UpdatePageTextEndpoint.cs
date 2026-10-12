using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.UpdatePageText;

internal static class UpdatePageTextEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapPut("/{noteId:guid}/pages/{pageId:guid}/text", HandleAsync)
            .WithName("UpdatePageText")
            .Produces<PageDto>();

    private static async Task<Results<Ok<PageDto>, NotFound, ProblemHttpResult>> HandleAsync(Guid noteId, Guid pageId, UpdatePageTextRequest body,
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

        var page = await notes.UpdatePageTextAsync(noteId, pageId, user.ToDatabaseUser(), body.Text, ct);
        return page is null ? TypedResults.NotFound() : TypedResults.Ok(page);
    }
}
