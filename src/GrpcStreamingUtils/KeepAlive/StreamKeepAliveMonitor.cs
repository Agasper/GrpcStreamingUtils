using System.Collections.Concurrent;
using Niarru.GrpcStreamingUtils.Connection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Niarru.GrpcStreamingUtils.KeepAlive;

public sealed class StreamKeepAliveMonitor : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, StreamConnectionBase> _streams = new();
    private readonly ILogger<StreamKeepAliveMonitor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _tickInterval;
    private PeriodicTimer? _timer;

    public StreamKeepAliveMonitor(
        ILogger<StreamKeepAliveMonitor> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? tickInterval = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tickInterval = tickInterval ?? TimeSpan.FromSeconds(1);
    }

    public void Register(StreamConnectionBase connection)
    {
        if (connection == null)
            throw new ArgumentNullException(nameof(connection));

        if (_streams.TryAdd(connection.ConnectionId, connection))
        {
            using (_logger.BeginConnectionScope(connection.ConnectionId))
            {
                _logger.LogDebug("Registered stream for connection {connectionId} (total: {count})", connection.ConnectionId, _streams.Count);
            }
        }
        else
        {
            using (_logger.BeginConnectionScope(connection.ConnectionId))
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
            using (_logger.BeginConnectionScope(connection.ConnectionId))
            {
                _logger.LogDebug("Unregistered stream for connection {connectionId} (remaining: {count})", connection.ConnectionId, _streams.Count);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("StreamKeepAliveMonitor started");

        _timer = new PeriodicTimer(_tickInterval, _timeProvider);

        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var updateTasks = new List<Task>();

                foreach (var (connectionId, connection) in _streams)
                {
                    if (connection.IsClosed)
                    {
                        if (_streams.TryRemove(connectionId, out _))
                        {
                            using (_logger.BeginConnectionScope(connectionId))
                            {
                                _logger.LogDebug("Auto-removed closed connection {connectionId} (remaining: {count})", connectionId, _streams.Count);
                            }
                        }

                        continue;
                    }

                    if (connection.KeepAliveManager != null)
                    {
                        updateTasks.Add(UpdateConnectionAsync(connectionId, connection, stoppingToken));
                    }
                }

                if (updateTasks.Count > 0)
                {
                    await Task.WhenAll(updateTasks).ConfigureAwait(false);
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

    private async Task UpdateConnectionAsync(Guid connectionId, StreamConnectionBase connection, CancellationToken stoppingToken)
    {
        try
        {
            await connection.KeepAliveManager!.Update(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            using (_logger.BeginConnectionScope(connectionId))
            {
                _logger.LogError(ex, "Error updating stream for connection {connectionId}", connectionId);
            }
        }
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        _streams.Clear();
        base.Dispose();
    }
}
