using Notepal.Contracts;

namespace Notepal.Database;

/// <summary>The caller's role for a note and, for shared notes, who shared it.</summary>
public sealed record NoteAccess(NoteRole Role, string? SharedBy)
{
    public bool CanEdit => Role is NoteRole.Owner or NoteRole.Contributor;
}
