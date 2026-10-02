using System.ComponentModel.DataAnnotations;
using Notepal.Api.Auth;
using Notepal.Api.Data;
using Notepal.Shared;

namespace Notepal.Api.Endpoints;

public static class SearchEndpoints
{
    /// <summary>Markers wrapped around matched terms in snippets. Clients HTML-encode the snippet and render these as highlights.</summary>
    public const string HighlightStart = "\u27E6";
    public const string HighlightEnd = "\u27E7";

    private static readonly string HeadlineOptions =
        $"StartSel={HighlightStart}, StopSel={HighlightEnd}, MaxWords=35, MinWords=15, MaxFragments=2, FragmentDelimiter=\" … \"";

    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/search", Search).WithTags("Search");
        return api;
    }

    private static async Task<IResult> Search(
        [MaxLength(NoteLimits.MaxSearchLength)] string? q,
        NotesRepository notes,
        ICurrentUser user,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, NoteLimits.MaxPageSize)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var term = q?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            return Results.Ok(new PagedResult<SearchResultDto>([], 0, page, pageSize));
        }

        return Results.Ok(await notes.SearchAsync(user.UserId!, term, HeadlineOptions, page, pageSize, ct));
    }
}
