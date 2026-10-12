using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Notepal.Database;

namespace Notepal.Api.Tests;

public sealed class DatabaseMigrationTests(NotepalApiFactory factory) : IClassFixture<NotepalApiFactory>
{
    [Fact]
    public async Task Library_migrations_preserve_existing_history_and_data_when_rerun()
    {
        var services = factory.Services;
        var notes = services.GetRequiredService<NotesRepository>();
        var user = new DatabaseUser(Guid.NewGuid().ToString(), null);
        var created = await notes.CreateNoteAsync(user.UserId, "Migration preservation", ["test"],
            [new NewPage("original.png", "image/png", TestFiles.Png)], CancellationToken.None);
        var access = await notes.GetAccessAsync(created.Id, user, CancellationToken.None);
        Assert.NotNull(access);
        var saved = await notes.GetNoteAsync(created.Id, user, access, CancellationToken.None);
        Assert.NotNull(saved);

        var migrator = services.GetRequiredService<DatabaseMigrator>();
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var versions = await connection.QueryAsync<string>("SELECT version FROM schema_migrations ORDER BY version");
        Assert.Equal(new[] { "0001_initial", "0002_tags_and_sharing" }, versions);

        var preserved = await notes.GetNoteAsync(created.Id, user, access, CancellationToken.None);
        Assert.NotNull(preserved);
        Assert.Equal(created.Title, preserved.Title);
        Assert.Equal(saved.CreatedAt, preserved.CreatedAt);
        Assert.Equal(created.Tags, preserved.Tags);
        Assert.Equal(Assert.Single(created.Pages).Id, Assert.Single(preserved.Pages).Id);

        var original = await notes.GetOriginalAsync(created.Id, preserved.Pages[0].Id, user, CancellationToken.None);
        Assert.NotNull(original);
        Assert.Equal(TestFiles.Png, original.Data);
    }

    [Fact]
    public void Checksums_normalize_line_endings_but_detect_content_changes()
    {
        Assert.Equal(MigrationScript.ComputeChecksum("SELECT 1;\n"), MigrationScript.ComputeChecksum("SELECT 1;\r\n"));
        Assert.NotEqual(MigrationScript.ComputeChecksum("SELECT 1;\n"), MigrationScript.ComputeChecksum("SELECT 2;\n"));
        Assert.Equal(64, MigrationScript.ComputeChecksum("SELECT 1;").Length);
    }

    [Fact]
    public async Task Records_checksums_and_applies_each_script_once_in_version_order()
    {
        await using var db = await CreateDatabaseAsync();
        MigrationScript[] scripts =
        [
            new("002_insert", "INSERT INTO demo (id) VALUES (1);"),
            new("001_create", "CREATE TABLE demo (id integer PRIMARY KEY);"),
        ];
        var migrator = CreateMigrator(db, scripts);
        await migrator.MigrateAsync();
        await using var connection = await db.OpenConnectionAsync();
        var before = (await connection.QueryAsync<History>("SELECT version, checksum, applied_at FROM schema_migrations ORDER BY version")).ToArray();
        await migrator.MigrateAsync();
        var after = (await connection.QueryAsync<History>("SELECT version, checksum, applied_at FROM schema_migrations ORDER BY version")).ToArray();
        Assert.Equal(before, after);
        Assert.Equal(scripts.OrderBy(s => s.Version).Select(s => s.Checksum), after.Select(row => row.Checksum));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM demo"));
    }

    [Fact]
    public async Task Edited_history_fails_before_applying_pending_migrations()
    {
        await using var db = await CreateDatabaseAsync();
        await CreateMigrator(db, [new("001_demo", "CREATE TABLE demo (id integer);")]).MigrateAsync();
        var exception = await Assert.ThrowsAsync<DatabaseMigrator.MigrationChecksumMismatchException>(() =>
            CreateMigrator(db,
            [
                new("000_pending", "CREATE TABLE pending (id integer);"),
                new("001_demo", "CREATE TABLE demo (id bigint);"),
            ]).MigrateAsync());
        Assert.Equal("001_demo", exception.Version);
        await using var connection = await db.OpenConnectionAsync();
        Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT to_regclass('pending')::text"));
        await AssertMigrationLockAvailableAsync(db);
    }

