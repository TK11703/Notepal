using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Identity.Web;
using Notepal.Api.Auth;
using Notepal.Api.Data;
using Notepal.Api.Endpoints;
using Notepal.Api.Ocr;
using Notepal.Api.Processing;
using Notepal.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = UploadLimits.MaxRequestBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = UploadLimits.MaxRequestBytes);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Azure Container Apps terminates TLS at its ingress and forwards the original scheme/client IP.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Authentication: Entra ID access tokens issued for this API.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

var requiredScope = builder.Configuration["AzureAd:Scopes"] ?? "access_as_user";
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("NotesUser", policy => policy
        .RequireAuthenticatedUser()
        .RequireScope(requiredScope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        // Every row is keyed on the user's object id, so a token without one can never be served.
        .RequireAssertion(ctx => HttpContextCurrentUser.GetUserId(ctx.User) is not null));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

builder.Services.AddDbContext<NotepalDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Notepal"))
    // Page content is only ever reached through the (filtered) Page entity.
    .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));

builder.Services.Configure<OcrOptions>(builder.Configuration.GetSection(OcrOptions.SectionName));
if (builder.Configuration.GetSection(OcrOptions.SectionName).Get<OcrOptions>()?.IsConfigured == true)
{
    builder.Services.AddSingleton<IOcrClient, FoundryAgentOcrClient>();
}
else
{
    builder.Services.AddSingleton<IOcrClient, UnconfiguredOcrClient>();
}

builder.Services.AddScoped<TextExtractionService>();
builder.Services.AddSingleton<ProcessingQueue>();
builder.Services.AddHostedService<PageProcessingService>();

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<NotepalDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz").AllowAnonymous();

app.MapGroup("/api")
    .RequireAuthorization("NotesUser")
    .MapNotesEndpoints()
    .MapSearchEndpoints();

app.Run();

public partial class Program;
