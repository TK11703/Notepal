using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.UpdateShare;

internal static class UpdateShareEndpoint
{
    public static void Map(RouteGroupBuilder shares) =>
        shares.MapPut("/{shareId:guid}", HandleAsync).Produces<NoteShareDto>();

    private static async Task<IResult> HandleAsync(Guid noteId, Guid shareId, UpdateNoteShareRequest body,
        NotesRepository notes, SharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        var error = UpdateShareValidator.Validate(body);
        if (error is not null)
        {
            return error;
        }

        var denied = await NoteAuthorization.RequireOwnerAsync(notes, noteId, user, ct);
        if (denied is not null)
        {
            return denied;
        }

        var share = await shares.UpdatePermissionAsync(user.UserId!, noteId, shareId, body.Permission, ct);
        return share is null ? Results.NotFound() : Results.Ok(share);
    }
}
