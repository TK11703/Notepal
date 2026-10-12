using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Common;

internal static class NoteAuthorization
{
    public static async Task<(NoteAccess? Access, ProblemHttpResult? Forbidden, bool NotFound)> RequireEditableAsync(
        INotesRepository notes, Guid noteId, ICurrentUser user, CancellationToken ct)
    {
        var access = await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct);
        if (access is null)
        {
            return (null, null, true);
        }

        return access.CanEdit
            ? (access, null, false)
            : (null, ReadOnly(), false);
    }

    public static async Task<(bool Allowed, ProblemHttpResult? Forbidden, bool NotFound)> RequireOwnerAsync(
        INotesRepository notes, Guid noteId, ICurrentUser user, CancellationToken ct)
    {
        var access = await notes.GetAccessAsync(noteId, user.ToDatabaseUser(), ct);
        if (access is null)
        {
            return (false, null, true);
        }

        return access.Role == NoteRole.Owner
            ? (true, null, false)
            : (false, OwnerOnly("change who it is shared with"), false);
    }

    public static ProblemHttpResult ReadOnly() =>
        TypedResults.Problem("You have read-only access to this note.", statusCode: StatusCodes.Status403Forbidden);

    public static ProblemHttpResult OwnerOnly(string action) =>
        TypedResults.Problem($"Only the note's owner can {action}.", statusCode: StatusCodes.Status403Forbidden);
}
