using System.Security.Claims;
using Microsoft.Identity.Web;
using Notepal.Shared;

namespace Notepal.Api.Auth;

public interface ICurrentUser
{
    /// <summary>The Entra ID object id of the signed in user, or <c>null</c> outside of an authenticated request.</summary>
    string? UserId { get; }

    /// <summary>
    /// The signed in user's normalised sign-in e-mail address (<c>preferred_username</c>, <c>email</c> or <c>upn</c>), used to
    /// match notes shared by e-mail address. <c>null</c> when the token carries none.
    /// </summary>
    string? Email => null;

    /// <summary>The signed in user's display name (<c>name</c> claim), if any.</summary>
    string? DisplayName => null;
}

public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string? UserId => GetUserId(accessor.HttpContext?.User);

    public string? Email => GetEmail(accessor.HttpContext?.User);

    public string? DisplayName
    {
        get
        {
            var user = accessor.HttpContext?.User;
            return GetUserId(user) is null ? null : (user!.FindFirst("name") ?? user.FindFirst(ClaimTypes.Name))?.Value;
        }
    }

    public static string? GetUserId(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var oid = user.GetObjectId();
        return string.IsNullOrWhiteSpace(oid) ? null : oid;
    }

    public static string? GetEmail(ClaimsPrincipal? user)
    {
        if (GetUserId(user) is null)
        {
            return null;
        }

        // The app registrations are single tenant, so these values are controlled by the organisation's directory.
        return ShareLimits.NormalizeEmail(user!.FindFirst("preferred_username")?.Value)
            ?? ShareLimits.NormalizeEmail(user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst("email")?.Value)
            ?? ShareLimits.NormalizeEmail(user.FindFirst(ClaimTypes.Upn)?.Value ?? user.FindFirst("upn")?.Value);
    }
}
