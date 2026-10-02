using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;
using Notepal.Web.Components;
using Notepal.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var apiScopes = builder.Configuration.GetSection($"{NotepalApiClient.ServiceName}:Scopes").Get<string[]>() ?? [];

// Sign users in with Entra ID and acquire tokens to call Notepal.Api on their behalf.
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi(apiScopes)
    .AddDownstreamApi(NotepalApiClient.ServiceName, builder.Configuration.GetSection(NotepalApiClient.ServiceName))
    .AddInMemoryTokenCaches();

builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Events = new RejectSessionCookieWhenAccountNotInCacheEvents(apiScopes);
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Azure Container Apps terminates TLS at its ingress; the OIDC redirect URI must keep the https scheme.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHealthChecks();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<NotepalApiClient>();

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/healthz").AllowAnonymous();

var auth = app.MapGroup("/authentication");
auth.MapGet("/login", (string? returnUrl) =>
        TypedResults.Challenge(new AuthenticationProperties { RedirectUri = LocalUrl(returnUrl) }, [OpenIdConnectDefaults.AuthenticationScheme]))
    .AllowAnonymous();
auth.MapPost("/logout", ([FromForm] string? returnUrl) =>
        TypedResults.SignOut(new AuthenticationProperties { RedirectUri = LocalUrl(returnUrl) },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]))
    .RequireAuthorization();

// Streams an original artifact from the API so <img>/<iframe> elements can display it with the user's cookie.
app.MapGet("/files/notes/{noteId:guid}/pages/{pageId:guid}", async (Guid noteId, Guid pageId, HttpContext context, NotepalApiClient client) =>
{
    try
    {
        using var response = await client.GetOriginalAsync(context.User, noteId, pageId, context.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            return Results.StatusCode((int)response.StatusCode);
        }

        var headers = context.Response.Headers;
        headers.ContentDisposition = response.Content.Headers.ContentDisposition?.ToString();
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "private, max-age=3600";
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return Results.Bytes(await response.Content.ReadAsByteArrayAsync(context.RequestAborted), contentType);
    }
    catch (ReauthenticationRequiredException)
    {
        return Results.Unauthorized();
    }
}).RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string LocalUrl(string? returnUrl) =>
    !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
        ? returnUrl
        : "/";
