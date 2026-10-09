using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Notepal.Contracts;

namespace Notepal.Web.Services;

/// <summary>Finds people in the organization's Entra ID directory so notes can be shared with them.</summary>
public interface IDirectorySearch
{
    /// <summary>People whose name or email address starts with <paramref name="query"/>.</summary>
    /// <exception cref="DirectorySearchUnavailableException">The directory cannot be searched (e.g. permission not granted).</exception>
    Task<IReadOnlyList<DirectoryUserDto>> SearchAsync(string query, CancellationToken ct = default);
}

/// <summary>Searches users with Microsoft Graph (<c>User.ReadBasic.All</c>, delegated) on behalf of the signed in user.</summary>
public sealed class GraphDirectorySearch(IDownstreamApi api, AuthenticationStateProvider authenticationState, ILogger<GraphDirectorySearch> logger) : IDirectorySearch
{
    public const string ServiceName = "MicrosoftGraph";
    private const int MaxResults = 8;

    public async Task<IReadOnlyList<DirectoryUserDto>> SearchAsync(string query, CancellationToken ct = default)
    {
        var term = query.Trim();
        if (term.Length < 2)
        {
            return [];
        }

        if (term.Length > 100)
        {
            term = term[..100];
        }

        // OData string literals escape single quotes by doubling them; the whole filter is then URL encoded.
        var literal = term.Replace("'", "''");
        var filter = string.Join(" or ", new[] { "displayName", "givenName", "surname", "mail", "userPrincipalName" }
            .Select(property => $"startswith({property},'{literal}')"));
        var path = new StringBuilder("users?$select=id,displayName,mail,userPrincipalName")
            .Append("&$top=").Append(MaxResults)
            .Append("&$filter=").Append(Uri.EscapeDataString(filter))
            .ToString();

        var user = (await authenticationState.GetAuthenticationStateAsync()).User;
        HttpResponseMessage response;
        try
        {
            response = await api.CallApiForUserAsync(ServiceName, options => options.RelativePath = path, user, content: null, cancellationToken: ct);
        }
        catch (MicrosoftIdentityWebChallengeUserException ex)
        {
            logger.LogWarning(ex, "Directory search needs consent for Microsoft Graph");
            throw new DirectorySearchUnavailableException();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "Directory search failed");
            throw new DirectorySearchUnavailableException();
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Microsoft Graph user search failed with {Status}", (int)response.StatusCode);
                throw new DirectorySearchUnavailableException();
            }

            var page = await response.Content.ReadFromJsonAsync<GraphUsers>(ct);
            return (page?.Value ?? [])
                .Select(u => (u.Id, Name: u.DisplayName, Email: ShareLimits.NormalizeEmail(u.Mail) ?? ShareLimits.NormalizeEmail(u.UserPrincipalName)))
                .Where(u => !string.IsNullOrEmpty(u.Id) && u.Email is not null)
                .Select(u => new DirectoryUserDto(u.Id!, string.IsNullOrWhiteSpace(u.Name) ? u.Email! : u.Name, u.Email!))
                .ToList();
        }
    }

    private sealed record GraphUsers(List<GraphUser>? Value);

    private sealed record GraphUser(string? Id, string? DisplayName, string? Mail, string? UserPrincipalName);
}

public sealed class DirectorySearchUnavailableException()
    : Exception("Searching your organization's directory isn't available right now. Type the person's email address instead.");
