using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Notepal.Database;

/// <summary>Applies the embedded <c>Scripts/Schema/*.sql</c> scripts in name order, each exactly once.</summary>
public sealed class DatabaseMigrator
{
    private const string ResourcePrefix = "migrations/";
    internal const long AdvisoryLockKey = 0x4E6F746570616C; // "Notepal"
    private readonly NpgsqlDataSource dataSource;
    private readonly ILogger<DatabaseMigrator> logger;
    private readonly IReadOnlyList<MigrationScript>? scripts;
    private readonly ISqlFileProvider sql;

    public DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> logger)
        : this(dataSource, logger, new SqlFileProvider(), null) { }

    public DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> logger, ISqlFileProvider sql)
        : this(dataSource, logger, sql, null) { }

    internal DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> logger,
        IReadOnlyList<MigrationScript>? scripts)
        : this(dataSource, logger, new SqlFileProvider(), scripts) { }

    private DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> logger,
        ISqlFileProvider sql, IReadOnlyList<MigrationScript>? scripts)
    {
        this.dataSource = dataSource;
        this.logger = logger;
        this.scripts = scripts;
        this.sql = sql;
    }

    // Trusted contents of the two releases that predate checksum tracking. Never update these hashes.
    private static readonly IReadOnlyDictionary<string, string> LegacyChecksums = new Dictionary<string, string>
    {
        ["0001_initial"] = "e364f7cd8d6cea48bac4fd932f302a4c971b0144d9653ba633a5238ec94d9f74",
        ["0002_tags_and_sharing"] = "1b2dc03f3afaf4b7b200b9c8b654372f94d3e3c7b95a5a014b5517e491e4f29f",
    };

    public sealed class MigrationChecksumMismatchException(string version)
        : InvalidOperationException($"Migration '{version}' does not match its recorded history. Add a new migration instead of editing an applied script.")
    {
        public string Version { get; } = version;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var lockKey = new { Key = AdvisoryLockKey };

        // Serializes replicas that start at the same time.
        await connection.ExecuteAsync(new CommandDefinition(sql.Get("Commands/Initialization/AcquireMigrationLock.sql"), lockKey, cancellationToken: cancellationToken));
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                sql.Get("Commands/Initialization/EnsureMigrationLedger.sql"), cancellationToken: cancellationToken));

            var applied = (await connection.QueryAsync<MigrationHistory>(new CommandDefinition(
                sql.Get("Commands/Initialization/GetMigrationHistory.sql"), cancellationToken: cancellationToken)))
                .ToDictionary(row => row.Version, StringComparer.Ordinal);
            var orderedScripts = (scripts ?? await ReadScriptsAsync(cancellationToken))
                .OrderBy(script => script.Version, StringComparer.Ordinal).ToArray();
            var byVersion = orderedScripts.ToDictionary(script => script.Version, StringComparer.Ordinal);

            // Validate all history before applying any pending scripts or adopting legacy checksums.
            foreach (var row in applied.Values)
            {
                if (!byVersion.TryGetValue(row.Version, out var script))
                {
                    throw new InvalidOperationException($"Applied migration '{row.Version}' is missing from this release.");
                }

                var expected = row.Checksum;
                if (expected is null && !LegacyChecksums.TryGetValue(row.Version, out expected))
                {
                    throw new InvalidOperationException($"Applied migration '{row.Version}' has no checksum or trusted legacy baseline.");
                }

                if (!string.Equals(expected, script.Checksum, StringComparison.Ordinal))
                {
                    throw new MigrationChecksumMismatchException(row.Version);
                }
            }

            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                foreach (var row in applied.Values.Where(row => row.Checksum is null))
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        sql.Get("Commands/Initialization/AdoptLegacyChecksum.sql"),
                        new { row.Version, Checksum = byVersion[row.Version].Checksum }, transaction,
                        cancellationToken: cancellationToken));
                    logger.LogInformation("Adopted trusted legacy checksum for migration {Version}", row.Version);
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    sql.Get("Commands/Initialization/RequireMigrationChecksum.sql"),
                    transaction: transaction, cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);
            }

            foreach (var script in orderedScripts)
            {
                if (applied.ContainsKey(script.Version))
                {
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(script.Body, transaction: transaction, cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    sql.Get("Commands/Initialization/RecordMigration.sql"),
                    new { script.Version, script.Checksum }, transaction, cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation("Applied database migration {Version}", script.Version);
            }

        }
        finally
        {
            // Release the session lock even after caller cancellation, with a bounded cleanup budget.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.ExecuteAsync(new CommandDefinition(sql.Get("Commands/Initialization/ReleaseMigrationLock.sql"), lockKey, cancellationToken: cleanup.Token));
        }
    }

    internal static async Task<IReadOnlyList<MigrationScript>> ReadScriptsAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        var scripts = new List<MigrationScript>();
        foreach (var resource in assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            await using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded migration '{resource}' is unavailable.");
            using var reader = new StreamReader(stream);
            scripts.Add(new MigrationScript(Path.GetFileNameWithoutExtension(resource[ResourcePrefix.Length..]),
                await reader.ReadToEndAsync(cancellationToken)));
        }

        if (scripts.Count == 0)
        {
            throw new InvalidOperationException("No embedded database migrations were found.");
        }

        return scripts;
    }

    private sealed class MigrationHistory
    {
        public string Version { get; set; } = null!;
        public string? Checksum { get; set; }
    }
}
