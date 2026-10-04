using Microsoft.EntityFrameworkCore;
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

        return api;
    }

    private static async Task<IResult> ListShares(Guid noteId, NotepalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var denied = await RequireOwnerAsync(db, noteId, user, ct);
        if (denied is not null)
        {
            return denied;
        }

        var shares = await db.NoteShares
            .Where(s => s.NoteId == noteId && s.OwnerId == user.UserId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(shares.Select(s => s.ToDto()).ToList());
    }

    private static async Task<IResult> AddShare(Guid noteId, AddNoteShareRequest body, NotepalDbContext db, ICurrentUser user, CancellationToken ct)
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

        var denied = await RequireOwnerAsync(db, noteId, user, ct);
        if (denied is not null)
        {
            return denied;
        }

        if (recipientId == ownerId || email == user.Email)
        {
            return Invalid("email", "You own this note already.");
        }

        var name = string.IsNullOrWhiteSpace(body.DisplayName) ? null : NotesEndpoints.Truncate(body.DisplayName.Trim(), ShareLimits.MaxDisplayNameLength);
        var existing = await db.NoteShares
            .Where(s => s.NoteId == noteId && s.OwnerId == ownerId)
            .ToListAsync(ct);

        // Adding someone again (by address or by directory id) updates their existing share instead of duplicating it.
        var share = existing.FirstOrDefault(s => s.RecipientEmail == email)
            ?? (recipientId is null ? null : existing.FirstOrDefault(s => s.RecipientId == recipientId));
        var created = share is null;
        if (share is null)
        {
            if (existing.Count >= ShareLimits.MaxSharesPerNote)
            {
                return Invalid("email", $"A note can be shared with at most {ShareLimits.MaxSharesPerNote} people.");
            }

            share = new NoteShare
            {
                Id = Guid.NewGuid(),
                NoteId = noteId,
                OwnerId = ownerId,
                RecipientEmail = email,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.NoteShares.Add(share);
        }

        share.RecipientEmail = email;
        share.RecipientId = recipientId ?? share.RecipientId;
        share.RecipientName = name ?? share.RecipientName;
        share.Permission = body.Permission;
        share.OwnerName = Clean(user.DisplayName, ShareLimits.MaxDisplayNameLength);
        share.OwnerEmail = user.Email;
        await db.SaveChangesAsync(ct);

        return created
            ? Results.Created($"/api/notes/{noteId}/shares/{share.Id}", share.ToDto())
            : Results.Ok(share.ToDto());
    }

    private static async Task<IResult> UpdateShare(Guid noteId, Guid shareId, UpdateNoteShareRequest body, NotepalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (!Enum.IsDefined(body.Permission))
        {
            return Invalid("permission", "Permission must be Reader or Contributor.");
        }

        var denied = await RequireOwnerAsync(db, noteId, user, ct);
        if (denied is not null)
        {
            return denied;
        }

        var share = await db.NoteShares.FirstOrDefaultAsync(s => s.Id == shareId && s.NoteId == noteId && s.OwnerId == user.UserId, ct);
        if (share is null)
        {
            return Results.NotFound();
        }

        share.Permission = body.Permission;
        await db.SaveChangesAsync(ct);
        return Results.Ok(share.ToDto());
    }

    /// <summary>The owner removes someone; a recipient may also remove their own share to leave a note.</summary>
    private static async Task<IResult> RemoveShare(Guid noteId, Guid shareId, NotepalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var userId = user.UserId!;
        var email = user.Email;
        var removed = await db.NoteShares
            .Where(s => s.Id == shareId && s.NoteId == noteId)
            .Where(s => s.OwnerId == userId || (s.RecipientId != null && s.RecipientId == userId) || (email != null && s.RecipientEmail == email))
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
        {
            return Results.NoContent();
        }

        return await db.GetAccessAsync(noteId, user, ct) is { Role: not NoteRole.Owner }
            ? NoteAccess.OwnerOnly("change who it is shared with")
            : Results.NotFound();
    }

    /// <summary>Notes the caller owns and has shared with at least one person, most recently updated first.</summary>
    private static async Task<IResult> SharedByMe(NotepalDbContext db, ICurrentUser user, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var ownerId = user.UserId!;
        (page, pageSize) = Paging.Normalize(page, pageSize);

        var query = db.Notes.Where(n => n.OwnerId == ownerId && n.Shares.Any());
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(n => n.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new
            {
                n.Id,
                n.Title,
                n.CreatedAt,
                n.UpdatedAt,
                n.Tags,
                Statuses = n.Pages.Select(p => p.Status).ToList(),
                Preview = n.Pages.OrderBy(p => p.PageNumber)
                    .Select(p => (p.EditedText ?? p.ExtractedText)!.Substring(0, 240))
                    .FirstOrDefault(),
                Shares = n.Shares.OrderBy(s => s.CreatedAt).ToList(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new SharedNoteDto(
                new NoteSummaryDto(r.Id, r.Title, r.CreatedAt, r.UpdatedAt, r.Statuses.Count, Mapping.AggregateStatus(r.Statuses), r.Preview, r.Tags),
                NoteRole.Owner,
                null,
                r.Shares.Max(s => s.CreatedAt),
                r.Shares.Select(s => s.ToDto()).ToList()))
            .ToList();

        return Results.Ok(new PagedResult<SharedNoteDto>(items, total, page, pageSize));
    }

    /// <summary>Notes other people have shared with the caller, most recently updated first.</summary>
    private static async Task<IResult> SharedWithMe(NotepalDbContext db, ICurrentUser user, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var userId = user.UserId!;
        var email = user.Email;
        var isFor = NoteAccessQueries.IsFor(userId, email);
        (page, pageSize) = Paging.Normalize(page, pageSize);

        var query = db.Notes.SharedWith(userId, email);
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(n => n.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new
            {
                n.Id,
                n.Title,
                n.CreatedAt,
                n.UpdatedAt,
                n.Tags,
                Statuses = n.Pages.Select(p => p.Status).ToList(),
                Preview = n.Pages.OrderBy(p => p.PageNumber)
                    .Select(p => (p.EditedText ?? p.ExtractedText)!.Substring(0, 240))
                    .FirstOrDefault(),
                Shares = n.Shares.AsQueryable().Where(isFor)
                    .Select(s => new { s.Permission, s.OwnerName, s.OwnerEmail, s.CreatedAt })
                    .ToList(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new SharedNoteDto(
                new NoteSummaryDto(r.Id, r.Title, r.CreatedAt, r.UpdatedAt, r.Statuses.Count, Mapping.AggregateStatus(r.Statuses), r.Preview, r.Tags),
                r.Shares.Any(s => s.Permission == SharePermission.Contributor) ? NoteRole.Contributor : NoteRole.Reader,
                r.Shares.Select(s => s.OwnerName ?? s.OwnerEmail).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                r.Shares.Min(s => s.CreatedAt),
                []))
            .ToList();

        return Results.Ok(new PagedResult<SharedNoteDto>(items, total, page, pageSize));
    }

    private static async Task<IResult?> RequireOwnerAsync(NotepalDbContext db, Guid noteId, ICurrentUser user, CancellationToken ct)
    {
        var access = await db.GetAccessAsync(noteId, user, ct);
        return access is null ? Results.NotFound()
            : access.Role == NoteRole.Owner ? null
            : NoteAccess.OwnerOnly("change who it is shared with");
    }

    private static string? Clean(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : NotesEndpoints.Truncate(value.Trim(), length);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
