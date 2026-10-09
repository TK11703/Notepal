using System.Net;
using System.Net.Http.Json;
using Notepal.Contracts;
using static Notepal.Api.Tests.NotesApiTests;

namespace Notepal.Api.Tests;

public sealed class SharingApiTests(NotepalApiFactory factory) : IClassFixture<NotepalApiFactory>
{
    private sealed record Person(string Id, string Email, HttpClient Client);

    private Person NewPerson(bool withEmail = true)
    {
        var id = Guid.NewGuid().ToString();
        var email = $"user-{id[..8]}@contoso.example";
        return new Person(id, email, factory.CreateClientFor(id, email: withEmail ? email : null));
    }

    private static async Task<NoteShareDto> ShareAsync(Person owner, Guid noteId, AddNoteShareRequest request)
    {
        var response = await owner.Client.PostAsJsonAsync($"/api/notes/{noteId}/shares", request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<NoteShareDto>())!;
    }

    [Fact]
    public async Task Reader_can_view_but_not_change_or_delete()
    {
        var alice = NewPerson();
        var bob = NewPerson();
        var note = await WaitForProcessingAsync(alice.Client, (await UploadAsync(alice.Client, "Shared biology", ("a.png", TestFiles.Png))).Id);
        var pageId = note.Pages[0].Id;

        await ShareAsync(alice, note.Id, new AddNoteShareRequest(bob.Email, "Bob", bob.Id));

        var seen = await bob.Client.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}");
        Assert.Equal("Shared biology", seen!.Title);
        Assert.Equal(NoteRole.Reader, seen.Role);
        Assert.Equal(HttpStatusCode.OK, (await bob.Client.GetAsync($"/api/notes/{note.Id}/pages/{pageId}/original")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Client.PutAsJsonAsync($"/api/notes/{note.Id}", new UpdateNoteRequest("hacked"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Client.PutAsJsonAsync($"/api/notes/{note.Id}/tags", new UpdateNoteTagsRequest(["x"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Client.PutAsJsonAsync($"/api/notes/{note.Id}/pages/{pageId}/text", new UpdatePageTextRequest("hacked"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Client.PostAsync($"/api/notes/{note.Id}/pages/{pageId}/reprocess", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Client.PostAsync($"/api/notes/{note.Id}/pages", Files(("b.png", TestFiles.Png)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Client.PutAsJsonAsync($"/api/notes/{note.Id}/pages/{pageId}/position", new MovePageRequest(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Client.DeleteAsync($"/api/notes/{note.Id}/pages/{pageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Client.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Client.GetAsync($"/api/notes/{note.Id}/shares")).StatusCode);

        // Shared notes are not mixed into the recipient's own notes or search.
        Assert.Equal(0, (await bob.Client.GetFromJsonAsync<PagedResult<NoteSummaryDto>>("/api/notes"))!.Total);
        Assert.Empty((await bob.Client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=biology"))!.Items);

        Assert.Equal("Shared biology", (await alice.Client.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}"))!.Title);
    }

    [Fact]
    public async Task Contributor_can_edit_and_add_pages_but_not_delete_or_share()
    {
        var alice = NewPerson();
        var carol = NewPerson();
        var dave = NewPerson();
        var note = await WaitForProcessingAsync(alice.Client, (await UploadAsync(alice.Client, "Team notes", ("a.png", TestFiles.Png))).Id);
        var pageId = note.Pages[0].Id;

        await ShareAsync(alice, note.Id, new AddNoteShareRequest(carol.Email, Permission: SharePermission.Contributor));

        var renamed = await carol.Client.PutAsJsonAsync($"/api/notes/{note.Id}", new UpdateNoteRequest("Team notes v2"));
        renamed.EnsureSuccessStatusCode();
        Assert.Equal(NoteRole.Contributor, (await renamed.Content.ReadFromJsonAsync<NoteDto>())!.Role);
        (await carol.Client.PutAsJsonAsync($"/api/notes/{note.Id}/tags", new UpdateNoteTagsRequest(["team"]))).EnsureSuccessStatusCode();
        (await carol.Client.PutAsJsonAsync($"/api/notes/{note.Id}/pages/{pageId}/text", new UpdatePageTextRequest("Carol's correction"))).EnsureSuccessStatusCode();
        (await carol.Client.PostAsync($"/api/notes/{note.Id}/pages", Files(("b.png", TestFiles.Png)))).EnsureSuccessStatusCode();
        (await carol.Client.PutAsJsonAsync($"/api/notes/{note.Id}/pages/{pageId}/position", new MovePageRequest(2))).EnsureSuccessStatusCode();
        (await carol.Client.PutAsJsonAsync($"/api/notes/{note.Id}/pages/{pageId}/position", new MovePageRequest(1))).EnsureSuccessStatusCode();
        Assert.Equal(1, (await alice.Client.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}"))!.ShareCount);
        Assert.Equal(0, (await carol.Client.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}"))!.ShareCount);

        Assert.Equal(HttpStatusCode.Forbidden, (await carol.Client.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.Client.PostAsJsonAsync($"/api/notes/{note.Id}/shares", new AddNoteShareRequest(dave.Email))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dave.Client.GetAsync($"/api/notes/{note.Id}")).StatusCode);

        // The owner sees the contributor's changes, and pages added by the contributor still belong to the owner.
        var updated = await WaitForProcessingAsync(alice.Client, note.Id);
        Assert.Equal("Team notes v2", updated.Title);
        Assert.Equal(["team"], updated.Tags);
        Assert.Equal("Carol's correction", updated.Pages[0].Text);
        Assert.Equal(2, updated.Pages.Count);
        Assert.Equal(1, (await alice.Client.GetFromJsonAsync<PagedResult<NoteSummaryDto>>("/api/notes?tag=team"))!.Total);

        (await alice.Client.DeleteAsync($"/api/notes/{note.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await carol.Client.GetAsync($"/api/notes/{note.Id}")).StatusCode);
    }

    [Fact]
    public async Task Shares_by_email_are_matched_on_sign_in_address()
    {
        var alice = NewPerson();
        var erin = NewPerson();
        var note = await UploadAsync(alice.Client, "By address", ("a.png", TestFiles.Png));

        await ShareAsync(alice, note.Id, new AddNoteShareRequest($"  {erin.Email.ToUpperInvariant()} "));

        Assert.Equal(HttpStatusCode.OK, (await erin.Client.GetAsync($"/api/notes/{note.Id}")).StatusCode);

        // Without a matching address (and no object id on the share) there is no access.
        var erinWithoutEmail = factory.CreateClientFor(erin.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await erinWithoutEmail.GetAsync($"/api/notes/{note.Id}")).StatusCode);
    }

    [Fact]
    public async Task Owner_manages_shares_and_lists_show_both_directions()
    {
        var alice = NewPerson();
        var bob = NewPerson();
        var note = await UploadAsync(alice.Client, "Lists", ("a.png", TestFiles.Png));
        var bobsNote = await UploadAsync(bob.Client, "Bob's note", ("b.png", TestFiles.Png));

        var share = await ShareAsync(alice, note.Id, new AddNoteShareRequest(bob.Email, "Bob Builder", bob.Id));
        Assert.Equal(SharePermission.Reader, share.Permission);

        // Adding the same person again updates the existing share.
        var again = await ShareAsync(alice, note.Id, new AddNoteShareRequest(bob.Email.ToUpperInvariant(), Permission: SharePermission.Contributor));
        Assert.Equal(share.Id, again.Id);
        Assert.Equal(SharePermission.Contributor, again.Permission);
        Assert.Equal("Bob Builder", again.DisplayName);

        var updated = await alice.Client.PutAsJsonAsync($"/api/notes/{note.Id}/shares/{share.Id}", new UpdateNoteShareRequest(SharePermission.Reader));
        Assert.Equal(SharePermission.Reader, (await updated.Content.ReadFromJsonAsync<NoteShareDto>())!.Permission);
        Assert.Single((await alice.Client.GetFromJsonAsync<List<NoteShareDto>>($"/api/notes/{note.Id}/shares"))!);

        await ShareAsync(bob, bobsNote.Id, new AddNoteShareRequest(alice.Email, Permission: SharePermission.Contributor));

        var aliceByMe = (await alice.Client.GetFromJsonAsync<PagedResult<SharedNoteDto>>("/api/shared/by-me"))!;
        var byMe = Assert.Single(aliceByMe.Items);
        Assert.Equal(note.Id, byMe.Note.Id);
        Assert.Equal(bob.Email, Assert.Single(byMe.SharedWith).Email);

        var aliceWithMe = (await alice.Client.GetFromJsonAsync<PagedResult<SharedNoteDto>>("/api/shared/with-me"))!;
        var withMe = Assert.Single(aliceWithMe.Items);
        Assert.Equal(bobsNote.Id, withMe.Note.Id);
        Assert.Equal(NoteRole.Contributor, withMe.Role);
        Assert.Equal($"Test user {bob.Id}", withMe.SharedBy);
        Assert.Empty(withMe.SharedWith);

        Assert.Single((await bob.Client.GetFromJsonAsync<PagedResult<SharedNoteDto>>("/api/shared/with-me"))!.Items);

        // Removing the share revokes access.
        (await alice.Client.DeleteAsync($"/api/notes/{note.Id}/shares/{share.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.GetAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Empty((await bob.Client.GetFromJsonAsync<PagedResult<SharedNoteDto>>("/api/shared/with-me"))!.Items);
        Assert.Empty((await alice.Client.GetFromJsonAsync<PagedResult<SharedNoteDto>>("/api/shared/by-me"))!.Items);
    }

    [Fact]
    public async Task Recipient_can_leave_a_shared_note()
    {
        var alice = NewPerson();
        var bob = NewPerson();
        var note = await UploadAsync(alice.Client, "Leave me", ("a.png", TestFiles.Png));
        var share = await ShareAsync(alice, note.Id, new AddNoteShareRequest(bob.Email, UserId: bob.Id));

        (await bob.Client.DeleteAsync($"/api/notes/{note.Id}/shares/{share.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.GetAsync($"/api/notes/{note.Id}")).StatusCode);
    }

    [Fact]
    public async Task Recipient_can_leave_several_shared_notes_at_once()
    {
        var alice = NewPerson();
        var bob = NewPerson();
        var carol = NewPerson();
        var first = await UploadAsync(alice.Client, "Leave one", ("a.png", TestFiles.Png));
        var second = await UploadAsync(alice.Client, "Leave two", ("a.png", TestFiles.Png));
        var kept = await UploadAsync(alice.Client, "Keep", ("a.png", TestFiles.Png));
        var bobsOwn = await UploadAsync(bob.Client, "Bob's own", ("a.png", TestFiles.Png));
        await ShareAsync(alice, first.Id, new AddNoteShareRequest(bob.Email, UserId: bob.Id));
        await ShareAsync(alice, second.Id, new AddNoteShareRequest(bob.Email, Permission: SharePermission.Contributor));
        await ShareAsync(alice, kept.Id, new AddNoteShareRequest(bob.Email));
        await ShareAsync(alice, first.Id, new AddNoteShareRequest(carol.Email));
        await ShareAsync(bob, bobsOwn.Id, new AddNoteShareRequest(carol.Email));

        // Bob's own note and a note he has no share for are ignored.
        var response = await bob.Client.PostAsJsonAsync("/api/shared/with-me/leave",
            new LeaveSharedNotesRequest([first.Id, second.Id, bobsOwn.Id, Guid.NewGuid()]));
        response.EnsureSuccessStatusCode();
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<LeaveSharedNotesResult>())!.Left);

        var withMe = (await bob.Client.GetFromJsonAsync<PagedResult<SharedNoteDto>>("/api/shared/with-me"))!;
        Assert.Equal([kept.Id], withMe.Items.Select(i => i.Note.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.GetAsync($"/api/notes/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.GetAsync($"/api/notes/{second.Id}")).StatusCode);

        // Other people's shares and the notes themselves are untouched.
        Assert.Equal(HttpStatusCode.OK, (await carol.Client.GetAsync($"/api/notes/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await carol.Client.GetAsync($"/api/notes/{bobsOwn.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.Client.GetAsync($"/api/notes/{second.Id}")).StatusCode);
        var aliceShares = (await alice.Client.GetFromJsonAsync<List<NoteShareDto>>($"/api/notes/{first.Id}/shares"))!;
        Assert.Equal([carol.Email], aliceShares.Select(s => s.Email));
    }

    [Fact]
    public async Task Leaving_requires_note_ids_within_the_limit()
    {
        var bob = NewPerson();
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.Client.PostAsJsonAsync("/api/shared/with-me/leave", new LeaveSharedNotesRequest([]))).StatusCode);
        var tooMany = Enumerable.Range(0, ShareLimits.MaxNotesPerLeave + 1).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.Client.PostAsJsonAsync("/api/shared/with-me/leave", new LeaveSharedNotesRequest(tooMany))).StatusCode);
    }

    [Fact]
    public async Task Invalid_shares_are_rejected()
    {
        var alice = NewPerson();
        var mallory = NewPerson();
        var note = await UploadAsync(alice.Client, "Validation", ("a.png", TestFiles.Png));

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Client.PostAsJsonAsync($"/api/notes/{note.Id}/shares", new AddNoteShareRequest("not-an-email"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Client.PostAsJsonAsync($"/api/notes/{note.Id}/shares", new AddNoteShareRequest(alice.Email))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Client.PostAsJsonAsync($"/api/notes/{note.Id}/shares", new AddNoteShareRequest("x@y.example", Permission: (SharePermission)7))).StatusCode);

        // Someone without access cannot share (or discover) the note.
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Client.PostAsJsonAsync($"/api/notes/{note.Id}/shares", new AddNoteShareRequest(mallory.Email))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Client.GetAsync($"/api/notes/{note.Id}")).StatusCode);
    }
}
