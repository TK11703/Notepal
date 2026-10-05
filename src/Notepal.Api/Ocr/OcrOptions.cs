namespace Notepal.Api.Ocr;

public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    /// <summary>Foundry (Azure OpenAI) account endpoint, e.g. <c>https://my-foundry.openai.azure.com/</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Name of a vision-capable model deployment on that account (e.g. <c>gpt-4.1-mini</c>).</summary>
    public string ModelDeploymentName { get; set; } = "gpt-4.1-mini";

    /// <summary>Client id of a user-assigned managed identity. When empty, the Azure CLI login is used in Development and the default Azure credential chain otherwise.</summary>
    public string? ManagedIdentityClientId { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);

    public const string Instructions =
        """
        You are an OCR (optical character recognition) engine for a note taking application.
        You receive photos or scans of handwritten or printed notes.
        Transcribe ALL of the text exactly as written, preserving the reading order, line breaks, lists, headings and paragraphs.
        Use Markdown only for structure that is clearly present (bullet lists, numbered lists, headings, tables).
        Do not summarize, translate, correct spelling or add commentary. If a word is illegible write [illegible].
        If the image contains no text, reply with an empty response.
        Reply with the transcription only.
        """;
}