    [Fact]
    public async Task Legacy_history_adopts_only_pinned_checksums_without_reapplying_scripts()
    {
        await using var db = await CreateDatabaseAsync();
        var scripts = await DatabaseMigrator.ReadScriptsAsync(CancellationToken.None);
        await SeedLegacyAsync(db, scripts);
        await using var connection = await db.OpenConnectionAsync();
        var before = (await connection.QueryAsync<DateTime>("SELECT applied_at FROM schema_migrations ORDER BY version")).ToArray();
        await new DatabaseMigrator(db, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        var history = (await connection.QueryAsync<History>("SELECT version, checksum, applied_at FROM schema_migrations ORDER BY version")).ToArray();
        Assert.Equal(before, history.Select(row => row.AppliedAt));
        Assert.Equal(scripts.OrderBy(s => s.Version).Select(s => s.Checksum), history.Select(row => row.Checksum));
        await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            "UPDATE schema_migrations SET checksum = NULL WHERE version = '0001_initial'"));
        await new DatabaseMigrator(db, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
    }

    [Fact]
    public async Task Edited_legacy_script_is_rejected_without_baselining_any_history()
    {
        await using var db = await CreateDatabaseAsync();
        var scripts = await DatabaseMigrator.ReadScriptsAsync(CancellationToken.None);
        await SeedLegacyAsync(db, scripts);
        var edited = scripts.Select(s => s.Version == "0002_tags_and_sharing"
            ? new MigrationScript(s.Version, s.Body + "\n-- changed") : s).ToArray();
        await Assert.ThrowsAsync<DatabaseMigrator.MigrationChecksumMismatchException>(() => CreateMigrator(db, edited).MigrateAsync());
        await using var connection = await db.OpenConnectionAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations WHERE checksum IS NULL"));
    }

