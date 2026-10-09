using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.RenameNote;

internal static class RenameNoteEndpoint
{
    public static void Map(RouteGroupBuilder notes) =>
        notes.MapPut("/{noteId:guid}", HandleAsync).Produces<NoteDto>();

    private static async Task<IResult> HandleAsync(Guid noteId, UpdateNoteRequest body, NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (title, error) = RenameNoteValidator.Validate(body);
        if (error is not null)
        {
            return error;
        }

        var (access, denied) = await NoteAuthorization.RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var note = await notes.RenameNoteAsync(noteId, user.ToDatabaseUser(), access, title, ct);
        return note is null ? Results.NotFound() : Results.Ok(note);
    }
}
