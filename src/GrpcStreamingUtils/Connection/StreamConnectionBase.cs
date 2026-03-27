using Niarru.GrpcStreamingUtils.KeepAlive;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Niarru.GrpcStreamingUtils.Connection;

public abstract class StreamConnectionBase
{
    public Guid ConnectionId { get; }
    internal StreamKeepAliveManager KeepAliveManager { get; private protected set; } = null!;
    internal bool IsClosed { get; private protected set; }

    protected StreamConnectionBase(Guid connectionId)
    {
        ConnectionId = connectionId;
    }
}

public abstract class StreamConnectionBase<TIncoming, TOutgoing> : StreamConnectionBase, IStreamConnection<TIncoming, TOutgoing>
    where TIncoming : class
    where TOutgoing : class
{
    private readonly object _closeLock = new();
    private bool _closed;

    protected readonly ILogger _logger;
    private readonly CancellationTokenSource _connectionCts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public CancellationToken ConnectionClosed => _connectionCts.Token;

    protected StreamConnectionBase(
        TimeProvider timeProvider,
        CancellationToken externalCancellation,
        ILogger logger,
        TimeSpan? pingInterval = null,
        TimeSpan? idleTimeout = null)
        : base(Guid.NewGuid())
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);

        KeepAliveManager = new StreamKeepAliveManager(
            ConnectionId,
            pingInterval.HasValue ? async ct => await SendAsync(CreatePingMessage(), ct).ConfigureAwait(false) : null,
            () => _connectionCts.Cancel(),
            pingInterval,
            idleTimeout,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)),
            logger);
    }

    private protected abstract IAsyncStreamReader<TIncoming> GetReader();
    private protected abstract Task WriteMessageAsync(TOutgoing message);
    private protected abstract Task OnCloseAsync();
    protected abstract Task OnDisposeAsync();

    protected abstract TOutgoing CreatePingMessage();

    protected virtual Task OnMessageReceivedAsync(TIncoming message, CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected virtual void OnConnectionClosed(StreamConnectionClosedArgs args) { }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connectionCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            await foreach (var message in GetReader().ReadAllAsync(linkedToken).ConfigureAwait(false))
            {
                KeepAliveManager.UpdateLastMessageTime();
                await OnMessageReceivedAsync(message, linkedToken).ConfigureAwait(false);
            }

            await CloseAsync(CloseReason.Normal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CloseAsync(CloseReason.Normal).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            await CloseAsync(CloseReason.Normal).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CloseAsync(CloseReason.Error, ex).ConfigureAwait(false);
        }
    }

    public async Task SendAsync(TOutgoing message, CancellationToken cancellationToken)
    {
        if (_disposed || _connectionCts.IsCancellationRequested)
            throw new OperationCanceledException("Connection is closed.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connectionCts.IsCancellationRequested)
                throw new OperationCanceledException("Connection is closed.");

            await WriteMessageAsync(message).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken)
        => CloseAsync(CloseReason.Normal);

    private async Task CloseAsync(CloseReason reason, Exception? exception = null)
    {
        lock (_closeLock)
        {
            if (_closed) return;
            _closed = true;
        }

        IsClosed = true;

        await OnCloseAsync().ConfigureAwait(false);

        if (!_connectionCts.IsCancellationRequested)
        {
            _connectionCts.Cancel();
        }

        try
        {
            OnConnectionClosed(new StreamConnectionClosedArgs(reason, exception));
        }
        catch (Exception ex)
        {
            using (_logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = ConnectionId.ToString() }))
            {
                _logger.LogWarning(ex, "Error in OnConnectionClosed handler");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await CloseAsync(CancellationToken.None).ConfigureAwait(false);

        KeepAliveManager.Dispose();
        _connectionCts.Dispose();

        await OnDisposeAsync().ConfigureAwait(false);

        try
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            _writeLock.Release();
        }
        catch (ObjectDisposedException)
        {
        }

        _writeLock.Dispose();
    }
}
