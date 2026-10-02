using Azure.AI.Agents.Persistent;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace Notepal.Api.Ocr;

/// <summary>
/// Performs OCR with an Azure AI Foundry agent: the image is uploaded to the project, attached to a new thread,
/// a run is executed against the OCR agent and the assistant reply is returned. Threads and files are deleted afterwards.
/// </summary>
public sealed class FoundryAgentOcrClient : IOcrClient
{
    private readonly PersistentAgentsClient _client;
    private readonly OcrOptions _options;
    private readonly ILogger<FoundryAgentOcrClient> _logger;
    private readonly SemaphoreSlim _agentLock = new(1, 1);
    private string? _agentId;

    public FoundryAgentOcrClient(IOptions<OcrOptions> options, ILogger<FoundryAgentOcrClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _agentId = string.IsNullOrWhiteSpace(_options.AgentId) ? null : _options.AgentId;

        TokenCredential credential = string.IsNullOrWhiteSpace(_options.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(_options.ManagedIdentityClientId));

        _client = new PersistentAgentsClient(_options.ProjectEndpoint!, credential);
    }

    public async Task<string> ExtractTextAsync(ReadOnlyMemory<byte> image, string contentType, string fileName, CancellationToken cancellationToken)
    {
        var agentId = await GetAgentIdAsync(cancellationToken);

        string? fileId = null;
        string? threadId = null;
        try
        {
            using (var stream = new MemoryStream(image.ToArray(), writable: false))
            {
                PersistentAgentFileInfo file = await _client.Files.UploadFileAsync(stream, PersistentAgentFilePurpose.Agents, UploadFileName(contentType), cancellationToken);
                fileId = file.Id;
            }

            PersistentAgentThread thread = await _client.Threads.CreateThreadAsync(cancellationToken: cancellationToken);
            threadId = thread.Id;

            await _client.Messages.CreateMessageAsync(
                threadId,
                MessageRole.User,
                [
                    new MessageInputTextBlock("Transcribe all text in this image."),
                    new MessageInputImageFileBlock(new MessageImageFileParam(fileId) { Detail = ImageDetailLevel.High }),
                ],
                cancellationToken: cancellationToken);

            ThreadRun run = await _client.Runs.CreateRunAsync(threadId, agentId, cancellationToken: cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RunTimeout);
            while (run.Status == RunStatus.Queued || run.Status == RunStatus.InProgress)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
                run = await _client.Runs.GetRunAsync(threadId, run.Id, timeout.Token);
            }

            if (run.Status != RunStatus.Completed)
            {
                throw new InvalidOperationException($"OCR agent run ended with status '{run.Status}': {run.LastError?.Message}");
            }

            var parts = new List<string>();
            await foreach (PersistentThreadMessage message in _client.Messages.GetMessagesAsync(threadId, order: ListSortOrder.Ascending, cancellationToken: cancellationToken))
            {
                if (message.Role != MessageRole.Agent)
                {
                    continue;
                }

                parts.AddRange(message.ContentItems.OfType<MessageTextContent>().Select(c => c.Text));
            }

            return string.Join(Environment.NewLine, parts).Trim();
        }
        finally
        {
            await CleanupAsync(threadId, fileId);
        }
    }

    private async Task<string> GetAgentIdAsync(CancellationToken cancellationToken)
    {
        if (_agentId is not null)
        {
            return _agentId;
        }

        await _agentLock.WaitAsync(cancellationToken);
        try
        {
            if (_agentId is not null)
            {
                return _agentId;
            }

            await foreach (PersistentAgent existing in _client.Administration.GetAgentsAsync(cancellationToken: cancellationToken))
            {
                if (string.Equals(existing.Name, _options.AgentName, StringComparison.Ordinal))
                {
                    _logger.LogInformation("Using existing OCR agent {AgentId}", existing.Id);
                    return _agentId = existing.Id;
                }
            }

            PersistentAgent agent = await _client.Administration.CreateAgentAsync(
                model: _options.ModelDeploymentName,
                name: _options.AgentName,
                description: "Transcribes text from photos and scans of notes for Notepal.",
                instructions: OcrOptions.Instructions,
                temperature: 0,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Created OCR agent {AgentId}", agent.Id);
            return _agentId = agent.Id;
        }
        finally
        {
            _agentLock.Release();
        }
    }

    private async Task CleanupAsync(string? threadId, string? fileId)
    {
        try
        {
            if (threadId is not null)
            {
                await _client.Threads.DeleteThreadAsync(threadId);
            }

            if (fileId is not null)
            {
                await _client.Files.DeleteFileAsync(fileId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up OCR thread {ThreadId} / file {FileId}", threadId, fileId);
        }
    }

    private static string UploadFileName(string contentType)
    {
        var extension = contentType switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".jpg",
        };

        return $"notepal-{Guid.NewGuid():N}{extension}";
    }
}
