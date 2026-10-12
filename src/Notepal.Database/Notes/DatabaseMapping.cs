using Notepal.Contracts;

namespace Notepal.Database;

internal static partial class DatabaseMapping
{
    public static NoteDto ToDto(this Note note, IEnumerable<Page> pages, NoteAccess? access = null, int shareCount = 0)
    {
        var ordered = pages.OrderBy(p => p.PageNumber).ToList();
        return new(
            note.Id,
            note.Title,
            note.CreatedAt,
            note.UpdatedAt,
            AggregateStatus(ordered.Select(p => p.Status)),
            ordered.Select(p => p.ToDto()).ToList(),
            note.Tags,
            access?.Role ?? NoteRole.Owner,
            access?.SharedBy,
            shareCount);
    }

    public static ProcessingStatus AggregateStatus(IEnumerable<ProcessingStatus> statuses)
    {
        var list = statuses as IReadOnlyCollection<ProcessingStatus> ?? statuses.ToList();
        if (list.Any(s => s is ProcessingStatus.Pending or ProcessingStatus.Processing))
        {
            return list.All(s => s == ProcessingStatus.Pending) ? ProcessingStatus.Pending : ProcessingStatus.Processing;
        }

        return list.Any(s => s == ProcessingStatus.Failed) ? ProcessingStatus.Failed : ProcessingStatus.Completed;
    }
}
