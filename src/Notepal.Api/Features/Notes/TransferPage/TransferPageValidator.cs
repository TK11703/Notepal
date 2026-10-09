using Notepal.Contracts;

namespace Notepal.Api.Features.Notes.TransferPage;

internal static class TransferPageValidator
{
    public static IResult? Validate(Guid noteId, TransferPageRequest body) =>
        body.TargetNoteId == Guid.Empty || body.TargetNoteId == noteId
            ? Results.ValidationProblem(new Dictionary<string, string[]> { ["targetNoteId"] = ["Choose a different note."] })
            : null;
}
