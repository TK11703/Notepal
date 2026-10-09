using Notepal.Api.Auth;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.LeaveSharedNotes;

internal static class LeaveSharedNotesEndpoint
{
    public static void Map(RouteGroupBuilder shared) =>
        shared.MapPost("/with-me/leave", HandleAsync).Produces<LeaveSharedNotesResult>();

    private static async Task<IResult> HandleAsync(
        LeaveSharedNotesRequest body, SharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        var (noteIds, error) = LeaveSharedNotesValidator.Validate(body);
        if (error is not null)
        {
            return error;
        }

        return Results.Ok(new LeaveSharedNotesResult(await shares.LeaveAsync(noteIds, user.ToDatabaseUser(), ct)));
    }
}
