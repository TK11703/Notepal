using Notepal.Api.Data;
using Notepal.Shared;

namespace Notepal.Api.Endpoints;

internal static class Mapping
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

    public static NoteDto ToDto(this Note note, IEnumerable<Page> pages)
    {
        var ordered = pages.OrderBy(p => p.PageNumber).ToList();
        return new NoteDto(
            note.Id,
            note.Title,
            note.CreatedAt,
            note.UpdatedAt,
            AggregateStatus(ordered.Select(p => p.Status)),
            ordered.Select(p => p.ToDto()).ToList());
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
