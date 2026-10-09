using System.Net.Http.Json;
using System.Text.Json;
using Notepal.Contracts;

namespace Notepal.Api.Tests;

public sealed class ApiContractTests(NotepalApiFactory factory) : IClassFixture<NotepalApiFactory>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Self_sharing_requires_ownership_before_reporting_a_validation_error(bool byUserId)
    {
        var ownerId = Guid.NewGuid().ToString();
        using var owner = factory.CreateClientFor(ownerId, email: "owner@example.com");
        var note = await NotesApiTests.UploadAsync(owner, "Sharing boundary", ("original.png", TestFiles.Png));
        var request = byUserId
            ? new AddNoteShareRequest("person@example.com", UserId: ownerId)
            : new AddNoteShareRequest("owner@example.com");

        using var response = await owner.PostAsJsonAsync($"/api/notes/{note.Id}/shares", request);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var missing = await owner.PostAsJsonAsync($"/api/notes/{Guid.NewGuid()}/shares", request);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Note_page_and_share_responses_match_contracts_without_persistence_fields()
    {
        using var owner = factory.CreateClientFor(Guid.NewGuid().ToString(), email: "owner@example.com");
        using var recipient = factory.CreateClientFor(Guid.NewGuid().ToString(), email: "recipient@example.com");
        var note = await NotesApiTests.WaitForProcessingAsync(owner,
            (await NotesApiTests.UploadAsync(owner, "Contracts", ["tag"], ("original.png", TestFiles.Png))).Id);

        using var noteResponse = await owner.GetAsync($"/api/notes/{note.Id}");
        using var noteJson = await ReadJsonAsync(noteResponse);
        AssertProperties<NoteDto>(noteJson.RootElement);
        AssertProperties<PageDto>(noteJson.RootElement.GetProperty("pages")[0]);
        Assert.Equal(JsonValueKind.Number, noteJson.RootElement.GetProperty("role").ValueKind);
        Assert.Equal(JsonValueKind.Number, noteJson.RootElement.GetProperty("status").ValueKind);

        using var shareResponse = await owner.PostAsJsonAsync($"/api/notes/{note.Id}/shares",
            new AddNoteShareRequest("recipient@example.com", "Recipient"));
        using var shareJson = await ReadJsonAsync(shareResponse);
        AssertProperties<NoteShareDto>(shareJson.RootElement);
        Assert.Equal(JsonValueKind.Number, shareJson.RootElement.GetProperty("permission").ValueKind);
        var shareId = shareJson.RootElement.GetProperty("id").GetGuid();

        using var updatedResponse = await owner.PutAsJsonAsync($"/api/notes/{note.Id}/shares/{shareId}",
            new UpdateNoteShareRequest(SharePermission.Contributor));
        using var updatedJson = await ReadJsonAsync(updatedResponse);
        AssertProperties<NoteShareDto>(updatedJson.RootElement);

        using var sharesResponse = await owner.GetAsync($"/api/notes/{note.Id}/shares");
        using var sharesJson = await ReadJsonAsync(sharesResponse);
        AssertProperties<NoteShareDto>(Assert.Single(sharesJson.RootElement.EnumerateArray()));

        using var ownedResponse = await owner.GetAsync("/api/shared/by-me");
        using var ownedJson = await ReadJsonAsync(ownedResponse);
        AssertProperties<PagedResult<SharedNoteDto>>(ownedJson.RootElement);
        var owned = Assert.Single(ownedJson.RootElement.GetProperty("items").EnumerateArray());
        AssertProperties<SharedNoteDto>(owned);
        AssertProperties<NoteSummaryDto>(owned.GetProperty("note"));
        AssertProperties<NoteShareDto>(Assert.Single(owned.GetProperty("sharedWith").EnumerateArray()));

        using var receivedResponse = await recipient.GetAsync("/api/shared/with-me");
        using var receivedJson = await ReadJsonAsync(receivedResponse);
        AssertProperties<SharedNoteDto>(Assert.Single(receivedJson.RootElement.GetProperty("items").EnumerateArray()));
    }

    [Fact]
    public async Task Empty_tags_clear_tags_and_empty_corrected_text_is_valid()
    {
        using var client = factory.CreateClientFor(Guid.NewGuid().ToString());
        var note = await NotesApiTests.WaitForProcessingAsync(client,
            (await NotesApiTests.UploadAsync(client, "Valid empty values", ["tag"], ("original.png", TestFiles.Png))).Id);
        using var tagsResponse = await client.PutAsJsonAsync($"/api/notes/{note.Id}/tags", new UpdateNoteTagsRequest([]));
        tagsResponse.EnsureSuccessStatusCode();
        Assert.Empty((await tagsResponse.Content.ReadFromJsonAsync<NoteDto>())!.Tags);

        using var textResponse = await client.PutAsJsonAsync($"/api/notes/{note.Id}/pages/{note.Pages[0].Id}/text",
            new UpdatePageTextRequest(""));
        textResponse.EnsureSuccessStatusCode();
        var page = await textResponse.Content.ReadFromJsonAsync<PageDto>();
        Assert.NotNull(page);
        Assert.Equal("", page.Text);
        Assert.True(page.IsEdited);
    }

    [Theory]
    [InlineData("/api/notes")]
    [InlineData("/api/search")]
    [InlineData("/api/shared/by-me")]
    [InlineData("/api/shared/with-me")]
    public async Task Pagination_is_normalized_at_the_HTTP_boundary(string route)
    {
        using var client = factory.CreateClientFor(Guid.NewGuid().ToString());
        using var response = await client.GetAsync($"{route}?page=-1&pageSize=1000");
        using var json = await ReadJsonAsync(response);
        Assert.Equal(1, json.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(NoteLimits.MaxPageSize, json.RootElement.GetProperty("pageSize").GetInt32());
    }

    private static void AssertProperties<T>(JsonElement element)
    {
        var expected = typeof(T).GetProperties().Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Order(StringComparer.Ordinal);
        var actual = element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
