using Notepal.Api.Auth;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Common;

internal static class NoteAuthorization
{
    public static async Task<(NoteAccess? Access, IResult Denied)> RequireEditableAsync(
        NotesRepository notes, Guid noteId, ICurrentUser user, CancellationToken ct)
    {
        var access = await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct);
        return access is null ? (null, Results.NotFound())
            : access.CanEdit ? (access, Results.Empty)
            : (null, ReadOnly());
    }

    public static async Task<IResult?> RequireOwnerAsync(
        NotesRepository notes, Guid noteId, ICurrentUser user, CancellationToken ct)
    {
        var access = await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct);
        return access is null ? Results.NotFound()
            : access.Role == NoteRole.Owner ? null
            : OwnerOnly("change who it is shared with");
    }

    public static IResult ReadOnly() =>
        Results.Problem("You have read-only access to this note.", statusCode: StatusCodes.Status403Forbidden);

    public static IResult OwnerOnly(string action) =>
        Results.Problem($"Only the note's owner can {action}.", statusCode: StatusCodes.Status403Forbidden);
}
