using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Notepal.Contracts;

namespace Notepal.Web.Services;

/// <summary>
/// Calls Notepal.Api on behalf of the signed-in user. An access token for the user is acquired
/// (from the token cache) for every call, so the API only ever returns that user's data.
/// </summary>
public sealed class NotepalApiClient(IDownstreamApi api, AuthenticationStateProvider authenticationState, ILogger<NotepalApiClient> logger)
{
    public const string ServiceName = "NotepalApi";

    public Task<PagedResult<NoteSummaryDto>> ListNotesAsync(int page, int pageSize, string? tag = null, CancellationToken ct = default) =>
        SendAsync<PagedResult<NoteSummaryDto>>(HttpMethod.Get, $"api/notes?page={page}&pageSize={pageSize}{TagQuery(tag)}", null, ct);

    public Task<NoteStatsDto> GetNoteStatsAsync(string? tag = null, CancellationToken ct = default) =>
        SendAsync<NoteStatsDto>(HttpMethod.Get, $"api/notes/stats?{TagQuery(tag).TrimStart('&')}", null, ct);

    public Task<NoteDto?> GetNoteAsync(Guid noteId, CancellationToken ct = default) =>
        SendOrDefaultAsync<NoteDto>(HttpMethod.Get, $"api/notes/{noteId}", null, ct);

    public async Task<NoteDto> CreateNoteAsync(string? title, IReadOnlyList<string> tags, IReadOnlyList<UploadItem> files, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        if (!string.IsNullOrWhiteSpace(title))
        {
            content.Add(new StringContent(title.Trim()), "title");
        }

        foreach (var tag in tags)
        {
            content.Add(new StringContent(tag), "tags");
        }

        AddFiles(content, files);
        return await SendAsync<NoteDto>(HttpMethod.Post, "api/notes", content, ct);
    }

    /// <summary>Appends files as new pages at the end of an existing note.</summary>
    public async Task<NoteDto> AddPagesAsync(Guid noteId, IReadOnlyList<UploadItem> files, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        AddFiles(content, files);
        return await SendAsync<NoteDto>(HttpMethod.Post, $"api/notes/{noteId}/pages", content, ct);
    }

    private static void AddFiles(MultipartFormDataContent content, IReadOnlyList<UploadItem> files)
    {
        foreach (var file in files)
        {
            var part = new ByteArrayContent(file.Data);
            part.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
            content.Add(part, "files", file.FileName);
        }
    }

    public Task<NoteDto> RenameNoteAsync(Guid noteId, string title, CancellationToken ct = default) =>
        SendAsync<NoteDto>(HttpMethod.Put, $"api/notes/{noteId}", JsonContent.Create(new UpdateNoteRequest(title)), ct);

    public Task<NoteDto> UpdateNoteTagsAsync(Guid noteId, IReadOnlyList<string> tags, CancellationToken ct = default) =>
        SendAsync<NoteDto>(HttpMethod.Put, $"api/notes/{noteId}/tags", JsonContent.Create(new UpdateNoteTagsRequest(tags)), ct);

    /// <summary>Tags the user has used before, most used first.</summary>
    public Task<List<TagDto>> GetTagsAsync(CancellationToken ct = default) =>
        SendAsync<List<TagDto>>(HttpMethod.Get, "api/tags", null, ct);

    public async Task DeleteNoteAsync(Guid noteId, CancellationToken ct = default) =>
        await SendRawAsync(HttpMethod.Delete, $"api/notes/{noteId}", null, ct);

    public Task<PageDto> UpdatePageTextAsync(Guid noteId, Guid pageId, string text, CancellationToken ct = default) =>
        SendAsync<PageDto>(HttpMethod.Put, $"api/notes/{noteId}/pages/{pageId}/text", JsonContent.Create(new UpdatePageTextRequest(text)), ct);

    public Task<PageDto> ReprocessPageAsync(Guid noteId, Guid pageId, CancellationToken ct = default) =>
        SendAsync<PageDto>(HttpMethod.Post, $"api/notes/{noteId}/pages/{pageId}/reprocess", null, ct);

    /// <summary>Deletes a page; the remaining pages are renumbered.</summary>
    public Task<NoteDto> DeletePageAsync(Guid noteId, Guid pageId, CancellationToken ct = default) =>
        SendAsync<NoteDto>(HttpMethod.Delete, $"api/notes/{noteId}/pages/{pageId}", null, ct);

    /// <summary>Moves a page to another (1-based) position.</summary>
    public Task<NoteDto> MovePageAsync(Guid noteId, Guid pageId, int pageNumber, CancellationToken ct = default) =>
        SendAsync<NoteDto>(HttpMethod.Put, $"api/notes/{noteId}/pages/{pageId}/position", JsonContent.Create(new MovePageRequest(pageNumber)), ct);

    /// <summary>Moves a page to the end of another note; returns the (renumbered) source note.</summary>
    public Task<NoteDto> TransferPageAsync(Guid noteId, Guid pageId, Guid targetNoteId, CancellationToken ct = default) =>
        SendAsync<NoteDto>(HttpMethod.Post, $"api/notes/{noteId}/pages/{pageId}/transfer", JsonContent.Create(new TransferPageRequest(targetNoteId)), ct);

