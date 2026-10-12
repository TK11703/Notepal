using Notepal.Contracts;

namespace Notepal.Database;

internal static partial class DatabaseMapping
{
    public static PageDto ToDto(this Page page) => new(
        page.Id,
        page.PageNumber,
        page.FileName,
        page.ContentType,
        page.SizeBytes,
        page.Status,
        page.ExtractedText,
        page.EffectiveText,
        page.EditedText is not null,
        page.Error,
        page.UpdatedAt);
}
