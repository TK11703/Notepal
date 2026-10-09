using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.AddPages;

internal static class AddPagesValidator
{
    public static async Task<(List<NewPage> Pages, IResult? Error)> ValidateAsync(
        IFormFileCollection files, CancellationToken ct)
    {
        if (files.Count == 0)
        {
            return ([], NoteUploads.FilesProblem("Upload at least one file."));
        }

        if (files.Count > UploadLimits.MaxFilesPerNote)
        {
            return ([], NoteUploads.FilesProblem($"A note can contain at most {UploadLimits.MaxFilesPerNote} pages."));
        }

        var (pages, errors) = await NoteUploads.ReadFilesAsync(files, ct);
        return (pages, errors.Count > 0 ? NoteUploads.FilesProblem([.. errors]) : null);
    }
}
