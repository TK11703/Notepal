using Notepal.Api.Processing;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes;

internal static class NoteUploads
{
    private const int MaxFileNameLength = 260;

    public static async Task<(List<NewPage> Pages, List<string> Errors)> ReadFilesAsync(IFormFileCollection files, CancellationToken ct)
    {
        var pages = new List<NewPage>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file.FileName);
            if (fileName.Length > MaxFileNameLength)
            {
                fileName = fileName[..MaxFileNameLength];
            }

            if (file.Length == 0 || file.Length > UploadLimits.MaxFileBytes)
            {
                errors.Add($"'{fileName}' must be between 1 byte and {UploadLimits.MaxFileBytes / (1024 * 1024)} MB.");
                continue;
            }

            byte[] data;
            using (var buffer = new MemoryStream((int)file.Length))
            {
                await file.CopyToAsync(buffer, ct);
                data = buffer.ToArray();
            }

            if (!FileTypes.TryResolve(fileName, data, out var contentType))
            {
                errors.Add($"'{fileName}' is not a supported file. Upload JPEG, PNG, WebP or GIF images, PDF or Word (.docx) documents.");
                continue;
            }

            pages.Add(new NewPage(fileName, contentType, data));
        }

        return (pages, errors);
    }

    public static IResult FilesProblem(params string[] errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["files"] = errors });
}
