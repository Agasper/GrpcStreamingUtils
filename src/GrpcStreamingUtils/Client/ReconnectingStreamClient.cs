using Niarru.GrpcStreamingUtils.Configuration;
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
    private readonly StreamingOptions _streamingOptions;
    private int _currentBackoffSeconds;
    private TConnection? _connection;

    protected TimeProvider TimeProvider { get; }
    protected ILogger Logger { get; }

    protected TConnection? Connection => _connection;

    protected bool IsConnected => _connection != null;

    protected ReconnectingStreamClient(
        StreamKeepAliveMonitor keepAliveMonitor,
        StreamingOptions streamingOptions,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _keepAliveMonitor = keepAliveMonitor ?? throw new ArgumentNullException(nameof(keepAliveMonitor));
        _streamingOptions = streamingOptions ?? throw new ArgumentNullException(nameof(streamingOptions));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                    _currentBackoffSeconds = 0;

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
        if (_currentBackoffSeconds == 0)
        {
            _currentBackoffSeconds = _streamingOptions.InitialReconnectIntervalSeconds;
        }
        else
        {
            _currentBackoffSeconds = Math.Min(
                _currentBackoffSeconds * 2,
                _streamingOptions.MaxReconnectIntervalSeconds);
        }

        Logger.LogInformation("Reconnecting in {Delay}s...", _currentBackoffSeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_currentBackoffSeconds), cancellationToken).ConfigureAwait(false);
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
