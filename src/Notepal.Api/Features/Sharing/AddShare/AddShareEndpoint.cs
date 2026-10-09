using Notepal.Api.Auth;
using Notepal.Api.Features.Common;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Features.Sharing.AddShare;

internal static class AddShareEndpoint
{
    public static void Map(RouteGroupBuilder shares) =>
        shares.MapPost("/", HandleAsync)
            .Produces<NoteShareDto>(StatusCodes.Status201Created)
            .Produces<NoteShareDto>();

    private static async Task<IResult> HandleAsync(Guid noteId, AddNoteShareRequest body,
        NotesRepository notes, SharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        var (email, recipientId, error) = AddShareValidator.Validate(body);
        if (error is not null)
        {
            return error;
        }

        var denied = await NoteAuthorization.RequireOwnerAsync(notes, noteId, user, ct);
        if (denied is not null)
        {
            return denied;
        }

        var recipientError = AddShareValidator.ValidateRecipient(ownerId, user.Email, email, recipientId);
        if (recipientError is not null)
        {
            return recipientError;
        }

        var (outcome, share) = await shares.UpsertShareAsync(ownerId, noteId, new ShareDetails(
            email,
            recipientId,
            AddShareValidator.NormalizeName(body.DisplayName),
            body.Permission,
            AddShareValidator.NormalizeName(user.DisplayName),
            user.Email), ct);

        return outcome switch
        {
            UpsertShareOutcome.NotFound => Results.NotFound(),
            UpsertShareOutcome.TooMany => ShareValidation.Invalid("email", $"A note can be shared with at most {ShareLimits.MaxSharesPerNote} people."),
            UpsertShareOutcome.Created => Results.Created($"/api/notes/{noteId}/shares/{share!.Id}", share),
            _ => Results.Ok(share),
        };
    }

}
