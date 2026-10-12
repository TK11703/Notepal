using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Processing;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.CreateNote;

internal static class CreateNoteEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapPost("/", HandleAsync)
            .DisableAntiforgery()
            .WithName("CreateNote")
            .Produces<NoteDto>(StatusCodes.Status201Created);

    private static async Task<Results<Created<NoteDto>, ValidationProblem>> HandleAsync(
        IFormCollection form, INotesRepository notes, ICurrentUser user, ProcessingQueue queue, CancellationToken ct)
    {
        var (title, tags, pages, error) = await CreateNoteValidator.ValidateAsync(form, ct);
        if (error is not null)
        {
            return error;
        }

        var note = await notes.CreateNoteAsync(user.UserId!, title, tags, pages, ct);
        foreach (var page in note.Pages)
        {
            queue.Enqueue(page.Id);
        }

        return TypedResults.Created($"/api/notes/{note.Id}", note);
    }
}
