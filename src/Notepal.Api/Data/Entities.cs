using NpgsqlTypes;
using Notepal.Shared;

namespace Notepal.Api.Data;

public sealed class Note
{
    public Guid Id { get; set; }

    /// <summary>Entra ID object id (<c>oid</c>) of the user who owns the note.</summary>
    public required string OwnerId { get; set; }

    public required string Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public NpgsqlTsVector TitleSearchVector { get; set; } = null!;

    public List<Page> Pages { get; set; } = [];
}

/// <summary>A captured artifact (photo, uploaded image, PDF or Word document) belonging to a note.</summary>
public sealed class Page
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public Note Note { get; set; } = null!;

    /// <summary>Denormalised owner id so every page query can be filtered by user without a join.</summary>
    public required string OwnerId { get; set; }

    public int PageNumber { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }

    public ProcessingStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }

    /// <summary>Text produced by OCR / document extraction.</summary>
    public string? ExtractedText { get; set; }

    /// <summary>User corrected text. When <c>null</c> the extracted text is used.</summary>
    public string? EditedText { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public NpgsqlTsVector SearchVector { get; set; } = null!;

    public PageContent? Content { get; set; }

    public string? EffectiveText => EditedText ?? ExtractedText;
}

/// <summary>The original uploaded bytes, stored separately so listing pages never loads blobs.</summary>
public sealed class PageContent
{
    public Guid PageId { get; set; }
    public required byte[] Data { get; set; }
}
