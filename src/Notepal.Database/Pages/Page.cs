using Notepal.Contracts;

namespace Notepal.Database;

/// <summary>Row of the <c>pages</c> table: a captured artifact (photo, image, PDF or Word document) of a note.</summary>
internal sealed class Page
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
