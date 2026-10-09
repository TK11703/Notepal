namespace Notepal.Api.Endpoints;

internal static class NoteAccessResults
{
    public static IResult ReadOnly() =>
        Results.Problem("You have read-only access to this note.", statusCode: StatusCodes.Status403Forbidden);

    public static IResult OwnerOnly(string action) =>
        Results.Problem($"Only the note's owner can {action}.", statusCode: StatusCodes.Status403Forbidden);
}
