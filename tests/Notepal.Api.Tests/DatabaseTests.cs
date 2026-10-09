using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Notepal.Api.Auth;
using Notepal.Database;

namespace Notepal.Api.Tests;

public sealed class DatabaseTests
{
    [Fact]
    public void Database_library_has_no_API_Web_or_HTTP_dependencies()
    {
        var references = typeof(NotesRepository).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name is "Notepal.Api" or "Notepal.Web");
        Assert.DoesNotContain(references, reference => reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Migrations_are_embedded_in_the_database_library_with_existing_resource_names()
    {
        var resources = typeof(DatabaseMigrator).Assembly.GetManifestResourceNames().Order(StringComparer.Ordinal);
        Assert.Equal(new[]
        {
            "migrations/0001_initial.sql",
            "migrations/0002_tags_and_sharing.sql",
        }, resources);
        Assert.DoesNotContain(typeof(Program).Assembly.GetManifestResourceNames(),
            resource => resource.StartsWith("migrations/", StringComparison.Ordinal));
    }

    [Fact]
    public void Database_registration_supplies_the_data_source_and_singleton_repositories()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:notepal"] = "Host=localhost;Database=notepal;Username=notepal;Password=test";

        Assert.Same(builder, builder.AddNotepalDatabase());
        using var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<NpgsqlDataSource>());
        Assert.NotNull(host.Services.GetRequiredService<DatabaseMigrator>());
        Assert.Same(host.Services.GetRequiredService<NotesRepository>(), host.Services.GetRequiredService<NotesRepository>());
        Assert.Same(host.Services.GetRequiredService<SharesRepository>(), host.Services.GetRequiredService<SharesRepository>());
        Assert.Same(host.Services.GetRequiredService<PageWorkRepository>(), host.Services.GetRequiredService<PageWorkRepository>());
    }

    [Fact]
    public void API_passes_caller_identity_without_HTTP_or_claim_dependencies()
    {
        ICurrentUser user = new TestCurrentUser("user-id", "user@example.com");

        Assert.Equal(new DatabaseUser("user-id", "user@example.com"), user.ToDatabaseUser());
        Assert.Equal(new DatabaseUser("user-id", null), new TestCurrentUser("user-id", null).ToDatabaseUser());
    }

    [Fact]
    public void API_rejects_database_identity_without_a_user_object_id()
    {
        Assert.Throws<InvalidOperationException>(() => new TestCurrentUser(null, null).ToDatabaseUser());
    }

    private sealed record TestCurrentUser(string? UserId, string? Email) : ICurrentUser;
}
