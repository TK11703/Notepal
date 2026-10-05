using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Notepal.Api.Auth;
using Notepal.Api.Data;
using Notepal.Shared;

namespace Notepal.Api.Endpoints;

/// <summary>The caller's role for a note and, for shared notes, who shared it.</summary>
public sealed record NoteAccess(NoteRole Role, string? SharedBy)
{
    public bool CanEdit => Role is NoteRole.Owner or NoteRole.Contributor;

    public static IResult ReadOnly() =>
        Results.Problem("You have read-only access to this note.", statusCode: StatusCodes.Status403Forbidden);

    public static IResult OwnerOnly(string action) =>
        Results.Problem($"Only the note's owner can {action}.", statusCode: StatusCodes.Status403Forbidden);
}

internal static class NoteAccessQueries
{
    /// <summary>Matches shares addressed to the user, by object id or (for shares created by e-mail address) by sign-in address.</summary>
    public static Expression<Func<NoteShare, bool>> IsFor(string userId, string? email) =>
        s => (s.RecipientId != null && s.RecipientId == userId) || (email != null && s.RecipientEmail == email);

    /// <summary>Notes the user owns or that are shared with them. Applied explicitly in addition to the global query filter.</summary>
    public static IQueryable<Note> VisibleTo(this IQueryable<Note> notes, string userId, string? email)
    {
        var isFor = IsFor(userId, email);
        return notes.Where(n => n.OwnerId == userId || n.Shares.AsQueryable().Any(isFor));
    }

    /// <summary>Notes shared with the user (not owned by them).</summary>
    public static IQueryable<Note> SharedWith(this IQueryable<Note> notes, string userId, string? email)
    {
        var isFor = IsFor(userId, email);
        return notes.Where(n => n.OwnerId != userId && n.Shares.AsQueryable().Any(isFor));
    }

    /// <summary>Resolves the caller's access to a note, or <c>null</c> when the note does not exist or is not visible to them.</summary>
    public static async Task<NoteAccess?> GetAccessAsync(this NotepalDbContext db, Guid noteId, ICurrentUser user, CancellationToken ct)
    {
        var userId = user.UserId!;
        var email = user.Email;
        var isFor = IsFor(userId, email);
        var row = await db.Notes
            .Where(n => n.Id == noteId)
            .VisibleTo(userId, email)
            .Select(n => new
            {
                n.OwnerId,
                Shares = n.Shares.AsQueryable().Where(isFor)
                    .Select(s => new { s.Permission, s.OwnerName, s.OwnerEmail })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return null;
        }

        if (row.OwnerId == userId)
        {
            return new NoteAccess(NoteRole.Owner, null);
        }

        // A person can match more than one share (by id and by address); the most generous permission wins.
        var contributor = row.Shares.Any(s => s.Permission == SharePermission.Contributor);
        var sharer = row.Shares.Select(s => s.OwnerName ?? s.OwnerEmail).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        return new NoteAccess(contributor ? NoteRole.Contributor : NoteRole.Reader, sharer);
    }
}
