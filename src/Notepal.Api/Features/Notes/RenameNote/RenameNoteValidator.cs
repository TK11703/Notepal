using Notepal.Contracts;

namespace Notepal.Api.Features.Notes.RenameNote;

internal static class RenameNoteValidator
{
    public static (string Title, IResult? Error) Validate(UpdateNoteRequest body)
    {
        var title = body.Title.Trim();
        return (title, title.Length == 0
            ? Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Title is required."] })
            : null);
    }
}
