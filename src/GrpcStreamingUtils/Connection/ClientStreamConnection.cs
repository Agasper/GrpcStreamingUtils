using Grpc.Core;
using Microsoft.Extensions.Logging;
using Niarru.GrpcStreamingUtils.Logging;

namespace Niarru.GrpcStreamingUtils.Connection;

public abstract class ClientStreamConnection<TIncoming, TOutgoing> : StreamConnectionBase<TIncoming, TOutgoing>
    where TIncoming : class
    where TOutgoing : class
{
    private readonly AsyncDuplexStreamingCall<TOutgoing, TIncoming> _stream;

    protected ClientStreamConnection(
        AsyncDuplexStreamingCall<TOutgoing, TIncoming> stream,
        TimeProvider timeProvider,
        ILogger logger,
        TimeSpan? pingInterval = null,
        TimeSpan? idleTimeout = null,
        GrpcLoggingConfiguration? grpcLoggingConfig = null)
        : base(timeProvider, CancellationToken.None, logger, pingInterval, idleTimeout, grpcLoggingConfig)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    private protected override IAsyncStreamReader<TIncoming> GetReader() => _stream.ResponseStream;

    private protected override Task WriteMessageAsync(TOutgoing message) => _stream.RequestStream.WriteAsync(message);

    private protected override async Task OnCloseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stream.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            using (_logger.BeginConnectionScope(ConnectionId))
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
