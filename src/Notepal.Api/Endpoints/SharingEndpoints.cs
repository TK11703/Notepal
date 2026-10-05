using Notepal.Api.Auth;
using Notepal.Api.Data;
using Notepal.Shared;

namespace Notepal.Api.Endpoints;

/// <summary>
/// Sharing notes with other people in the organisation. Only a note's owner can see and manage its shares;
/// the people it is shared with get <see cref="SharePermission.Reader"/> or <see cref="SharePermission.Contributor"/> access.
/// </summary>
public static class SharingEndpoints
{
    public static RouteGroupBuilder MapSharingEndpoints(this RouteGroupBuilder api)
    {
        var shares = api.MapGroup("/notes/{noteId:guid}/shares").WithTags("Sharing");
        shares.MapGet("/", ListShares);
        shares.MapPost("/", AddShare);
        shares.MapPut("/{shareId:guid}", UpdateShare);
        shares.MapDelete("/{shareId:guid}", RemoveShare);

        var shared = api.MapGroup("/shared").WithTags("Sharing");
        shared.MapGet("/by-me", SharedByMe);
        shared.MapGet("/with-me", SharedWithMe);
        shared.MapPost("/with-me/leave", LeaveSharedNotes);

        return api;
    }

    private static async Task<IResult> ListShares(Guid noteId, NotesRepository notes, SharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        var denied = await RequireOwnerAsync(notes, noteId, user, ct);
        if (denied is not null)
        {
            return denied;
        }

        var list = await shares.ListSharesAsync(user.UserId!, noteId, ct);
        return Results.Ok(list.Select(s => s.ToDto()).ToList());
    }

    private static async Task<IResult> AddShare(Guid noteId, AddNoteShareRequest body, NotesRepository notes, SharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        var ownerId = user.UserId!;
        var email = ShareLimits.NormalizeEmail(body.Email);
        if (email is null)
        {
            return Invalid("email", "Enter a valid e-mail address.");
        }

        if (!Enum.IsDefined(body.Permission))
        {
            return Invalid("permission", "Permission must be Reader or Contributor.");
        }

        var recipientId = string.IsNullOrWhiteSpace(body.UserId) ? null : body.UserId.Trim();
        if (recipientId is { Length: > 128 })
        {
            return Invalid("userId", "The user id is not valid.");
        }

        var denied = await RequireOwnerAsync(notes, noteId, user, ct);
        if (denied is not null)
        {
            return denied;
        }

        if (recipientId == ownerId || email == user.Email)
        {
            return Invalid("email", "You own this note already.");
        }

        var (outcome, share) = await shares.UpsertShareAsync(ownerId, noteId, new ShareDetails(
            email,
            recipientId,
            Clean(body.DisplayName, ShareLimits.MaxDisplayNameLength),
            body.Permission,
            Clean(user.DisplayName, ShareLimits.MaxDisplayNameLength),
            user.Email), ct);

        return outcome switch
        {
            UpsertShareOutcome.NotFound => Results.NotFound(),
            UpsertShareOutcome.TooMany => Invalid("email", $"A note can be shared with at most {ShareLimits.MaxSharesPerNote} people."),
            UpsertShareOutcome.Created => Results.Created($"/api/notes/{noteId}/shares/{share!.Id}", share.ToDto()),
            _ => Results.Ok(share!.ToDto()),
        };
    }

    private static async Task<IResult> UpdateShare(Guid noteId, Guid shareId, UpdateNoteShareRequest body, NotesRepository notes, SharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        if (!Enum.IsDefined(body.Permission))
        {
            return Invalid("permission", "Permission must be Reader or Contributor.");
        }

        var denied = await RequireOwnerAsync(notes, noteId, user, ct);
        if (denied is not null)
        {
            return denied;
        }

        var share = await shares.UpdatePermissionAsync(user.UserId!, noteId, shareId, body.Permission, ct);
        return share is null ? Results.NotFound() : Results.Ok(share.ToDto());
    }

    /// <summary>The owner removes someone; a recipient may also remove their own share to leave a note.</summary>
    private static async Task<IResult> RemoveShare(Guid noteId, Guid shareId, NotesRepository notes, SharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        if (await shares.RemoveShareAsync(noteId, shareId, user, ct))
        {
            return Results.NoContent();
        }

        return await notes.GetAccessAsync(noteId, user, ct) is { Role: not NoteRole.Owner }
            ? NoteAccess.OwnerOnly("change who it is shared with")
            : Results.NotFound();
    }

    private static async Task<IResult> SharedByMe(SharesRepository shares, ICurrentUser user, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        (page, pageSize) = Paging.Normalize(page, pageSize);
        return Results.Ok(await shares.SharedByMeAsync(user.UserId!, page, pageSize, ct));
    }

    private static async Task<IResult> SharedWithMe(SharesRepository shares, ICurrentUser user, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        (page, pageSize) = Paging.Normalize(page, pageSize);
        return Results.Ok(await shares.SharedWithMeAsync(user, page, pageSize, ct));
    }

    /// <summary>
    /// The caller leaves one or more notes shared with them by removing their own shares. Shares of other people and
    /// the caller's own notes are never touched; ids the caller has no share for are ignored.
    /// </summary>
    private static async Task<IResult> LeaveSharedNotes(LeaveSharedNotesRequest body, SharesRepository shares, ICurrentUser user, CancellationToken ct)
    {
        var noteIds = (body.NoteIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (noteIds.Count == 0)
        {
            return Invalid(nameof(body.NoteIds), "Choose at least one note to leave.");
        }

        if (noteIds.Count > ShareLimits.MaxNotesPerLeave)
        {
            return Invalid(nameof(body.NoteIds), $"You can leave up to {ShareLimits.MaxNotesPerLeave} notes at a time.");
        }

        return Results.Ok(new LeaveSharedNotesResult(await shares.LeaveAsync(noteIds, user, ct)));
    }

    private static async Task<IResult?> RequireOwnerAsync(NotesRepository notes, Guid noteId, ICurrentUser user, CancellationToken ct)
    {
        var access = await notes.GetAccessAsync(noteId, user, ct);
        return access is null ? Results.NotFound()
            : access.Role == NoteRole.Owner ? null
            : NoteAccess.OwnerOnly("change who it is shared with");
    }

    private static string? Clean(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : NotesEndpoints.Truncate(value.Trim(), length);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
