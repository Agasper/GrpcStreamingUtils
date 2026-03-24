using System.Collections.Concurrent;
using Niarru.GrpcStreamingUtils.Connection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Niarru.GrpcStreamingUtils.KeepAlive;

public sealed class StreamKeepAliveMonitor : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, StreamConnectionBase> _streams = new();
    private readonly ILogger<StreamKeepAliveMonitor> _logger;
    private readonly PeriodicTimer _timer;

    public StreamKeepAliveMonitor(ILogger<StreamKeepAliveMonitor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    }

    public void Register(StreamConnectionBase connection)
    {
        if (connection == null)
            throw new ArgumentNullException(nameof(connection));

        if (_streams.TryAdd(connection.ConnectionId, connection))
        {
            using (_logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = connection.ConnectionId.ToString() }))
            {
                _logger.LogDebug("Registered stream for connection {connectionId} (total: {count})", connection.ConnectionId, _streams.Count);
            }
        }
        else
        {
            using (_logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = connection.ConnectionId.ToString() }))
            {
                _logger.LogWarning("Stream for connection {connectionId} is already registered", connection.ConnectionId);
            }
        }
    }

    public void Unregister(StreamConnectionBase connection)
    {
        if (connection == null)
            throw new ArgumentNullException(nameof(connection));

        if (_streams.TryRemove(connection.ConnectionId, out _))
        {
            using (_logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = connection.ConnectionId.ToString() }))
            {
                _logger.LogDebug("Unregistered stream for connection {connectionId} (remaining: {count})", connection.ConnectionId, _streams.Count);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("StreamKeepAliveMonitor started");

        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (var (connectionId, connection) in _streams)
                {
                    if (connection.IsClosed)
                    {
                        if (_streams.TryRemove(connectionId, out _))
                        {
                            using (_logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = connectionId.ToString() }))
                            {
                                _logger.LogDebug("Auto-removed closed connection {connectionId} (remaining: {count})", connectionId, _streams.Count);
                            }
                        }

                        continue;
                    }

                    try
                    {
                        if (connection.KeepAliveManager != null)
                        {
                            await connection.KeepAliveManager.Update(stoppingToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        using (_logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = connectionId.ToString() }))
                        {
                            _logger.LogError(ex, "Error updating stream for connection {connectionId}", connectionId);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in StreamKeepAliveMonitor loop");
        }

        _logger.LogDebug("StreamKeepAliveMonitor stopped");
    }

    public override void Dispose()
    {
        _timer.Dispose();
        _streams.Clear();
        base.Dispose();
    }
}
