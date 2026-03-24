using Niarru.GrpcStreamingUtils.Configuration;
using Microsoft.Extensions.Logging;

namespace Niarru.GrpcStreamingUtils.KeepAlive;

internal sealed class StreamKeepAliveManager : IDisposable
{
    private readonly Guid _connectionId;
    private readonly Func<CancellationToken, Task> _sendPingFunc;
    private readonly Action _onTimeoutAction;
    private readonly StreamingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly object _disposeLock = new();
    private DateTimeOffset _lastMessageReceivedAt;
    private DateTimeOffset _lastPingSentAt;
    private bool _disposed;

    public StreamKeepAliveManager(
        Guid connectionId,
        Func<CancellationToken, Task> sendPingFunc,
        Action onTimeoutAction,
        StreamingOptions options,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _connectionId = connectionId;
        _sendPingFunc = sendPingFunc ?? throw new ArgumentNullException(nameof(sendPingFunc));
        _onTimeoutAction = onTimeoutAction ?? throw new ArgumentNullException(nameof(onTimeoutAction));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var now = _timeProvider.GetUtcNow();
        _lastMessageReceivedAt = now;
        _lastPingSentAt = now;
    }

    public void UpdateLastMessageTime()
    {
        lock (_lock)
        {
            _lastMessageReceivedAt = _timeProvider.GetUtcNow();
        }
    }

    public async Task Update(CancellationToken cancellationToken)
    {
        if (_disposed) return;

        bool shouldSendPing = false;
        bool shouldTimeout = false;
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            if (_disposed) return;

            if (_options.PingIntervalSeconds > 0)
            {
                var timeSinceLastPing = now - _lastPingSentAt;
                if (timeSinceLastPing.TotalSeconds >= _options.PingIntervalSeconds)
                {
                    shouldSendPing = true;
                    _lastPingSentAt = now;
                }
            }

            if (_options.IdleTimeoutSeconds > 0)
            {
                var idleTime = now - _lastMessageReceivedAt;
                var timeout = TimeSpan.FromSeconds(_options.IdleTimeoutSeconds);

                if (idleTime > timeout)
                {
                    using (_logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = _connectionId.ToString() }))
                    {
                        _logger.LogWarning(
                            "Stream connection {connectionId} timed out: no messages received for {idleSeconds}s (timeout: {timeoutSeconds}s)",
                            _connectionId,
                            (int)idleTime.TotalSeconds,
                            _options.IdleTimeoutSeconds);
                    }

                    shouldTimeout = true;
                }
            }
        }

        if (shouldSendPing)
        {
            try
            {
                await _sendPingFunc(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                using (_logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = _connectionId.ToString() }))
                {
                    _logger.LogWarning(ex, "Failed to send Ping message for connection {connectionId}", _connectionId);
                }
            }
        }

        if (shouldTimeout)
        {
            _onTimeoutAction();
        }
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
