using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Notepal.Api.Ocr;

/// <summary>Performs OCR by sending the image inline to a vision-capable chat model deployment on the Foundry account.</summary>
public sealed class AzureOpenAiOcrClient : IOcrClient
{
    private readonly ChatClient _chat;

    internal AzureOpenAiOcrClient(ChatClient chat) => _chat = chat;

    public AzureOpenAiOcrClient(IOptions<OcrOptions> options, IHostEnvironment environment)
    {
        var settings = options.Value;
        TokenCredential credential = !string.IsNullOrWhiteSpace(settings.ManagedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId))
            // The default chain tries IDE sign-ins before the Azure CLI, which can pick an account without access.
            : environment.IsDevelopment() ? new AzureCliCredential()
            : new DefaultAzureCredential();

        var client = new AzureOpenAIClient(new Uri(settings.Endpoint!), credential, new AzureOpenAIClientOptions { NetworkTimeout = settings.Timeout });
        _chat = client.GetChatClient(settings.ModelDeploymentName);
    }

    public async Task<string> ExtractTextAsync(ReadOnlyMemory<byte> image, string contentType, string fileName, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new SystemChatMessage(OcrOptions.Instructions),
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart("Transcribe all text in this image."),
                ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(image), contentType, ChatImageDetailLevel.High)),
        ];

        // The SDK writes request messages into options, so concurrent pages must not share them.
        var completionOptions = new ChatCompletionOptions { Temperature = 0 };
        ClientResult<ChatCompletion> result = await _chat.CompleteChatAsync(messages, completionOptions, cancellationToken);
        var completion = result.Value;
        if (completion.FinishReason == ChatFinishReason.ContentFilter)
        {
            throw new InvalidOperationException("The OCR model's content filter blocked this image.");
        }

        return string.Concat(completion.Content.Select(part => part.Text)).Trim();
    }
}
