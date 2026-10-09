using Notepal.Api.Features.Tags;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Notes.CreateNote;

internal static class CreateNoteValidator
{
    public static async Task<(string Title, List<string> Tags, List<NewPage> Pages, IResult? Error)> ValidateAsync(
        IFormCollection form, CancellationToken ct)
    {
        var files = form.Files;
        var title = form["title"].ToString().Trim();
        if (title.Length > NoteLimits.MaxTitleLength)
        {
            return (title, [], [], Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = [$"Title must be at most {NoteLimits.MaxTitleLength} characters."],
            }));
        }

        if (files.Count == 0)
        {
            return (title, [], [], NoteUploads.FilesProblem("Upload at least one file."));
        }

        if (files.Count > UploadLimits.MaxFilesPerNote)
        {
            return (title, [], [], NoteUploads.FilesProblem($"A note can contain at most {UploadLimits.MaxFilesPerNote} files."));
        }

        var tags = TagLimits.NormalizeAll(form["tags"]);
        if (tags.Count > TagLimits.MaxTagsPerNote)
        {
            return (title, tags, [], TagValidation.TooManyTags());
        }

        var (pages, errors) = await NoteUploads.ReadFilesAsync(files, ct);
        if (errors.Count > 0)
        {
            return (title, tags, pages, NoteUploads.FilesProblem([.. errors]));
        }

        if (title.Length == 0)
        {
            title = files.Count == 1 ? Path.GetFileNameWithoutExtension(files[0].FileName) : $"Note {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}";
        }

        if (title.Length > NoteLimits.MaxTitleLength)
        {
            title = title[..NoteLimits.MaxTitleLength];
        }

        return (title, tags, pages, null);
    }
}
