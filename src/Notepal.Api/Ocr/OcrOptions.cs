namespace Notepal.Api.Ocr;

public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    /// <summary>Azure AI Foundry project endpoint, e.g. <c>https://my-foundry.services.ai.azure.com/api/projects/notepal</c>.</summary>
    public string? ProjectEndpoint { get; set; }

    /// <summary>Name of the model deployment used when the agent has to be created (e.g. <c>gpt-4.1-mini</c>).</summary>
    public string ModelDeploymentName { get; set; } = "gpt-4.1-mini";

    /// <summary>Optional id of an existing agent. When empty, an agent named <see cref="AgentName"/> is reused or created.</summary>
    public string? AgentId { get; set; }

    public string AgentName { get; set; } = "notepal-ocr";

    /// <summary>Client id of a user-assigned managed identity. Leave empty to use the default Azure credential chain.</summary>
    public string? ManagedIdentityClientId { get; set; }

    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectEndpoint);

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
