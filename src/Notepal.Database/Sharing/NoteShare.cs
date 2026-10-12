using Notepal.Contracts;

namespace Notepal.Database;

/// <summary>Row of the <c>note_shares</c> table: grants another person access to a note. Only the note's owner can create, change or remove shares.</summary>
internal sealed class NoteShare
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }

    /// <summary>Denormalized owner id of the note (the person who shared it).</summary>
    public string OwnerId { get; set; } = null!;

    /// <summary>Owner's name and email when the note was shared, shown to the recipient.</summary>
    public string? OwnerName { get; set; }
    public string? OwnerEmail { get; set; }

    /// <summary>Recipient's Entra object id when they were picked from the directory; otherwise <c>null</c>.</summary>
    public string? RecipientId { get; set; }

    /// <summary>Normalized (lower case) email address of the recipient. Shares without an id are matched on it.</summary>
    public string RecipientEmail { get; set; } = null!;
    public string? RecipientName { get; set; }
    public SharePermission Permission { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
