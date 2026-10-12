namespace Notepal.Database;

/// <summary>Row of the <c>page_contents</c> table: the original uploaded bytes.</summary>
internal sealed class PageContent
{
    public Guid PageId { get; set; }
    public byte[] Data { get; set; } = null!;
}
