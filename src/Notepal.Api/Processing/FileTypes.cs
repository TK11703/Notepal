using Notepal.Contracts;

namespace Notepal.Api.Processing;

public static class FileTypes
{
    public const string Pdf = "application/pdf";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public static bool IsImage(string contentType) => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the canonical content type from the file extension and verifies that the file's leading bytes
    /// match that type, so a renamed executable or HTML file can never be stored and served back as an image.
    /// </summary>
    public static bool TryResolve(string fileName, ReadOnlySpan<byte> header, out string contentType)
    {
        contentType = string.Empty;
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension) || !UploadLimits.AllowedTypes.TryGetValue(extension, out var resolved))
        {
            return false;
        }

        var matches = resolved switch
        {
            "image/jpeg" => header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
            "image/png" => header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "image/gif" => header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8),
            "image/webp" => header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8),
            Pdf => header.StartsWith("%PDF-"u8),
            Docx => header.StartsWith((ReadOnlySpan<byte>)[0x50, 0x4B, 0x03, 0x04]),
            _ => false,
        };

        if (matches)
        {
            contentType = resolved;
        }

        return matches;
    }
}
