using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace Notepal.Web.Services;

/// <summary>
/// The token cache lives in memory, so it is lost whenever the container restarts or scales to zero while the
/// auth cookie survives. Reject such cookies so the user is transparently sent back through Entra sign-in
/// (normally silent thanks to SSO) instead of failing on the first API call.
/// </summary>
public sealed class RejectSessionCookieWhenAccountNotInCacheEvents(string[] scopes) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        try
        {
            var tokenAcquisition = context.HttpContext.RequestServices.GetRequiredService<ITokenAcquisition>();
            await tokenAcquisition.GetAccessTokenForUserAsync(scopes, user: context.Principal);
        }
        catch (MicrosoftIdentityWebChallengeUserException ex) when (AccountDoesNotExistInTokenCache(ex))
        {
            context.RejectPrincipal();
        }
    }

    private static bool AccountDoesNotExistInTokenCache(MicrosoftIdentityWebChallengeUserException ex) =>
        ex.InnerException is MsalUiRequiredException { ErrorCode: "user_null" };
}
