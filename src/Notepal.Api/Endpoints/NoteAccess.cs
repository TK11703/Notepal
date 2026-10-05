using Notepal.Shared;

namespace Notepal.Api.Endpoints;

/// <summary>The caller's role for a note and, for shared notes, who shared it.</summary>
public sealed record NoteAccess(NoteRole Role, string? SharedBy)
{
    public bool CanEdit => Role is NoteRole.Owner or NoteRole.Contributor;

    public static IResult ReadOnly() =>
        Results.Problem("You have read-only access to this note.", statusCode: StatusCodes.Status403Forbidden);

    public static IResult OwnerOnly(string action) =>
        Results.Problem($"Only the note's owner can {action}.", statusCode: StatusCodes.Status403Forbidden);
}
