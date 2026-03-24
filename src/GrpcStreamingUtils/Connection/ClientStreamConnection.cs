using Niarru.GrpcStreamingUtils.Configuration;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Niarru.GrpcStreamingUtils.Connection;

public abstract class ClientStreamConnection<TIncoming, TOutgoing> : StreamConnectionBase<TIncoming, TOutgoing>
    where TIncoming : class
    where TOutgoing : class
{
    private readonly AsyncDuplexStreamingCall<TOutgoing, TIncoming> _stream;

    protected ClientStreamConnection(
        AsyncDuplexStreamingCall<TOutgoing, TIncoming> stream,
        StreamingOptions options,
        TimeProvider timeProvider,
        ILogger logger)
        : base(options, timeProvider, new CancellationTokenSource(), logger)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    private protected override IAsyncStreamReader<TIncoming> GetReader() => _stream.ResponseStream;

    private protected override Task WriteMessageAsync(TOutgoing message) => _stream.RequestStream.WriteAsync(message);

    private protected override async Task OnCloseAsync()
    {
        try
        {
            await _stream.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            using (_logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = ConnectionId.ToString() }))
            {
                _logger.LogWarning(ex, "Error completing request stream");
            }
        }
    }

    protected override Task OnDisposeAsync()
    {
        _stream.Dispose();
        return Task.CompletedTask;
    }
}
