using System.Threading.Channels;

namespace Notepal.Api.Processing;

/// <summary>In-process queue of page ids waiting for text extraction.</summary>
public sealed class ProcessingQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false });

    public void Enqueue(Guid pageId) => _channel.Writer.TryWrite(pageId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}
