using Notepal.Api.Features.Notes.AddPages;
using Notepal.Api.Features.Notes.CreateNote;
using Notepal.Api.Features.Notes.DeleteNote;
using Notepal.Api.Features.Notes.DeletePage;
using Notepal.Api.Features.Notes.GetNote;
using Notepal.Api.Features.Notes.GetNoteStats;
using Notepal.Api.Features.Notes.GetOriginal;
using Notepal.Api.Features.Notes.ListNotes;
using Notepal.Api.Features.Notes.MovePage;
using Notepal.Api.Features.Notes.RenameNote;
using Notepal.Api.Features.Notes.ReprocessPage;
using Notepal.Api.Features.Notes.TransferPage;
using Notepal.Api.Features.Notes.UpdatePageText;

namespace Notepal.Api.Features.Notes;

public static class NotesEndpoints
{
    public static RouteGroupBuilder MapNotesEndpoints(this RouteGroupBuilder api)
    {
        var notes = api.MapGroup("/notes").WithTags("Notes");
        ListNotesEndpoint.Map(notes);
        GetNoteStatsEndpoint.Map(notes);
        GetNoteEndpoint.Map(notes);
        CreateNoteEndpoint.Map(notes);
        RenameNoteEndpoint.Map(notes);
        AddPagesEndpoint.Map(notes);
        DeleteNoteEndpoint.Map(notes);
        GetOriginalEndpoint.Map(notes);
        UpdatePageTextEndpoint.Map(notes);
        ReprocessPageEndpoint.Map(notes);
        DeletePageEndpoint.Map(notes);
        MovePageEndpoint.Map(notes);
        TransferPageEndpoint.Map(notes);
        return api;
    }
}
