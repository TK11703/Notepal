using Notepal.Contracts;

namespace Notepal.Api.Features.Tags;

internal static class TagValidation
{
    public static IResult TooManyTags() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["tags"] = [$"A note can have at most {TagLimits.MaxTagsPerNote} tags."] });
}
