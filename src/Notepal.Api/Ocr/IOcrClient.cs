namespace Notepal.Api.Ocr;

/// <summary>Extracts text from an image using an AI vision model.</summary>
public interface IOcrClient
{
    Task<string> ExtractTextAsync(ReadOnlyMemory<byte> image, string contentType, string fileName, CancellationToken cancellationToken);
}

/// <summary>Used when no Foundry project is configured; pages fail with an actionable message instead of crashing the app.</summary>
public sealed class UnconfiguredOcrClient : IOcrClient
{
    public Task<string> ExtractTextAsync(ReadOnlyMemory<byte> image, string contentType, string fileName, CancellationToken cancellationToken) =>
        throw new OcrUnavailableException("OCR is not configured. Set 'Ocr:ProjectEndpoint' to an Azure AI Foundry project endpoint.");
}

public sealed class OcrUnavailableException(string message) : Exception(message);
