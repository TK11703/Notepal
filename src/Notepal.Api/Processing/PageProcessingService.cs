using Microsoft.EntityFrameworkCore;
using Notepal.Api.Data;
using Notepal.Api.Ocr;
using Notepal.Shared;

namespace Notepal.Api.Processing;

/// <summary>
/// Background worker that extracts text for queued pages. On start-up it re-queues anything left pending
/// (e.g. when the container was scaled to zero mid-way), so no upload is ever lost.
/// </summary>
public sealed class PageProcessingService(
    ProcessingQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<PageProcessingService> logger) : BackgroundService
{
    private const int MaxConcurrency = 2;
    private const int MaxAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        await Parallel.ForEachAsync(
            queue.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = stoppingToken },
            async (pageId, ct) =>
            {
                try
                {
                    await ProcessAsync(pageId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected failure processing page {PageId}", pageId);
                }
            });
    }

    private async Task RequeueUnfinishedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotepalDbContext>();
            var ids = await db.Pages.IgnoreQueryFilters()
                .Where(p => p.Status == ProcessingStatus.Pending || p.Status == ProcessingStatus.Processing)
                .OrderBy(p => p.UpdatedAt)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);

            foreach (var id in ids)
            {
                queue.Enqueue(id);
            }

            if (ids.Count > 0)
            {
                logger.LogInformation("Re-queued {Count} unfinished pages", ids.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to re-queue unfinished pages");
        }
    }

    internal async Task ProcessAsync(Guid pageId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotepalDbContext>();
        var extractor = scope.ServiceProvider.GetRequiredService<TextExtractionService>();

        // The worker runs outside of a user request, so it bypasses the per-user filter and works on a single page id.
        var page = await db.Pages.IgnoreQueryFilters().Include(p => p.Content).FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page?.Content is null || page.Status is ProcessingStatus.Completed or ProcessingStatus.Failed)
        {
            return;
        }

        page.Status = ProcessingStatus.Processing;
        page.Attempts++;
        page.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var text = await extractor.ExtractAsync(page.Content.Data, page.ContentType, page.FileName, cancellationToken);
            page.ExtractedText = text;
            page.Status = ProcessingStatus.Completed;
            page.Error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave as Processing; it is re-queued on the next start.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Text extraction failed for page {PageId} (attempt {Attempt})", page.Id, page.Attempts);
            var retry = ex is not (OcrUnavailableException or NotSupportedException) && page.Attempts < MaxAttempts;
            page.Status = retry ? ProcessingStatus.Pending : ProcessingStatus.Failed;
            page.Error = Truncate(ex is OcrUnavailableException ? ex.Message : $"Text extraction failed: {ex.Message}", 2000);
        }

        page.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

        if (page.Status == ProcessingStatus.Pending)
        {
            _ = RequeueLaterAsync(page.Id, TimeSpan.FromSeconds(10 * page.Attempts), cancellationToken);
        }
    }

    private async Task RequeueLaterAsync(Guid pageId, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            queue.Enqueue(pageId);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
