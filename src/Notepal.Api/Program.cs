using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Notepal.Api.Auth;
using Notepal.Api.Data;
using Notepal.Api.Endpoints;
using Notepal.Api.Ocr;
using Notepal.Api.Processing;
using Notepal.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddValidation();

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

// Registers NpgsqlDataSource from ConnectionStrings:notepal (injected by the Aspire AppHost locally) with health checks and tracing.
builder.AddNpgsqlDataSource("notepal");
DapperConfiguration.Apply();
builder.Services.AddSingleton<DatabaseMigrator>();
builder.Services.AddSingleton<NotesRepository>();
builder.Services.AddSingleton<PageWorkRepository>();

builder.Services.Configure<OcrOptions>(builder.Configuration.GetSection(OcrOptions.SectionName));
if (builder.Configuration.GetSection(OcrOptions.SectionName).Get<OcrOptions>()?.IsConfigured == true)
{
    builder.Services.AddSingleton<IOcrClient, FoundryAgentOcrClient>();
}
else
{
    builder.Services.AddSingleton<IOcrClient, UnconfiguredOcrClient>();
}

builder.Services.AddSingleton<TextExtractionService>();
builder.Services.AddSingleton<ProcessingQueue>();
builder.Services.AddHostedService<PageProcessingService>();

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
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

// Liveness only: a database outage must not make Container Apps restart the replica.
app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") }).AllowAnonymous();
app.MapDefaultEndpoints();

app.MapGroup("/api")
    .RequireAuthorization("NotesUser")
    .MapNotesEndpoints()
    .MapSharingEndpoints()
    .MapSearchEndpoints();

app.Run();

public partial class Program;