    /// <summary>People the note is shared with (owner only).</summary>
    public Task<List<NoteShareDto>> GetSharesAsync(Guid noteId, CancellationToken ct = default) =>
        SendAsync<List<NoteShareDto>>(HttpMethod.Get, $"api/notes/{noteId}/shares", null, ct);

    /// <summary>Shares the note with someone, or updates their permission if it is already shared with them.</summary>
    public Task<NoteShareDto> AddShareAsync(Guid noteId, AddNoteShareRequest request, CancellationToken ct = default) =>
        SendAsync<NoteShareDto>(HttpMethod.Post, $"api/notes/{noteId}/shares", JsonContent.Create(request), ct);

    public Task<NoteShareDto> UpdateShareAsync(Guid noteId, Guid shareId, SharePermission permission, CancellationToken ct = default) =>
        SendAsync<NoteShareDto>(HttpMethod.Put, $"api/notes/{noteId}/shares/{shareId}", JsonContent.Create(new UpdateNoteShareRequest(permission)), ct);

    public async Task RemoveShareAsync(Guid noteId, Guid shareId, CancellationToken ct = default) =>
        await SendRawAsync(HttpMethod.Delete, $"api/notes/{noteId}/shares/{shareId}", null, ct);

    /// <summary>Notes the user shared with others.</summary>
    public Task<PagedResult<SharedNoteDto>> ListSharedByMeAsync(int page, int pageSize, CancellationToken ct = default) =>
        SendAsync<PagedResult<SharedNoteDto>>(HttpMethod.Get, $"api/shared/by-me?page={page}&pageSize={pageSize}", null, ct);

    /// <summary>Notes other people shared with the user.</summary>
    public Task<PagedResult<SharedNoteDto>> ListSharedWithMeAsync(int page, int pageSize, CancellationToken ct = default) =>
        SendAsync<PagedResult<SharedNoteDto>>(HttpMethod.Get, $"api/shared/with-me?page={page}&pageSize={pageSize}", null, ct);

    /// <summary>Removes the user's access to notes other people shared with them.</summary>
    public Task<LeaveSharedNotesResult> LeaveSharedNotesAsync(IReadOnlyList<Guid> noteIds, CancellationToken ct = default) =>
        SendAsync<LeaveSharedNotesResult>(HttpMethod.Post, "api/shared/with-me/leave", JsonContent.Create(new LeaveSharedNotesRequest(noteIds)), ct);

    /// <summary>Full-text search; when tags are given, only notes that carry every one of them are searched.</summary>
    public Task<PagedResult<SearchResultDto>> SearchAsync(string query, int page, int pageSize, IReadOnlyList<string>? tags = null, CancellationToken ct = default) =>
        SendAsync<PagedResult<SearchResultDto>>(HttpMethod.Get, $"api/search?q={Uri.EscapeDataString(query)}&page={page}&pageSize={pageSize}{TagQuery(tags ?? [])}", null, ct);

    private static string TagQuery(params IReadOnlyList<string?> tags) =>
        string.Concat(tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => $"&tag={Uri.EscapeDataString(t!)}"));

    /// <summary>Fetches an original artifact for the file proxy endpoint (plain HTTP request, so the user is passed explicitly).</summary>
    public async Task<HttpResponseMessage> GetOriginalAsync(ClaimsPrincipal user, Guid noteId, Guid pageId, CancellationToken ct = default) =>
        await CallAsync(user, HttpMethod.Get, $"api/notes/{noteId}/pages/{pageId}/original", null, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, content, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private async Task<T?> SendOrDefaultAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        try
        {
            return await SendAsync<T>(method, path, content, ct);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var user = (await authenticationState.GetAuthenticationStateAsync()).User;
        var response = await CallAsync(user, method, path, content, ct);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var problem = await ReadProblemAsync(response, ct);
            logger.LogWarning("Notepal API {Method} request failed with {Status}", method, (int)response.StatusCode);
            throw new ApiException(response.StatusCode, problem);
        }
    }

    private async Task<HttpResponseMessage> CallAsync(ClaimsPrincipal user, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        try
        {
            return await api.CallApiForUserAsync(
                ServiceName,
                options =>
                {
                    options.HttpMethod = method.Method;
                    options.RelativePath = path;
                },
                user,
                content,
                ct);
        }
        catch (MicrosoftIdentityWebChallengeUserException ex)
        {
            throw new ReauthenticationRequiredException(ex);
        }
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(ct);
            if (problem?.Errors is { Count: > 0 })
            {
                return string.Join(" ", problem.Errors.SelectMany(e => e.Value));
            }

            return problem?.Detail ?? problem?.Message ?? problem?.Title ?? response.ReasonPhrase ?? "Request failed.";
        }
        catch
        {
            return response.ReasonPhrase ?? "Request failed.";
        }
    }

    private sealed record ProblemPayload(string? Title, string? Detail, string? Message, Dictionary<string, string[]>? Errors);
}

public sealed record UploadItem(string FileName, string ContentType, byte[] Data);

public sealed class ApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class ReauthenticationRequiredException(Exception inner)
    : Exception("Your session has expired. Please sign in again.", inner);
