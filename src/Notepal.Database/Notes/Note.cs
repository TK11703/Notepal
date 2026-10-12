using Notepal.Contracts;

namespace Notepal.Database;

/// <summary>Row of the <c>notes</c> table.</summary>
internal sealed class Note
{
    public Guid Id { get; set; }

    /// <summary>Entra ID object id (<c>oid</c>) of the user who owns the note.</summary>
    public string OwnerId { get; set; } = null!;

    public string Title { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Normalized (see <see cref="TagLimits.Normalize"/>), distinct and sorted tags.</summary>
    public string[] Tags { get; set; } = [];
}
