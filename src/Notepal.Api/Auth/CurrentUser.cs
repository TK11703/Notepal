using System.Security.Claims;
using Microsoft.Identity.Web;

namespace Notepal.Api.Auth;

public interface ICurrentUser
{
    /// <summary>The Entra ID object id of the signed in user, or <c>null</c> outside of an authenticated request.</summary>
    string? UserId { get; }
}

public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string? UserId => GetUserId(accessor.HttpContext?.User);

    public static string? GetUserId(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var oid = user.GetObjectId();
        return string.IsNullOrWhiteSpace(oid) ? null : oid;
    }
}
