using Notepal.Api.Features.Sharing.AddShare;
using Notepal.Api.Features.Sharing.LeaveSharedNotes;
using Notepal.Api.Features.Sharing.ListShares;
using Notepal.Api.Features.Sharing.RemoveShare;
using Notepal.Api.Features.Sharing.SharedByMe;
using Notepal.Api.Features.Sharing.SharedWithMe;
using Notepal.Api.Features.Sharing.UpdateShare;

namespace Notepal.Api.Features.Sharing;

public static class SharingEndpoints
{
    public static RouteGroupBuilder MapSharingEndpoints(this RouteGroupBuilder api)
    {
        var shares = api.MapGroup("/notes/{noteId:guid}/shares").WithTags("Sharing");
        ListSharesEndpoint.Map(shares);
        AddShareEndpoint.Map(shares);
        UpdateShareEndpoint.Map(shares);
        RemoveShareEndpoint.Map(shares);

        var shared = api.MapGroup("/shared").WithTags("Sharing");
        SharedByMeEndpoint.Map(shared);
        SharedWithMeEndpoint.Map(shared);
        LeaveSharedNotesEndpoint.Map(shared);
        return api;
    }
}
