using Microsoft.AspNetCore.Mvc;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Database;
using Notepal.Contracts;

namespace Notepal.Api.Features.Search.SearchNotes;

internal static class SearchNotesEndpoint
{
    /// <summary>Markers wrapped around matched terms in snippets. Clients HTML-encode the snippet and render these as highlights.</summary>
    public const string HighlightStart = "\u27E6";
    public const string HighlightEnd = "\u27E7";

    private static readonly string HeadlineOptions =
        $"StartSel={HighlightStart}, StopSel={HighlightEnd}, MaxWords=35, MinWords=15, MaxFragments=2, FragmentDelimiter=\" … \"";

    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/search", Search).WithTags("Search").Produces<PagedResult<SearchResultDto>>();

    private static async Task<IResult> Search(string? q, NotesRepository notes, ICurrentUser user, [FromQuery(Name = "tag")] string[]? tags, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var ownerId = user.UserId!;
        (page, pageSize) = Paging.Normalize(page, pageSize);
        var term = q?.Trim() ?? string.Empty;
        var wantedTags = TagLimits.NormalizeAll(tags);
        if (term.Length == 0)
        {
            return wantedTags.Count == 0
                ? Results.Ok(new PagedResult<SearchResultDto>([], 0, page, pageSize))
                : Results.Ok(await notes.SearchByTagsAsync(ownerId, wantedTags, page, pageSize, ct));
        }

        if (term.Length > NoteLimits.MaxSearchLength)
        {
            term = term[..NoteLimits.MaxSearchLength];
        }

        return Results.Ok(await notes.SearchAsync(ownerId, term, wantedTags, HeadlineOptions, page, pageSize, ct));
    }
}
