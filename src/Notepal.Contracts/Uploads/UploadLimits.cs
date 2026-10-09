namespace Notepal.Contracts;

public static class UploadLimits
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    public const int MaxFilesPerNote = 20;
    public const long MaxRequestBytes = 100 * 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    };

    public const string AcceptAttribute = "image/*,.pdf,.docx,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document";
}
