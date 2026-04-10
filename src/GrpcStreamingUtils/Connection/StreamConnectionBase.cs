using Niarru.GrpcStreamingUtils.KeepAlive;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Niarru.GrpcStreamingUtils.Connection;

public abstract class StreamConnectionBase
{
    public Guid ConnectionId { get; }
    internal StreamKeepAliveManager? KeepAliveManager { get; private protected set; }

    private int _isClosed;
    internal bool IsClosed => Volatile.Read(ref _isClosed) != 0;
    private protected void MarkClosed() => Volatile.Write(ref _isClosed, 1);

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
    private Task? _closeTask;
    private volatile bool _timedOut;

    protected readonly ILogger _logger;
    private readonly CancellationTokenSource _connectionCts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

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

        if (pingInterval.HasValue || idleTimeout.HasValue)
        {
            KeepAliveManager = new StreamKeepAliveManager(
                ConnectionId,
                pingInterval.HasValue ? async ct => await SendAsync(CreatePingMessage(), ct).ConfigureAwait(false) : null,
                () =>
                {
                    _timedOut = true;
                    _connectionCts.Cancel();
                },
                pingInterval,
                idleTimeout,
                timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)),
                logger);
        }
    }

    private protected abstract IAsyncStreamReader<TIncoming> GetReader();
    private protected abstract Task WriteMessageAsync(TOutgoing message);
    private protected abstract Task OnCloseAsync(CancellationToken cancellationToken);
    protected abstract Task OnDisposeAsync();

    protected abstract TOutgoing CreatePingMessage();

    protected void ResetIdleTimer()
    {
        KeepAliveManager?.UpdateLastMessageTime();
    }

    protected virtual Task OnMessageReceivedAsync(TIncoming message, CancellationToken cancellationToken)
    {
        ResetIdleTimer();
        return Task.CompletedTask;
    }

    protected virtual string? FormatMessage<T>(T message) => message?.ToString();

    protected virtual void OnConnectionClosed(StreamConnectionClosedArgs args) { }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connectionCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            await foreach (var message in GetReader().ReadAllAsync(linkedToken).ConfigureAwait(false))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Stream received: {Message}", FormatMessage(message));

                await OnMessageReceivedAsync(message, linkedToken).ConfigureAwait(false);
            }

            await CloseCoreAsync(CloseReason.Normal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CloseCoreAsync(_timedOut ? CloseReason.Timeout : CloseReason.Normal).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            await CloseCoreAsync(_timedOut ? CloseReason.Timeout : CloseReason.Normal).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CloseCoreAsync(CloseReason.Error, ex).ConfigureAwait(false);
        }
    }

    public virtual async Task SendAsync(TOutgoing message, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || _connectionCts.IsCancellationRequested)
            throw new OperationCanceledException("Connection is closed.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connectionCts.IsCancellationRequested)
                throw new OperationCanceledException("Connection is closed.");

            await WriteMessageAsync(message).ConfigureAwait(false);

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Stream sent: {Message}", FormatMessage(message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CloseCoreAsync(CloseReason.Error, ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> TrySendAsync(TOutgoing message, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken)
        => CloseCoreAsync(CloseReason.Normal, cancellationToken: cancellationToken);

    private Task CloseCoreAsync(CloseReason reason, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        lock (_closeLock)
        {
            if (_closeTask != null) return _closeTask;
            _closeTask = ExecuteCloseAsync(reason, exception, cancellationToken);
            return _closeTask;
        }
    }

    private async Task ExecuteCloseAsync(CloseReason reason, Exception? exception, CancellationToken cancellationToken)
    {
        MarkClosed();

        await OnCloseAsync(cancellationToken).ConfigureAwait(false);

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
            using (_logger.BeginConnectionScope(ConnectionId))
            {
                _logger.LogWarning(ex, "Error in OnConnectionClosed handler");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        GC.SuppressFinalize(this);

        await CloseCoreAsync(CloseReason.Normal).ConfigureAwait(false);

        KeepAliveManager?.Dispose();

        await OnDisposeAsync().ConfigureAwait(false);

        try
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            _writeLock.Release();
        }
        catch (ObjectDisposedException)
        {
        }

        _connectionCts.Dispose();
        _writeLock.Dispose();
    }
}
