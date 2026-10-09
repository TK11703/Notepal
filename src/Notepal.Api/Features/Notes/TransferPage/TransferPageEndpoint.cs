using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.TransferPage;

internal static class TransferPageEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapPost("/{noteId:guid}/pages/{pageId:guid}/transfer", HandleAsync).Produces<NoteDto>();

    private static async Task<IResult> HandleAsync(Guid noteId, Guid pageId, TransferPageRequest body,
        NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var error = TransferPageValidator.Validate(noteId, body);
        if (error is not null)
        {
            return error;
        }

        var access = await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct);
        if (access is null)
        {
            return Results.NotFound();
        }

        if (access.Role != NoteRole.Owner)
        {
            return NoteAuthorization.OwnerOnly("move its pages to another note");
        }

        var (outcome, note) = await notes.TransferPageAsync(noteId, pageId, body.TargetNoteId, user.ToDatabaseUser(), access, ct);
        return outcome switch
        {
            PageChangeOutcome.Changed => Results.Ok(note),
            PageChangeOutcome.LastPage => Results.Conflict(new { message = "A note needs at least one page. Add another page first, or delete the note instead." }),
            PageChangeOutcome.TargetFull => Results.Conflict(new { message = $"The other note already has the maximum of {UploadLimits.MaxFilesPerNote} pages." }),
            _ => Results.NotFound(),
        };
    }
}
