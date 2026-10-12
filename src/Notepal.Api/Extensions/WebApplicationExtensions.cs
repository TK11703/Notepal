using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Notepal.Api.Features.Notes;
using Notepal.Api.Features.Search;
using Notepal.Api.Features.Sharing;
using Notepal.Api.Features.Tags;
using Notepal.Database;

namespace Notepal.Api.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication UseNotepalApi(this WebApplication app)
    {
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
        // Readiness includes PostgreSQL. Return status only, never dependency or exception details.
        app.MapHealthChecks("/readyz").AllowAnonymous();
        app.MapDefaultEndpoints();

        app.MapGroup("/api")
            .RequireAuthorization(WebApplicationBuilderExtensions.NotesUserPolicy)
            .MapNotesEndpoints()
            .MapTagsEndpoints()
            .MapSharingEndpoints()
            .MapSearchEndpoints();

        return app;
    }

    public static async Task<WebApplication> InitializeDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
        {
            await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(cancellationToken);
        }

        return app;
    }
}
