using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Notepal.Database;

/// <summary>Applies the embedded <c>Migrations/*.sql</c> scripts in name order, each exactly once.</summary>
public sealed class DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> logger)
{
    private const string ResourcePrefix = "migrations/";
    private const long AdvisoryLockKey = 0x4E6F746570616C; // "Notepal"

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var lockKey = new { Key = AdvisoryLockKey };

        // Serializes replicas that start at the same time.
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@Key)", lockKey, cancellationToken: cancellationToken));
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    text PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """, cancellationToken: cancellationToken));

            var applied = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT version FROM schema_migrations", cancellationToken: cancellationToken))).ToHashSet(StringComparer.Ordinal);

            var assembly = Assembly.GetExecutingAssembly();
            var scripts = assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal);

            foreach (var resource in scripts)
            {
                var version = Path.GetFileNameWithoutExtension(resource[ResourcePrefix.Length..]);
                if (applied.Contains(version))
                {
                    continue;
                }

                string script;
                await using (var stream = assembly.GetManifestResourceStream(resource)!)
                using (var reader = new StreamReader(stream))
                {
                    script = await reader.ReadToEndAsync(cancellationToken);
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(script, transaction: transaction, cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_migrations (version) VALUES (@Version)", new { Version = version }, transaction, cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation("Applied database migration {Version}", version);
            }
        }
        finally
        {
            // Release the session lock even after caller cancellation, with a bounded cleanup budget.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_unlock(@Key)", lockKey, cancellationToken: cleanup.Token));
        }
    }
}
