namespace Notepal.Api.Features.Sharing;

internal static class ShareValidation
{
    public static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
