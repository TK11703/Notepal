using System.Security.Cryptography;
using System.Text;

namespace Notepal.Database;

internal sealed record MigrationScript(string Version, string Body)
{
    public string Checksum { get; } = ComputeChecksum(Body);

    internal static string ComputeChecksum(string body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            body.Replace("\r\n", "\n", StringComparison.Ordinal))));
}
