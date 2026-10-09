using Notepal.Contracts;

namespace Notepal.Api.Features.Sharing.LeaveSharedNotes;

internal static class LeaveSharedNotesValidator
{
    public static (List<Guid> NoteIds, IResult? Error) Validate(LeaveSharedNotesRequest body)
    {
        var noteIds = (body.NoteIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (noteIds.Count == 0)
        {
            return (noteIds, ShareValidation.Invalid(nameof(body.NoteIds), "Choose at least one note to leave."));
        }

        return (noteIds, noteIds.Count > ShareLimits.MaxNotesPerLeave
            ? ShareValidation.Invalid(nameof(body.NoteIds), $"You can leave up to {ShareLimits.MaxNotesPerLeave} notes at a time.")
            : null);
    }
}
