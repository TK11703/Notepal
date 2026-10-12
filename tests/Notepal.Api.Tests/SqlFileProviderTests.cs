using Notepal.Database;

namespace Notepal.Api.Tests;

public sealed class SqlFileProviderTests
{
    [Fact]
    public void Every_command_is_embedded_and_available_by_relative_path()
    {
        var provider = new SqlFileProvider();
        var resources = typeof(SqlFileProvider).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("scripts/", StringComparison.Ordinal)).ToArray();

        Assert.Equal(45, resources.Length);
        foreach (var resource in resources)
        {
            Assert.StartsWith("scripts/Commands/", resource);
            var path = resource["scripts/".Length..];
            Assert.False(string.IsNullOrWhiteSpace(provider.Get(path)));
            Assert.Same(provider.Get(path), provider.Get(path.Replace('/', '\\')));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Invalid_paths_fail_explicitly(string? path)
    {
        var provider = new SqlFileProvider();
        Assert.ThrowsAny<ArgumentException>(() => provider.Get(path!));
    }

    [Theory]
    [InlineData("Commands/Notes/Missing.sql")]
    [InlineData("Commands/Notes/listnotes.sql")]
    [InlineData("Schema/0001_initial.sql")]
    public void Missing_paths_fail_without_filesystem_fallback(string path)
    {
        var exception = Assert.Throws<FileNotFoundException>(() => new SqlFileProvider().Get(path));
        Assert.Equal(path, exception.FileName);
    }
}
