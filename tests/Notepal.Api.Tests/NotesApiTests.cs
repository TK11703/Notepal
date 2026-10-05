using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Notepal.Shared;

namespace Notepal.Api.Tests;

public sealed class NotesApiTests(NotepalApiFactory factory) : IClassFixture<NotepalApiFactory>
{
    private static string NewUser() => Guid.NewGuid().ToString();

    [Fact]
    public async Task Requests_without_a_user_are_rejected()
    {
        var response = await factory.CreateClientFor(null).GetAsync("/api/notes");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requests_without_the_api_scope_are_forbidden()
    {
        var response = await factory.CreateClientFor(NewUser(), scopes: "User.Read").GetAsync("/api/notes");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous()
    {
        var response = await factory.CreateClientFor(null).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Uploaded_image_is_ocrd_stored_and_downloadable()
    {
        var client = factory.CreateClientFor(NewUser());

        var created = await UploadAsync(client, "Biology", ("board.png", TestFiles.Png));
        Assert.Equal("Biology", created.Title);
        var page = Assert.Single(created.Pages);
        Assert.Equal("image/png", page.ContentType);

        var note = await WaitForProcessingAsync(client, created.Id);
        Assert.Equal(ProcessingStatus.Completed, note.Status);
        Assert.Contains("photosynthesis", note.Pages[0].Text);

        var original = await client.GetAsync($"/api/notes/{note.Id}/pages/{page.Id}/original");
        original.EnsureSuccessStatusCode();
        Assert.Equal("image/png", original.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TestFiles.Png, await original.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Pdf_and_docx_text_is_extracted()
    {
        var client = factory.CreateClientFor(NewUser());

        var created = await UploadAsync(client, "Docs",
            ("lecture.pdf", TestFiles.Pdf("Mitochondria is the powerhouse of the cell")),
            ("summary.docx", TestFiles.Docx("Chapter one summary", "Krebs cycle overview")));

        var note = await WaitForProcessingAsync(client, created.Id);
        Assert.Equal(ProcessingStatus.Completed, note.Status);
        Assert.Contains("Mitochondria", note.Pages.Single(p => p.FileName == "lecture.pdf").Text);
        Assert.Contains("Krebs cycle", note.Pages.Single(p => p.FileName == "summary.docx").Text);
    }

    [Fact]
    public async Task Files_whose_content_does_not_match_the_extension_are_rejected()
    {
        var client = factory.CreateClientFor(NewUser());
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent("<script>alert(1)</script>"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "files", "evil.png");

        var response = await client.PostAsync("/api/notes", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Users_can_only_see_and_change_their_own_notes()
    {
        var alice = factory.CreateClientFor(NewUser());
        var bob = factory.CreateClientFor(NewUser());

        var note = await WaitForProcessingAsync(alice, (await UploadAsync(alice, "Alice secret zebra", ("a.png", TestFiles.Png))).Id);
        var pageId = note.Pages[0].Id;

        var bobList = await bob.GetFromJsonAsync<PagedResult<NoteSummaryDto>>("/api/notes");
        Assert.Equal(0, bobList!.Total);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/notes/{note.Id}/pages/{pageId}/original")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/notes/{note.Id}", new UpdateNoteRequest("hacked"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/notes/{note.Id}/pages/{pageId}/text", new UpdatePageTextRequest("hacked"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/notes/{note.Id}/pages/{pageId}/reprocess", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);

        var bobSearch = await bob.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=zebra");
        Assert.Empty(bobSearch!.Items);
        var bobTextSearch = await bob.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=photosynthesis");
        Assert.Empty(bobTextSearch!.Items);

        var aliceNote = await alice.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}");
        Assert.Equal("Alice secret zebra", aliceNote!.Title);
        Assert.Equal(note.Pages[0].Text, aliceNote.Pages[0].Text);
    }

    [Fact]
    public async Task Corrections_are_saved_and_searchable()
    {
        var client = factory.CreateClientFor(NewUser());
        var note = await WaitForProcessingAsync(client, (await UploadAsync(client, "Chemistry", ("c.png", TestFiles.Png))).Id);
        var page = note.Pages[0];

        var update = await client.PutAsJsonAsync($"/api/notes/{note.Id}/pages/{page.Id}/text",
            new UpdatePageTextRequest("Corrected: the electrons travel through the transport chain"));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<PageDto>();
        Assert.True(updated!.IsEdited);
        Assert.Equal(page.ExtractedText, updated.ExtractedText);

        var results = await client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=electron");
        var hit = Assert.Single(results!.Items);
        Assert.Equal(note.Id, hit.NoteId);
        Assert.Contains("\u27E6electrons\u27E7", hit.Snippet);

        // Old OCR text no longer matches because the corrected text replaces it.
        var stale = await client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=photosynthesis");
        Assert.Empty(stale!.Items);

        // Reverting to the extracted text clears the correction.
        var revert = await client.PutAsJsonAsync($"/api/notes/{note.Id}/pages/{page.Id}/text", new UpdatePageTextRequest(page.ExtractedText!));
        Assert.False((await revert.Content.ReadFromJsonAsync<PageDto>())!.IsEdited);
    }

    [Fact]
    public async Task Search_matches_titles_and_partial_words()
    {
        var client = factory.CreateClientFor(NewUser());
        await WaitForProcessingAsync(client, (await UploadAsync(client, "Quarterly planning", ("q.png", TestFiles.Png))).Id);

        var byTitle = await client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=planning");
        Assert.Single(byTitle!.Items);

        var partial = await client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=photosynth");
        Assert.Single(partial!.Items);

        var none = await client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=100%25_");
        Assert.Empty(none!.Items);
    }

    [Fact]
    public async Task Notes_can_be_renamed_and_deleted()
    {
        var client = factory.CreateClientFor(NewUser());
        var note = await UploadAsync(client, "Draft", ("d.png", TestFiles.Png));

        var renamed = await client.PutAsJsonAsync($"/api/notes/{note.Id}", new UpdateNoteRequest("Final"));
        Assert.Equal("Final", (await renamed.Content.ReadFromJsonAsync<NoteDto>())!.Title);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/notes/{note.Id}")).StatusCode);
    }

    [Fact]
    public async Task Tags_are_normalised_on_create_and_update()
    {
        var client = factory.CreateClientFor(NewUser());
        var note = await UploadAsync(client, "Tagged", ["#Biology", " exam  prep ", "biology"], ("t.png", TestFiles.Png));
        Assert.Equal(["biology", "exam prep"], note.Tags);

        var response = await client.PutAsJsonAsync($"/api/notes/{note.Id}/tags", new UpdateNoteTagsRequest(["Week 3", "BIOLOGY", "", "week 3"]));
        response.EnsureSuccessStatusCode();
        Assert.Equal(["biology", "week 3"], (await response.Content.ReadFromJsonAsync<NoteDto>())!.Tags);
        Assert.Equal(["biology", "week 3"], (await client.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}"))!.Tags);

        var tooMany = Enumerable.Range(0, TagLimits.MaxTagsPerNote + 1).Select(i => $"t{i}").ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/notes/{note.Id}/tags", new UpdateNoteTagsRequest(tooMany))).StatusCode);
    }

    [Fact]
    public async Task Notes_and_search_can_be_filtered_by_tag()
    {
        var client = factory.CreateClientFor(NewUser());
        var bio = await WaitForProcessingAsync(client, (await UploadAsync(client, "Bio lecture", ["biology", "exam"], ("b.png", TestFiles.Png))).Id);
        await WaitForProcessingAsync(client, (await UploadAsync(client, "History lecture", ["history"], ("h.png", TestFiles.Png))).Id);

        var list = await client.GetFromJsonAsync<PagedResult<NoteSummaryDto>>("/api/notes?tag=Biology");
        Assert.Equal(bio.Id, Assert.Single(list!.Items).Id);
        Assert.Equal(["biology", "exam"], list.Items[0].Tags);

        var both = await client.GetFromJsonAsync<PagedResult<NoteSummaryDto>>("/api/notes?tag=biology&tag=history");
        Assert.Empty(both!.Items);

        var twoTags = await client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?tag=biology&tag=exam");
        Assert.Equal(bio.Id, Assert.Single(twoTags!.Items).NoteId);
        Assert.Empty((await client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=lecture&tag=biology&tag=history"))!.Items);

        var tagOnly = await client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?tag=history");
        Assert.Equal("History lecture", Assert.Single(tagOnly!.Items).Title);

        var lecture = await client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=lecture");
        Assert.Equal(2, lecture!.Total);

        var lectureAndTag = await client.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?q=lecture&tag=biology");
        var hit = Assert.Single(lectureAndTag!.Items);
        Assert.Equal(bio.Id, hit.NoteId);
        Assert.Contains("exam", hit.Tags);

        var tags = await client.GetFromJsonAsync<List<TagDto>>("/api/tags");
        Assert.Equal([new TagDto("biology", 1), new TagDto("exam", 1), new TagDto("history", 1)], tags);
    }

    [Fact]
    public async Task Tags_are_private_to_their_owner()
    {
        var alice = factory.CreateClientFor(NewUser());
        var bob = factory.CreateClientFor(NewUser());
        var note = await UploadAsync(alice, "Alice", ["secret-project"], ("a.png", TestFiles.Png));

        Assert.Empty((await bob.GetFromJsonAsync<List<TagDto>>("/api/tags"))!);
        Assert.Empty((await bob.GetFromJsonAsync<PagedResult<SearchResultDto>>("/api/search?tag=secret-project"))!.Items);
        Assert.Empty((await bob.GetFromJsonAsync<PagedResult<NoteSummaryDto>>("/api/notes?tag=secret-project"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/notes/{note.Id}/tags", new UpdateNoteTagsRequest(["mine"]))).StatusCode);
        Assert.Equal(["secret-project"], (await alice.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}"))!.Tags);
    }

    [Fact]
    public async Task Pages_can_be_added_to_an_existing_note()
    {
        var client = factory.CreateClientFor(NewUser());
        var note = await WaitForProcessingAsync(client, (await UploadAsync(client, "Lecture", ("p1.png", TestFiles.Png))).Id);

        var response = await client.PostAsync($"/api/notes/{note.Id}/pages", Files(("p2.png", TestFiles.Png), ("p3.png", TestFiles.Png)));
        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<NoteDto>())!;
        Assert.Equal([1, 2, 3], updated.Pages.Select(p => p.PageNumber));
        Assert.Equal(["p1.png", "p2.png", "p3.png"], updated.Pages.Select(p => p.FileName));
        Assert.Equal(note.Pages[0].Id, updated.Pages[0].Id);

        var processed = await WaitForProcessingAsync(client, note.Id);
        Assert.Equal(3, processed.Pages.Count);
        Assert.All(processed.Pages, p => Assert.Equal(ProcessingStatus.Completed, p.Status));

        var bad = await client.PostAsync($"/api/notes/{note.Id}/pages", Files(("fake.png", "not an image"u8.ToArray())));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var tooMany = Enumerable.Range(0, UploadLimits.MaxFilesPerNote - 2).Select(i => ($"x{i}.png", TestFiles.Png)).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/notes/{note.Id}/pages", Files(tooMany))).StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}"))!.Pages.Count);
    }

    [Fact]
    public async Task Pages_cannot_be_added_to_someone_elses_note()
    {
        var alice = factory.CreateClientFor(NewUser());
        var bob = factory.CreateClientFor(NewUser());
        var note = await UploadAsync(alice, "Alice", ("a.png", TestFiles.Png));

        var response = await bob.PostAsync($"/api/notes/{note.Id}/pages", Files(("b.png", TestFiles.Png)));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single((await alice.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}"))!.Pages);
    }

    internal static MultipartFormDataContent Files(params (string Name, byte[] Data)[] files)
    {
        var content = new MultipartFormDataContent();
        foreach (var (name, data) in files)
        {
            content.Add(new ByteArrayContent(data), "files", name);
        }

        return content;
    }

    internal static Task<NoteDto> UploadAsync(HttpClient client, string title, params (string Name, byte[] Data)[] files) =>
        UploadAsync(client, title, [], files);

    internal static async Task<NoteDto> UploadAsync(HttpClient client, string title, string[] tags, params (string Name, byte[] Data)[] files)
    {
        using var content = new MultipartFormDataContent { { new StringContent(title), "title" } };
        foreach (var tag in tags)
        {
            content.Add(new StringContent(tag), "tags");
        }

        foreach (var (name, data) in files)
        {
            content.Add(new ByteArrayContent(data), "files", name);
        }

        var response = await client.PostAsync("/api/notes", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NoteDto>())!;
    }

    internal static async Task<NoteDto> WaitForProcessingAsync(HttpClient client, Guid noteId)
    {
        for (var i = 0; i < 100; i++)
        {
            var note = await client.GetFromJsonAsync<NoteDto>($"/api/notes/{noteId}");
            if (note!.Status is ProcessingStatus.Completed or ProcessingStatus.Failed)
            {
                return note;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Note was not processed in time.");
    }
}
