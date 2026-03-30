using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Niarru.GrpcStreamingUtils.KeepAlive;

namespace GrpcStreamingUtils.Tests.KeepAlive;

public class StreamKeepAliveManagerTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly Guid _connectionId = Guid.NewGuid();

    [Fact]
    public async Task Update_SendsPing_WhenIntervalElapsed()
    {
        int pingCount = 0;
        var manager = new StreamKeepAliveManager(
            _connectionId,
            sendPingFunc: _ => { pingCount++; return Task.CompletedTask; },
            onTimeoutAction: () => { },
            pingInterval: TimeSpan.FromSeconds(5),
            idleTimeout: null,
            _timeProvider,
            _logger);

        _timeProvider.Advance(TimeSpan.FromSeconds(6));
        await manager.Update(CancellationToken.None);

        Assert.Equal(1, pingCount);
    }

    [Fact]
    public async Task Update_DoesNotSendPing_BeforeIntervalElapsed()
    {
        int pingCount = 0;
        var manager = new StreamKeepAliveManager(
            _connectionId,
            sendPingFunc: _ => { pingCount++; return Task.CompletedTask; },
            onTimeoutAction: () => { },
            pingInterval: TimeSpan.FromSeconds(5),
            idleTimeout: null,
            _timeProvider,
            _logger);

        _timeProvider.Advance(TimeSpan.FromSeconds(3));
        await manager.Update(CancellationToken.None);

        Assert.Equal(0, pingCount);
    }

    [Fact]
    public async Task Update_CallsTimeout_WhenIdleTimeoutExceeded()
    {
        bool timedOut = false;
        var manager = new StreamKeepAliveManager(
            _connectionId,
            sendPingFunc: null,
            onTimeoutAction: () => timedOut = true,
            pingInterval: null,
            idleTimeout: TimeSpan.FromSeconds(10),
            _timeProvider,
            _logger);

        _timeProvider.Advance(TimeSpan.FromSeconds(11));
        await manager.Update(CancellationToken.None);

        Assert.True(timedOut);
    }

    [Fact]
    public async Task Update_DoesNotTimeout_WhenMessagesReceived()
    {
        bool timedOut = false;
        var manager = new StreamKeepAliveManager(
            _connectionId,
            sendPingFunc: null,
            onTimeoutAction: () => timedOut = true,
            pingInterval: null,
            idleTimeout: TimeSpan.FromSeconds(10),
            _timeProvider,
            _logger);

        _timeProvider.Advance(TimeSpan.FromSeconds(8));
        manager.UpdateLastMessageTime();

        _timeProvider.Advance(TimeSpan.FromSeconds(8));
        await manager.Update(CancellationToken.None);

        Assert.False(timedOut);
    }

    [Fact]
    public async Task Update_SkipsPing_WhenTimeoutAlsoTriggered()
    {
        int pingCount = 0;
        bool timedOut = false;
        var manager = new StreamKeepAliveManager(
            _connectionId,
            sendPingFunc: _ => { pingCount++; return Task.CompletedTask; },
            onTimeoutAction: () => timedOut = true,
            pingInterval: TimeSpan.FromSeconds(5),
            idleTimeout: TimeSpan.FromSeconds(10),
            _timeProvider,
            _logger);

        _timeProvider.Advance(TimeSpan.FromSeconds(11));
        await manager.Update(CancellationToken.None);

        Assert.True(timedOut);
        Assert.Equal(0, pingCount);
    }

    [Fact]
    public async Task Update_Noop_AfterDispose()
    {
        int pingCount = 0;
        bool timedOut = false;
        var manager = new StreamKeepAliveManager(
            _connectionId,
            sendPingFunc: _ => { pingCount++; return Task.CompletedTask; },
            onTimeoutAction: () => timedOut = true,
            pingInterval: TimeSpan.FromSeconds(5),
            idleTimeout: TimeSpan.FromSeconds(10),
            _timeProvider,
            _logger);

        manager.Dispose();

        _timeProvider.Advance(TimeSpan.FromSeconds(20));
        await manager.Update(CancellationToken.None);

        Assert.Equal(0, pingCount);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task Update_PingFailure_ClosesConnection()
    {
        bool closed = false;
        var manager = new StreamKeepAliveManager(
            _connectionId,
            sendPingFunc: _ => throw new InvalidOperationException("send failed"),
            onTimeoutAction: () => closed = true,
            pingInterval: TimeSpan.FromSeconds(5),
            idleTimeout: null,
            _timeProvider,
            _logger);

        _timeProvider.Advance(TimeSpan.FromSeconds(6));

        // Should not throw, but should close the connection
        await manager.Update(CancellationToken.None);

        Assert.True(closed);
    }

    [Fact]
    public async Task Update_UpdatesLastPingSentAt_OnlyAfterSuccessfulPing()
    {
        int pingCount = 0;
        var manager = new StreamKeepAliveManager(
            _connectionId,
            sendPingFunc: _ => { pingCount++; return Task.CompletedTask; },
            onTimeoutAction: () => { },
            pingInterval: TimeSpan.FromSeconds(5),
            idleTimeout: null,
            _timeProvider,
            _logger);

        // First ping
        _timeProvider.Advance(TimeSpan.FromSeconds(6));
        await manager.Update(CancellationToken.None);
        Assert.Equal(1, pingCount);

        // Not enough time since last ping
        _timeProvider.Advance(TimeSpan.FromSeconds(3));
        await manager.Update(CancellationToken.None);
        Assert.Equal(1, pingCount);

        // Enough time since last ping
        _timeProvider.Advance(TimeSpan.FromSeconds(3));
        await manager.Update(CancellationToken.None);
        Assert.Equal(2, pingCount);
    }
}
