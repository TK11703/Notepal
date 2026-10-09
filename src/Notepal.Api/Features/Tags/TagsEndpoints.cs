using Notepal.Api.Features.Tags.ListTags;
using Notepal.Api.Features.Tags.UpdateNoteTags;

namespace Notepal.Api.Features.Tags;

public static class TagsEndpoints
{
    public static RouteGroupBuilder MapTagsEndpoints(this RouteGroupBuilder api)
    {
        var tags = api.MapGroup("").WithTags("Tags");
        ListTagsEndpoint.Map(tags);
        UpdateNoteTagsEndpoint.Map(tags);
        return api;
    }
}
