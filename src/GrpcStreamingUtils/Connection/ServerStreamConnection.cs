using Grpc.Core;
using Microsoft.Extensions.Logging;
using Niarru.GrpcStreamingUtils.Logging;

namespace Niarru.GrpcStreamingUtils.Connection;

public abstract class ServerStreamConnection<TIncoming, TOutgoing> : StreamConnectionBase<TIncoming, TOutgoing>
    where TIncoming : class
    where TOutgoing : class
{
    private readonly IAsyncStreamReader<TIncoming> _requestStream;
    private readonly IServerStreamWriter<TOutgoing> _responseStream;

    protected ServerStreamConnection(
        IAsyncStreamReader<TIncoming> requestStream,
        IServerStreamWriter<TOutgoing> responseStream,
        TimeProvider timeProvider,
        CancellationToken grpcCallCancellation,
        ILogger logger,
        TimeSpan? pingInterval = null,
        TimeSpan? idleTimeout = null,
        GrpcLoggingConfiguration? grpcLoggingConfig = null,
        GrpcLogger? grpcLogger = null)
        : base(timeProvider, grpcCallCancellation, logger, pingInterval, idleTimeout, grpcLoggingConfig, grpcLogger)
    {
        _requestStream = requestStream ?? throw new ArgumentNullException(nameof(requestStream));
        _responseStream = responseStream ?? throw new ArgumentNullException(nameof(responseStream));
    }

    private protected override IAsyncStreamReader<TIncoming> GetReader() => _requestStream;

    private protected override Task WriteMessageAsync(TOutgoing message) => _responseStream.WriteAsync(message);

    private protected override Task OnCloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task OnDisposeAsync() => Task.CompletedTask;
}
