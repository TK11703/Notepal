using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Contracts;

namespace Notepal.Api.Features.Sharing.UpdateShare;

internal static class UpdateShareValidator
{
    public static ValidationProblem? Validate(UpdateNoteShareRequest body) =>
        !Enum.IsDefined(body.Permission)
            ? ShareValidation.Invalid("permission", "Permission must be Reader or Contributor.")
            : null;
}
