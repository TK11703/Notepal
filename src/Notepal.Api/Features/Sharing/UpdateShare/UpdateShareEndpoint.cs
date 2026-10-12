using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.UpdateShare;

internal static class UpdateShareEndpoint
{
    public static void Map(RouteGroupBuilder shares) =>
        shares.MapPut("/{shareId:guid}", HandleAsync)
            .WithName("UpdateShare")
            .Produces<NoteShareDto>();

    private static async Task<Results<Ok<NoteShareDto>, ValidationProblem, NotFound, ProblemHttpResult>> HandleAsync(
        Guid noteId, Guid shareId, UpdateNoteShareRequest body,
        INotesRepository notes, ISharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        var error = UpdateShareValidator.Validate(body);
        if (error is not null)
        {
            return error;
        }

        var (allowed, forbidden, notFound) = await NoteAuthorization.RequireOwnerAsync(notes, noteId, user, ct);
        if (notFound)
        {
            return TypedResults.NotFound();
        }
        if (forbidden is not null)
        {
            return forbidden;
        }

        var share = await shares.UpdatePermissionAsync(user.UserId!, noteId, shareId, body.Permission, ct);
        return share is null ? TypedResults.NotFound() : TypedResults.Ok(share);
    }
}
