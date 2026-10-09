using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Tags.UpdateNoteTags;

internal static class UpdateNoteTagsEndpoint
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapPut("/notes/{noteId:guid}/tags", HandleAsync).Produces<NoteDto>();

    private static async Task<IResult> HandleAsync(Guid noteId, UpdateNoteTagsRequest body,
        NotesRepository notes, ICurrentUser user, CancellationToken ct)
    {
        var (tags, error) = UpdateNoteTagsValidator.Validate(body);
        if (error is not null)
        {
            return error;
        }

        var (access, denied) = await NoteAuthorization.RequireEditableAsync(notes, noteId, user, ct);
        if (access is null)
        {
            return denied;
        }

        var note = await notes.SetTagsAsync(noteId, user.ToDatabaseUser(), access, tags, ct);
        return note is null ? Results.NotFound() : Results.Ok(note);
    }
}
