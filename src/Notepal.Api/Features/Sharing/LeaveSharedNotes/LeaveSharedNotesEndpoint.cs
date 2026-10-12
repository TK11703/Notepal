using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.LeaveSharedNotes;

internal static class LeaveSharedNotesEndpoint
{
    public static void Map(RouteGroupBuilder shared) =>
        shared.MapPost("/with-me/leave", HandleAsync)
            .WithName("LeaveSharedNotes")
            .Produces<LeaveSharedNotesResult>();

    private static async Task<Results<Ok<LeaveSharedNotesResult>, ValidationProblem>> HandleAsync(
        LeaveSharedNotesRequest body, ISharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        var (noteIds, error) = LeaveSharedNotesValidator.Validate(body);
        if (error is not null)
        {
            return error;
        }

        return TypedResults.Ok(new LeaveSharedNotesResult(await shares.LeaveAsync(noteIds, user.ToDatabaseUser(), ct)));
    }
}
