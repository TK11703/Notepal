namespace Notepal.Database;

/// <summary>Loads immutable SQL resources once, independent of the working directory or publish layout.</summary>
public sealed class SqlFileProvider : ISqlFileProvider
{
    private const string ResourcePrefix = "scripts/";
    private readonly IReadOnlyDictionary<string, string> scripts;

    public SqlFileProvider()
    {
        var assembly = typeof(SqlFileProvider).Assembly;
        scripts = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .ToDictionary(name => name[ResourcePrefix.Length..], name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Embedded SQL script '{name}' is unavailable.");
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }, StringComparer.Ordinal);
        if (scripts.Count == 0)
        {
            throw new InvalidOperationException("No embedded SQL command scripts were found.");
        }
    }

    public string Get(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return scripts.TryGetValue(relativePath.Replace('\\', '/'), out var script)
            ? script
            : throw new FileNotFoundException($"SQL script '{relativePath}' was not found.", relativePath);
    }
}
