using Notepal.Contracts;

namespace Notepal.Database;

public sealed record NewPage(string FileName, string ContentType, byte[] Data);

public sealed class PageOriginal
{
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public byte[] Data { get; set; } = null!;
}

public enum ReprocessOutcome
{
    NotFound,
    AlreadyProcessing,
    Queued,
}

public enum AddPagesOutcome
{
    NotFound,
    TooMany,
    Added,
}

public enum PageChangeOutcome
{
    NotFound,
    LastPage,
    Changed,
    TargetFull,
}

public sealed record AddPagesResult(AddPagesOutcome Outcome, NoteDto? Note = null, IReadOnlyList<Guid>? PageIds = null, int Remaining = 0);
