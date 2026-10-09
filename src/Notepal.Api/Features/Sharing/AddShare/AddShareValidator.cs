using Notepal.Contracts;

namespace Notepal.Api.Features.Sharing.AddShare;

internal static class AddShareValidator
{
    public static (string Email, string? RecipientId, IResult? Error) Validate(AddNoteShareRequest body)
    {
        var email = ShareLimits.NormalizeEmail(body.Email);
        if (email is null)
        {
            return ("", null, ShareValidation.Invalid("email", "Enter a valid email address."));
        }

        if (!Enum.IsDefined(body.Permission))
        {
            return (email, null, ShareValidation.Invalid("permission", "Permission must be Reader or Contributor."));
        }

        var recipientId = string.IsNullOrWhiteSpace(body.UserId) ? null : body.UserId.Trim();
        return (email, recipientId, recipientId is { Length: > 128 }
            ? ShareValidation.Invalid("userId", "The user id is not valid.")
            : null);
    }

    public static IResult? ValidateRecipient(string ownerId, string? ownerEmail, string email, string? recipientId) =>
        recipientId == ownerId || email == ownerEmail
            ? ShareValidation.Invalid("email", "You own this note already.")
            : null;

    public static string? NormalizeName(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null
            : trimmed.Length <= ShareLimits.MaxDisplayNameLength ? trimmed : trimmed[..ShareLimits.MaxDisplayNameLength];
    }
}
