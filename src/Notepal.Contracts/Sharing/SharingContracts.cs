namespace Notepal.Contracts;

/// <summary>What the caller may do with a note.</summary>
public enum NoteRole
{
    /// <summary>Created the note: full control, including sharing and deleting it.</summary>
    Owner = 0,

    /// <summary>Shared with edit rights: can rename, tag, correct text and add, remove and reorder pages, but not delete or share.</summary>
    Contributor = 1,

    /// <summary>Shared read-only.</summary>
    Reader = 2,
}

/// <summary>Permission granted to a person a note is shared with.</summary>
public enum SharePermission
{
    Reader = 0,
    Contributor = 1,
}

/// <summary>A person a note is shared with.</summary>
public sealed record NoteShareDto(
    Guid Id,
    string Email,
    string? DisplayName,
    SharePermission Permission,
    DateTimeOffset CreatedAt);

/// <summary>
/// Shares a note with someone. <see cref="UserId"/> (the Entra object id) is set when the person was picked from the directory;
/// otherwise the share is matched by email address when they sign in.
/// </summary>
public sealed record AddNoteShareRequest(string Email, string? DisplayName = null, string? UserId = null, SharePermission Permission = SharePermission.Reader);

public sealed record UpdateNoteShareRequest(SharePermission Permission);

/// <summary>
/// A note on the Shared notes page. For notes the caller shared, <see cref="SharedWith"/> lists the people;
/// for notes shared with the caller, <see cref="SharedBy"/> names the owner and <see cref="Role"/> is the caller's permission.
/// </summary>
public sealed record SharedNoteDto(
    NoteSummaryDto Note,
    NoteRole Role,
    string? SharedBy,
    DateTimeOffset SharedAt,
    IReadOnlyList<NoteShareDto> SharedWith);

/// <summary>Removes the caller's access to notes other people shared with them (the notes themselves are not changed).</summary>
public sealed record LeaveSharedNotesRequest(IReadOnlyList<Guid> NoteIds);

/// <summary>How many of the requested notes the caller no longer has access to.</summary>
public sealed record LeaveSharedNotesResult(int Left);

/// <summary>A person found in the organization's directory.</summary>
public sealed record DirectoryUserDto(string Id, string DisplayName, string Email);

public static class ShareLimits
{
    public const int MaxSharesPerNote = 50;
    public const int MaxEmailLength = 254;
    public const int MaxDisplayNameLength = 200;
    public const int MaxNotesPerLeave = 100;

    /// <summary>Trims and lower-cases an email address; returns <c>null</c> unless it looks like <c>name@domain.tld</c>.</summary>
    public static string? NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value.Length > MaxEmailLength || value.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@'))
        {
            return null;
        }

        var domain = value[(at + 1)..];
        var dot = domain.IndexOf('.');
        return dot > 0 && dot < domain.Length - 1 ? value : null;
    }
}