    [Fact]
    public async Task Unknown_legacy_history_fails_instead_of_trusting_current_contents()
    {
        await using var db = await CreateDatabaseAsync();
        await SeedLegacyAsync(db, [new("001_unknown", "CREATE TABLE demo (id integer);")]);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateMigrator(db, [new("001_unknown", "CREATE TABLE demo (id integer);")]).MigrateAsync());
        Assert.Contains("trusted legacy baseline", exception.Message);
    }

    [Fact]
    public async Task Missing_applied_script_fails_instead_of_silently_accepting_incomplete_history()
    {
        await using var db = await CreateDatabaseAsync();
        await CreateMigrator(db, [new("001_demo", "CREATE TABLE demo (id integer);")]).MigrateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateMigrator(db, [new("002_other", "CREATE TABLE other (id integer);")]).MigrateAsync());
    }

    [Fact]
    public async Task Failed_script_rolls_back_schema_and_history_and_can_be_retried()
    {
        await using var db = await CreateDatabaseAsync();
        MigrationScript first = new("001_demo", "CREATE TABLE demo (id integer);");
        await Assert.ThrowsAsync<PostgresException>(() => CreateMigrator(db,
            [first, new("002_other", "CREATE TABLE other (id integer); SELECT id FROM nonexistent_table;")]).MigrateAsync());
        await using var connection = await db.OpenConnectionAsync();
        Assert.Equal(["001_demo"], await connection.QueryAsync<string>("SELECT version FROM schema_migrations"));
        Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT to_regclass('other')::text"));
        await AssertMigrationLockAvailableAsync(db);
        await CreateMigrator(db, [first, new("002_other", "CREATE TABLE other (id integer);")]).MigrateAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task Concurrent_initializers_apply_each_script_exactly_once()
    {
        await using var db = await CreateDatabaseAsync();
        MigrationScript[] scripts =
        [
            new("001_demo", "CREATE TABLE demo (id integer PRIMARY KEY);"),
            new("002_insert", "INSERT INTO demo (id) VALUES (1);"),
        ];
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CreateMigrator(db, scripts).MigrateAsync()));
        await using var connection = await db.OpenConnectionAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM demo"));
        await AssertMigrationLockAvailableAsync(db);
    }

    [Fact]
    public async Task Cancellation_during_script_rolls_back_and_releases_migration_lock()
    {
        await using var db = await CreateDatabaseAsync();
        await using var blocker = await db.OpenConnectionAsync();
        const long scriptLock = 42;
        await blocker.ExecuteAsync("SELECT pg_advisory_lock(@Key)", new { Key = scriptLock });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var migration = CreateMigrator(db,
            [new("001_demo", "CREATE TABLE demo (id integer); SELECT pg_advisory_xact_lock(42);")])
            .MigrateAsync(cancellation.Token);
        try
        {
            await WaitForLockWaiterAsync(blocker, scriptLock, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration);
        }
        finally
        {
            await blocker.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", new { Key = scriptLock });
            cancellation.Cancel();
        }

        Assert.Equal(0, await blocker.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
        Assert.Null(await blocker.ExecuteScalarAsync<string?>("SELECT to_regclass('demo')::text"));
        await AssertMigrationLockAvailableAsync(db);
        await CreateMigrator(db, [new("001_demo", "CREATE TABLE demo (id integer);")]).MigrateAsync();
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_migration_lock_does_not_release_other_session_lock()
    {
        await using var db = await CreateDatabaseAsync();
        await using var blocker = await db.OpenConnectionAsync();
        var parameters = new { Key = DatabaseMigrator.AdvisoryLockKey };
        await blocker.ExecuteAsync("SELECT pg_advisory_lock(@Key)", parameters);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        MigrationScript[] scripts = [new("001_demo", "CREATE TABLE demo (id integer);")];
        var migration = CreateMigrator(db, scripts).MigrateAsync(cancellation.Token);
        try
        {
            await WaitForLockWaiterAsync(blocker, DatabaseMigrator.AdvisoryLockKey, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration);
            Assert.Equal(1, await blocker.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM pg_locks
                WHERE locktype = 'advisory' AND granted AND pid = pg_backend_pid()
                  AND ((classid::bigint << 32) | objid::bigint) = @Key
                """, parameters));
            Assert.Null(await blocker.ExecuteScalarAsync<string?>("SELECT to_regclass('schema_migrations')::text"));
        }
        finally
        {
            await blocker.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", parameters);
            cancellation.Cancel();
        }

        await CreateMigrator(db, scripts).MigrateAsync();
        await AssertMigrationLockAvailableAsync(db);
    }

    private async Task<NpgsqlDataSource> CreateDatabaseAsync()
    {
        var source = factory.Services.GetRequiredService<NpgsqlDataSource>();
        var name = $"migration_{Guid.NewGuid():N}";
        await using var connection = await source.OpenConnectionAsync();
        await connection.ExecuteAsync($"CREATE DATABASE \"{name}\"");
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var builder = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Notepal")) { Database = name };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static DatabaseMigrator CreateMigrator(NpgsqlDataSource db, IReadOnlyList<MigrationScript> scripts) =>
        new(db, NullLogger<DatabaseMigrator>.Instance, scripts);

    private static async Task SeedLegacyAsync(NpgsqlDataSource db, IReadOnlyList<MigrationScript> scripts)
    {
        await using var connection = await db.OpenConnectionAsync();
        await connection.ExecuteAsync("CREATE TABLE schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());");
        foreach (var script in scripts)
        {
            await connection.ExecuteAsync(script.Body);
            await connection.ExecuteAsync("INSERT INTO schema_migrations (version) VALUES (@Version)", new { script.Version });
        }
    }

    private static async Task AssertMigrationLockAvailableAsync(NpgsqlDataSource db)
    {
        await using var connection = await db.OpenConnectionAsync();
        var parameters = new { Key = DatabaseMigrator.AdvisoryLockKey };
        // Check the ledger lock itself: a pooled session could otherwise acquire its own leaked lock again.
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM pg_locks
            WHERE locktype = 'advisory'
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND ((classid::bigint << 32) | objid::bigint) = @Key
            """, parameters));
        Assert.True(await connection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@Key)", parameters));
        await connection.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", parameters);
    }

    private static async Task WaitForLockWaiterAsync(NpgsqlConnection connection, long key, CancellationToken ct)
    {
        while (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM pg_locks
                WHERE locktype = 'advisory' AND NOT granted
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND ((classid::bigint << 32) | objid::bigint) = @Key
            )
            """, new { Key = key }, cancellationToken: ct)))
        {
            await Task.Delay(25, ct);
        }
    }

    private sealed record History(string Version, string Checksum, DateTime AppliedAt);
}
