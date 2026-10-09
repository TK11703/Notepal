using Notepal.Api.Features.Search.SearchNotes;

namespace Notepal.Api.Features.Search;

public static class SearchEndpoints
{
    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder api)
    {
        SearchNotesEndpoint.Map(api);
        return api;
    }
}
