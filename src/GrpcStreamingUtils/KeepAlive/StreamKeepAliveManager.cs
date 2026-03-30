using Microsoft.Extensions.Logging;

namespace Niarru.GrpcStreamingUtils.KeepAlive;

internal sealed class StreamKeepAliveManager : IDisposable
{
    private readonly Guid _connectionId;
    private readonly Func<CancellationToken, Task>? _sendPingFunc;
    private readonly Action _onTimeoutAction;
    private readonly TimeSpan? _pingInterval;
    private readonly TimeSpan? _idleTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private DateTimeOffset _lastMessageReceivedAt;
    private DateTimeOffset _lastPingSentAt;
    private volatile bool _disposed;

    public StreamKeepAliveManager(
        Guid connectionId,
        Func<CancellationToken, Task>? sendPingFunc,
        Action onTimeoutAction,
        TimeSpan? pingInterval,
        TimeSpan? idleTimeout,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _connectionId = connectionId;
        _sendPingFunc = sendPingFunc;
        _onTimeoutAction = onTimeoutAction ?? throw new ArgumentNullException(nameof(onTimeoutAction));
        _pingInterval = pingInterval;
        _idleTimeout = idleTimeout;
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
        int idleSeconds = 0;
        int timeoutSeconds = 0;
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            if (_disposed) return;

            if (_pingInterval.HasValue && _sendPingFunc != null)
            {
                var timeSinceLastPing = now - _lastPingSentAt;
                if (timeSinceLastPing >= _pingInterval.Value)
                {
                    shouldSendPing = true;
                }
            }

            if (_idleTimeout.HasValue)
            {
                var idleTime = now - _lastMessageReceivedAt;

                if (idleTime > _idleTimeout.Value)
                {
                    shouldTimeout = true;
                    idleSeconds = (int)idleTime.TotalSeconds;
                    timeoutSeconds = (int)_idleTimeout.Value.TotalSeconds;
                }
            }
        }

        if (shouldSendPing && !shouldTimeout)
        {
            try
            {
                await _sendPingFunc!(cancellationToken).ConfigureAwait(false);

                lock (_lock)
                {
                    _lastPingSentAt = _timeProvider.GetUtcNow();
                }
            }
            catch (Exception ex)
            {
                using (_logger.BeginConnectionScope(_connectionId))
                {
                    _logger.LogWarning(ex, "Failed to send Ping, closing connection");
                }

                _onTimeoutAction();
            }
        }

        if (shouldTimeout)
        {
            using (_logger.BeginConnectionScope(_connectionId))
            {
                _logger.LogWarning(
                    "Stream connection timed out: no messages received for {idleSeconds}s (timeout: {timeoutSeconds}s)",
                    idleSeconds,
                    timeoutSeconds);
            }

            _onTimeoutAction();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
    }
}
