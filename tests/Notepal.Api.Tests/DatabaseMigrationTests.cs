using Dapper;
using Microsoft.Extensions.DependencyInjection;
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
}
