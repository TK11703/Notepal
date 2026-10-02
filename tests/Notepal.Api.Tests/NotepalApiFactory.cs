using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notepal.Api.Ocr;
using Testcontainers.PostgreSql;

namespace Notepal.Api.Tests;

public sealed class NotepalApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Notepal", _postgres.GetConnectionString());
        builder.UseSetting("AzureAd:TenantId", "00000000-0000-0000-0000-000000000001");
        builder.UseSetting("AzureAd:ClientId", "00000000-0000-0000-0000-000000000002");
        builder.UseSetting("Ocr:ProjectEndpoint", "");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });

            services.RemoveAll<IOcrClient>();
            services.AddSingleton<IOcrClient, FakeOcrClient>();
        });
    }

    public HttpClient CreateClientFor(string? userId, string scopes = "access_as_user")
    {
        var client = CreateClient();
        if (userId is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
            client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, scopes);
        }

        return client;
    }
}

/// <summary>Authenticates requests from headers so tests can impersonate different Entra users.</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string ScopeHeader = "X-Test-Scopes";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new("oid", user.ToString()),
            new("name", $"Test user {user}"),
            new("scp", Request.Headers[ScopeHeader].ToString()),
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

public sealed class FakeOcrClient : IOcrClient
{
    public Task<string> ExtractTextAsync(ReadOnlyMemory<byte> image, string contentType, string fileName, CancellationToken cancellationToken) =>
        Task.FromResult($"Handwritten photosynthesis lecture notes from {fileName}");
}
