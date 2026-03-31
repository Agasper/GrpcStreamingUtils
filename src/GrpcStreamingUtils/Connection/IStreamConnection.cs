namespace Niarru.GrpcStreamingUtils.Connection;

public interface IStreamConnection<TIncoming, TOutgoing> : IAsyncDisposable
    where TIncoming : class
    where TOutgoing : class
{
    Guid ConnectionId { get; }

    CancellationToken ConnectionClosed { get; }

    Task RunAsync(CancellationToken cancellationToken);

    Task SendAsync(TOutgoing message, CancellationToken cancellationToken);

    Task<bool> TrySendAsync(TOutgoing message, CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}
