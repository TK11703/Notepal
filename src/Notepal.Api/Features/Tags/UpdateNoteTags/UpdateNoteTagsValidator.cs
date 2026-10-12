using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Features.Tags;
using Notepal.Contracts;

namespace Notepal.Api.Features.Tags.UpdateNoteTags;

internal static class UpdateNoteTagsValidator
{
    public static (List<string> Tags, ValidationProblem? Error) Validate(UpdateNoteTagsRequest body)
    {
        var tags = TagLimits.NormalizeAll(body.Tags);
        return (tags, tags.Count > TagLimits.MaxTagsPerNote ? TagValidation.TooManyTags() : null);
    }
}
