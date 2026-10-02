using Microsoft.EntityFrameworkCore;
using Notepal.Api.Auth;
using Notepal.Api.Data;
using Notepal.Shared;

namespace Notepal.Api.Endpoints;

public static class SearchEndpoints
{
    /// <summary>Markers wrapped around matched terms in snippets. Clients HTML-encode the snippet and render these as highlights.</summary>
    public const string HighlightStart = "\u27E6";
    public const string HighlightEnd = "\u27E7";

    private const string Config = "english";
    private static readonly string HeadlineOptions =
        $"StartSel={HighlightStart}, StopSel={HighlightEnd}, MaxWords=35, MinWords=15, MaxFragments=2, FragmentDelimiter=\" … \"";

    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/search", Search).WithTags("Search");
        return api;
    }

    private static async Task<IResult> Search(string? q, NotepalDbContext db, ICurrentUser user, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var ownerId = user.UserId!;
        (page, pageSize) = Paging.Normalize(page, pageSize);
        var term = q?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            return Results.Ok(new PagedResult<SearchResultDto>([], 0, page, pageSize));
        }

        if (term.Length > 200)
        {
            term = term[..200];
        }

        var pattern = $"%{EscapeLike(term)}%";

        // Full text search (stemmed, ranked) over corrected/extracted text and note titles, plus a
        // substring match so partial words still find something. Always scoped to the caller.
        var query = db.Pages
            .Where(p => p.OwnerId == ownerId)
            .Where(p =>
                p.SearchVector.Matches(EF.Functions.WebSearchToTsQuery(Config, term)) ||
                p.Note.TitleSearchVector.Matches(EF.Functions.WebSearchToTsQuery(Config, term)) ||
                EF.Functions.ILike(p.Note.Title, pattern, "\\") ||
                EF.Functions.ILike(p.EditedText ?? p.ExtractedText ?? "", pattern, "\\"));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(p =>
                p.SearchVector.Rank(EF.Functions.WebSearchToTsQuery(Config, term)) +
                p.Note.TitleSearchVector.Rank(EF.Functions.WebSearchToTsQuery(Config, term)) * 2)
            .ThenByDescending(p => p.Note.UpdatedAt)
            .ThenBy(p => p.PageNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new SearchResultDto(
                p.NoteId,
                p.Note.Title,
                p.Id,
                p.PageNumber,
                EF.Functions.WebSearchToTsQuery(Config, term)
                    .GetResultHeadline(Config, p.EditedText ?? p.ExtractedText ?? "", HeadlineOptions),
                p.Note.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<SearchResultDto>(rows, total, page, pageSize));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
