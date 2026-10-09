using System.ClientModel;
using System.ClientModel.Primitives;
using Notepal.Api.Ocr;
using OpenAI.Chat;

namespace Notepal.Api.Tests;

public sealed class OcrRequestIsolationTests
{
    [Fact]
    public async Task Concurrent_pages_use_independent_options_and_image_payloads()
    {
        var chat = new DelayedChatClient();
        var ocr = new AzureOpenAiOcrClient(chat);
        var images = Enumerable.Range(1, 6).Select(i => new byte[] { (byte)i }).ToArray();
        var tasks = images.Select((image, i) =>
            ocr.ExtractTextAsync(image, "image/png", $"page-{i + 1}.png", CancellationToken.None)).ToArray();

        Assert.Equal(6, chat.Requests.Count);
        for (var i = 0; i < chat.Requests.Count; i++)
        {
            var request = chat.Requests[i];
            Assert.Equal(0f, request.Options.Temperature);
            Assert.Single(request.Messages.OfType<SystemChatMessage>());
            var user = Assert.Single(request.Messages.OfType<UserChatMessage>());
            var image = Assert.Single(user.Content, part => part.Kind == ChatMessageContentPartKind.Image);
            Assert.Equal(images[i], image.ImageBytes.ToArray());
            Assert.Equal("image/png", image.ImageBytesMediaType);
            foreach (var other in chat.Requests.Take(i))
            {
                Assert.NotSame(other.Options, request.Options);
            }
        }

        // Complete out of order to ensure each caller receives its own response.
        for (var i = chat.Requests.Count - 1; i >= 0; i--)
        {
            chat.Requests[i].Completion.SetResult(OpenAIChatModelFactory.ChatCompletion(
                content: [ChatMessageContentPart.CreateTextPart($"Text for page {i + 1}")]));
        }

        var results = await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"Text for page {i}"), results);
    }

    private sealed class DelayedChatClient : ChatClient
    {
        public List<Request> Requests { get; } = [];

        public override async Task<ClientResult<ChatCompletion>> CompleteChatAsync(
            IEnumerable<ChatMessage> messages, ChatCompletionOptions? options = null, CancellationToken cancellationToken = default)
        {
            var request = new Request(messages.ToArray(), options!);
            Requests.Add(request);
            var completion = await request.Completion.Task.WaitAsync(cancellationToken);
            return ClientResult.FromValue(completion, new SuccessfulResponse());
        }
    }

    private sealed record Request(ChatMessage[] Messages, ChatCompletionOptions Options)
    {
        public TaskCompletionSource<ChatCompletion> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SuccessfulResponse : PipelineResponse
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        protected override PipelineResponseHeaders HeadersCore => throw new NotSupportedException();
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content { get; } = BinaryData.FromString("");
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Content);
        public override void Dispose() { }
    }
}
