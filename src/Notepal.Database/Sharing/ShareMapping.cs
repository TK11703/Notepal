using Notepal.Contracts;

namespace Notepal.Database;

internal static partial class DatabaseMapping
{
    public static NoteShareDto ToDto(this NoteShare share) => new(
        share.Id,
        share.RecipientEmail,
        share.RecipientName,
        share.Permission,
        share.CreatedAt);
}
