using System.ClientModel;
using Azure;
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
    PageWorkRepository pages,
    TextExtractionService extractor,
    ILogger<PageProcessingService> logger) : BackgroundService
{
    private const int MaxConcurrency = 2;
    private const int MaxAttempts = 3;
    private const int MaxErrorLength = 2000;

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
            var ids = await pages.GetUnfinishedPageIdsAsync(cancellationToken);
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
        var work = await pages.TryStartAsync(pageId, cancellationToken);
        if (work is null)
        {
            return;
        }

        string text;
        try
        {
            text = await extractor.ExtractAsync(work.Data, work.ContentType, work.FileName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Left as Processing; it is re-queued on the next start.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Text extraction failed for page {PageId} (attempt {Attempt})", pageId, work.Attempts);
            var retry = ex is not (OcrUnavailableException or NotSupportedException) && work.Attempts < MaxAttempts;
            var error = ex switch
            {
                OcrUnavailableException => ex.Message,
                // Service errors carry raw JSON and response headers; the full details are in the log above.
                RequestFailedException failed => $"Text extraction failed: the OCR service returned an error ({failed.Status}). Details are in the API logs.",
                ClientResultException failed => $"Text extraction failed: the OCR service returned an error ({failed.Status}). Details are in the API logs.",
                _ => $"Text extraction failed: {ex.Message}",
            };
            await pages.FailAsync(pageId, retry ? ProcessingStatus.Pending : ProcessingStatus.Failed, Truncate(error, MaxErrorLength), CancellationToken.None);

            if (retry)
            {
                _ = RequeueLaterAsync(pageId, TimeSpan.FromSeconds(10 * work.Attempts), cancellationToken);
            }

            return;
        }

        await pages.CompleteAsync(pageId, text, CancellationToken.None);
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
