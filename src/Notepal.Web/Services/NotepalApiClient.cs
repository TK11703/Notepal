using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Notepal.Shared;

namespace Notepal.Web.Services;

/// <summary>
/// Calls Notepal.Api on behalf of the signed-in user. An access token for the user is acquired
/// (from the token cache) for every call, so the API only ever returns that user's data.
/// </summary>
public sealed class NotepalApiClient(IDownstreamApi api, AuthenticationStateProvider authenticationState, ILogger<NotepalApiClient> logger)
{
    public const string ServiceName = "NotepalApi";

    public Task<PagedResult<NoteSummaryDto>> ListNotesAsync(int page, int pageSize, CancellationToken ct = default) =>
        SendAsync<PagedResult<NoteSummaryDto>>(HttpMethod.Get, $"api/notes?page={page}&pageSize={pageSize}", null, ct);

    public Task<NoteDto?> GetNoteAsync(Guid noteId, CancellationToken ct = default) =>
        SendOrDefaultAsync<NoteDto>(HttpMethod.Get, $"api/notes/{noteId}", null, ct);

    public async Task<NoteDto> CreateNoteAsync(string? title, IReadOnlyList<UploadItem> files, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        if (!string.IsNullOrWhiteSpace(title))
        {
            content.Add(new StringContent(title.Trim()), "title");
        }

        foreach (var file in files)
        {
            var part = new ByteArrayContent(file.Data);
            part.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
            content.Add(part, "files", file.FileName);
        }

        return await SendAsync<NoteDto>(HttpMethod.Post, "api/notes", content, ct);
    }

    public Task<NoteDto> RenameNoteAsync(Guid noteId, string title, CancellationToken ct = default) =>
        SendAsync<NoteDto>(HttpMethod.Put, $"api/notes/{noteId}", JsonContent.Create(new UpdateNoteRequest(title)), ct);

    public async Task DeleteNoteAsync(Guid noteId, CancellationToken ct = default) =>
        await SendRawAsync(HttpMethod.Delete, $"api/notes/{noteId}", null, ct);

    public Task<PageDto> UpdatePageTextAsync(Guid noteId, Guid pageId, string text, CancellationToken ct = default) =>
        SendAsync<PageDto>(HttpMethod.Put, $"api/notes/{noteId}/pages/{pageId}/text", JsonContent.Create(new UpdatePageTextRequest(text)), ct);

    public Task<PageDto> ReprocessPageAsync(Guid noteId, Guid pageId, CancellationToken ct = default) =>
        SendAsync<PageDto>(HttpMethod.Post, $"api/notes/{noteId}/pages/{pageId}/reprocess", null, ct);

    public Task<PagedResult<SearchResultDto>> SearchAsync(string query, int page, int pageSize, CancellationToken ct = default) =>
        SendAsync<PagedResult<SearchResultDto>>(HttpMethod.Get, $"api/search?q={Uri.EscapeDataString(query)}&page={page}&pageSize={pageSize}", null, ct);

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
            logger.LogWarning("Notepal API {Method} {Path} failed with {Status}: {Problem}", method, path, (int)response.StatusCode, problem);
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
