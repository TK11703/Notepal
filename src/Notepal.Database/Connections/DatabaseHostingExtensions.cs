using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Notepal.Database;

public static class DatabaseHostingExtensions
{
    public static TBuilder AddNotepalDatabase<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Aspire supplies ConnectionStrings:notepal locally, along with health checks and tracing.
        builder.AddNpgsqlDataSource("notepal", configureDataSourceBuilder: dataSource =>
        {
            // Without a password, authenticate to Azure PostgreSQL using managed identity.
            if (string.IsNullOrEmpty(dataSource.ConnectionStringBuilder.Password))
            {
                var clientId = builder.Configuration["Database:ManagedIdentityClientId"];
                TokenCredential credential = !string.IsNullOrWhiteSpace(clientId)
                    ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
                    : new DefaultAzureCredential();
                var tokenRequest = new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]);
                dataSource.UsePeriodicPasswordProvider(
                    async (_, ct) => (await credential.GetTokenAsync(tokenRequest, ct)).Token,
                    TimeSpan.FromMinutes(30),
                    TimeSpan.FromSeconds(5));
            }
        });

        DapperConfiguration.Apply();
        builder.Services.AddSingleton<DatabaseMigrator>();
        builder.Services.AddSingleton<NotesRepository>();
        builder.Services.AddSingleton<SharesRepository>();
        builder.Services.AddSingleton<PageWorkRepository>();
        return builder;
    }
}
