using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Contracts;

namespace Notepal.Api.Features.Notes.TransferPage;

internal static class TransferPageValidator
{
    public static ValidationProblem? Validate(Guid noteId, TransferPageRequest body) =>
        body.TargetNoteId == Guid.Empty || body.TargetNoteId == noteId
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["targetNoteId"] = ["Choose a different note."] })
            : null;
}
