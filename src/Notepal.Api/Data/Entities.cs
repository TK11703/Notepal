using Notepal.Shared;

namespace Notepal.Api.Data;

/// <summary>Row of the <c>notes</c> table.</summary>
public sealed class Note
{
    public Guid Id { get; set; }

    /// <summary>Entra ID object id (<c>oid</c>) of the user who owns the note.</summary>
    public string OwnerId { get; set; } = null!;

    public string Title { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of the <c>pages</c> table: a captured artifact (photo, image, PDF or Word document) of a note.</summary>
public sealed class Page
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public string OwnerId { get; set; } = null!;
    public int PageNumber { get; set; }
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public ProcessingStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }

    /// <summary>Text produced by OCR / document extraction.</summary>
    public string? ExtractedText { get; set; }

    /// <summary>User corrected text. When <c>null</c> the extracted text is used.</summary>
    public string? EditedText { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? EffectiveText => EditedText ?? ExtractedText;
}

/// <summary>Row of the <c>page_contents</c> table: the original uploaded bytes.</summary>
public sealed class PageContent
{
    public Guid PageId { get; set; }
    public byte[] Data { get; set; } = null!;
}
