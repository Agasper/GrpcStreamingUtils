using Niarru.GrpcStreamingUtils.Connection;
using Niarru.GrpcStreamingUtils.Exceptions;
using Niarru.GrpcStreamingUtils.KeepAlive;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Niarru.GrpcStreamingUtils.Client;

public abstract class ReconnectingStreamClient<TConnection, TIncoming, TOutgoing> : BackgroundService, IAsyncDisposable
    where TConnection : ClientStreamConnection<TIncoming, TOutgoing>
    where TIncoming : class
    where TOutgoing : class
{
    private readonly StreamKeepAliveMonitor _keepAliveMonitor;
    private readonly TimeSpan _initialReconnectInterval;
    private readonly TimeSpan _maxReconnectInterval;
    private readonly double _reconnectBackoffMultiplier;
    private double _currentBackoffMs;
    private TConnection? _connection;

    protected TimeProvider TimeProvider { get; }
    protected ILogger Logger { get; }

    protected TConnection? Connection => _connection;

    protected bool IsConnected => _connection != null;

    protected ReconnectingStreamClient(
        StreamKeepAliveMonitor keepAliveMonitor,
        TimeProvider timeProvider,
        ILogger logger,
        TimeSpan? initialReconnectInterval = null,
        TimeSpan? maxReconnectInterval = null,
        double reconnectBackoffMultiplier = 2)
    {
        _keepAliveMonitor = keepAliveMonitor ?? throw new ArgumentNullException(nameof(keepAliveMonitor));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _initialReconnectInterval = initialReconnectInterval ?? TimeSpan.FromSeconds(1);
        _maxReconnectInterval = maxReconnectInterval ?? TimeSpan.FromSeconds(60);
        _reconnectBackoffMultiplier = reconnectBackoffMultiplier;
    }

    protected abstract string ClientName { get; }

    protected abstract AsyncDuplexStreamingCall<TOutgoing, TIncoming> CreateStream(CancellationToken cancellationToken);

    protected abstract TConnection CreateConnection(AsyncDuplexStreamingCall<TOutgoing, TIncoming> stream);

    protected virtual IDisposable? CreateLoggingScope() => null;

    protected virtual Task SendHandshakeAsync(
        AsyncDuplexStreamingCall<TOutgoing, TIncoming> stream,
        CancellationToken cancellationToken) => Task.CompletedTask;

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (CreateLoggingScope())
        {
            Logger.LogInformation("{ClientName} starting", ClientName);

            while (!stoppingToken.IsCancellationRequested)
            {
                TConnection? connection = null;
                try
                {
                    var stream = CreateStream(stoppingToken);
                    await SendHandshakeAsync(stream, stoppingToken).ConfigureAwait(false);

                    connection = CreateConnection(stream);
                    _connection = connection;
                    _keepAliveMonitor.Register(connection);
                    _currentBackoffMs = 0;

                    await connection.RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
                {
                    Logger.LogInformation("{ClientName} cancelled", ClientName);
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Logger.LogInformation("{ClientName} shutting down gracefully", ClientName);
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Stream error, reconnecting...");
                }
                finally
                {
                    if (connection != null)
                    {
                        _connection = null;
                        _keepAliveMonitor.Unregister(connection);
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (stoppingToken.IsCancellationRequested)
                    break;

                await ReconnectWithBackoffAsync(stoppingToken).ConfigureAwait(false);
            }

            Logger.LogInformation("{ClientName} stopped", ClientName);
        }
    }

    protected async Task SendAsync(TOutgoing message, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection == null)
        {
            throw new StreamNotEstablishedException();
        }

        await connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconnectWithBackoffAsync(CancellationToken cancellationToken)
    {
        if (_currentBackoffMs == 0)
        {
            _currentBackoffMs = _initialReconnectInterval.TotalMilliseconds;
        }
        else
        {
            _currentBackoffMs = Math.Min(
                _currentBackoffMs * _reconnectBackoffMultiplier,
                _maxReconnectInterval.TotalMilliseconds);
        }

        var delay = TimeSpan.FromMilliseconds(_currentBackoffMs);
        Logger.LogInformation("Reconnecting in {Delay:F1}s...", delay.TotalSeconds);

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            _keepAliveMonitor.Unregister(_connection);
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        base.Dispose();
    }
}
