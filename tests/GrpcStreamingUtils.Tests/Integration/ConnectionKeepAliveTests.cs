using Grpc.Core;
using GrpcStreamingUtils.Tests.TestDoubles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Niarru.GrpcStreamingUtils.Connection;
using Niarru.GrpcStreamingUtils.KeepAlive;

namespace GrpcStreamingUtils.Tests.Integration;

public class ConnectionKeepAliveTests
{
    private readonly ILogger<StreamKeepAliveMonitor> _monitorLogger =
        NullLoggerFactory.Instance.CreateLogger<StreamKeepAliveMonitor>();

    [Fact]
    public async Task MonitorTick_SendsPing_WhenPingIntervalElapsed()
    {
        var timeProvider = new FakeTimeProvider();
        var connection = new KeepAliveTestConnection(
            timeProvider,
            NullLogger.Instance,
            pingInterval: TimeSpan.FromSeconds(5));

        var monitor = new StreamKeepAliveMonitor(_monitorLogger, tickInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await monitor.StartAsync(cts.Token);

        monitor.Register(connection);

        // Advance fake time past the ping interval
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        // Wait for the monitor to tick and process the update
        await Task.Delay(200);

        Assert.True(connection.SentMessages.Count > 0, "Expected at least one ping message");
        Assert.Equal("ping", connection.SentMessages[0].Data);

        cts.Cancel();
        try { await monitor.StopAsync(CancellationToken.None); } catch { }
        monitor.Dispose();
    }

    [Fact]
    public async Task IdleTimeout_ClosesConnection_ThroughMonitor()
    {
        var timeProvider = new FakeTimeProvider();
        using var runCts = new CancellationTokenSource();
        var reader = new BlockingStreamReader<TestIncoming>(runCts.Token);
        var connection = new KeepAliveTestConnection(
            timeProvider,
            NullLogger.Instance,
            reader: reader,
            idleTimeout: TimeSpan.FromSeconds(10));

        var monitor = new StreamKeepAliveMonitor(_monitorLogger, tickInterval: TimeSpan.FromMilliseconds(50));

        using var monitorCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await monitor.StartAsync(monitorCts.Token);
        monitor.Register(connection);

        var runTask = connection.RunAsync(CancellationToken.None);

        // Advance time past the idle timeout
        timeProvider.Advance(TimeSpan.FromSeconds(11));

        // Wait for the monitor tick to detect the timeout
        await Task.Delay(300);

        await runTask;

        Assert.Equal(CloseReason.Timeout, connection.LastCloseReason);
        Assert.True(connection.IsClosed);

        monitorCts.Cancel();
        try { await monitor.StopAsync(CancellationToken.None); } catch { }
        monitor.Dispose();
    }

    [Fact]
    public async Task IncomingMessage_ResetsTimer_PreventsTimeout()
    {
        var timeProvider = new FakeTimeProvider();
        using var runCts = new CancellationTokenSource();
        var reader = new BlockingStreamReader<TestIncoming>(runCts.Token);
        var connection = new KeepAliveTestConnection(
            timeProvider,
            NullLogger.Instance,
            reader: reader,
            idleTimeout: TimeSpan.FromSeconds(10));

        var monitor = new StreamKeepAliveMonitor(_monitorLogger, tickInterval: TimeSpan.FromMilliseconds(50));

        using var monitorCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await monitor.StartAsync(monitorCts.Token);
        monitor.Register(connection);

        // Advance 8 seconds (within the 10s timeout)
        timeProvider.Advance(TimeSpan.FromSeconds(8));

        // Simulate an incoming message resetting the timer
        connection.KeepAliveManager!.UpdateLastMessageTime();

        // Advance another 8 seconds (8s since reset, still within 10s)
        timeProvider.Advance(TimeSpan.FromSeconds(8));

        // Wait for the monitor to tick
        await Task.Delay(200);

        // Connection should NOT have timed out
        Assert.False(connection.IsClosed, "Connection should not have timed out after idle timer reset");

        monitorCts.Cancel();
        runCts.Cancel();
        try { await monitor.StopAsync(CancellationToken.None); } catch { }
        monitor.Dispose();
    }

    [Fact]
    public async Task ClosedConnection_AutoRemovedFromMonitor_OnNextTick()
    {
        var timeProvider = new FakeTimeProvider();
        var connection = new KeepAliveTestConnection(
            timeProvider,
            NullLogger.Instance,
            pingInterval: TimeSpan.FromSeconds(5));

        var monitor = new StreamKeepAliveMonitor(_monitorLogger, tickInterval: TimeSpan.FromMilliseconds(50));

        using var monitorCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await monitor.StartAsync(monitorCts.Token);
        monitor.Register(connection);

        // Close the connection
        await connection.CloseAsync(CancellationToken.None);
        Assert.True(connection.IsClosed);

        // Let monitor tick to detect and auto-remove
        await Task.Delay(200);

        // Advance time past ping interval - should NOT trigger a ping since connection was removed
        int countBefore = connection.SentMessages.Count;
        timeProvider.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(200);

        Assert.Equal(countBefore, connection.SentMessages.Count);

        monitorCts.Cancel();
        try { await monitor.StopAsync(CancellationToken.None); } catch { }
        monitor.Dispose();
    }

    [Fact]
    public async Task MultipleConnections_TrackedIndependently()
    {
        var timeProvider = new FakeTimeProvider();

        // Connection A: 3s ping interval
        var connectionA = new KeepAliveTestConnection(
            timeProvider,
            NullLogger.Instance,
            pingInterval: TimeSpan.FromSeconds(3));

        // Connection B: 10s ping interval
        var connectionB = new KeepAliveTestConnection(
            timeProvider,
            NullLogger.Instance,
            pingInterval: TimeSpan.FromSeconds(10));

        var monitor = new StreamKeepAliveMonitor(_monitorLogger, tickInterval: TimeSpan.FromMilliseconds(50));

        using var monitorCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await monitor.StartAsync(monitorCts.Token);
        monitor.Register(connectionA);
        monitor.Register(connectionB);

        // Advance 4 seconds - only connection A's ping interval has elapsed
        timeProvider.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(200);

        Assert.True(connectionA.SentMessages.Count > 0, "Connection A should have sent a ping");
        Assert.Empty(connectionB.SentMessages);

        // Advance to 11 seconds total - now connection B should also have pinged
        timeProvider.Advance(TimeSpan.FromSeconds(7));
        await Task.Delay(200);

        Assert.True(connectionB.SentMessages.Count > 0, "Connection B should have sent a ping");

        monitorCts.Cancel();
        try { await monitor.StopAsync(CancellationToken.None); } catch { }
        monitor.Dispose();
    }

    [Fact]
    public async Task PingFailure_ClosesConnection()
    {
        var timeProvider = new FakeTimeProvider();
        using var runCts = new CancellationTokenSource();
        var reader = new BlockingStreamReader<TestIncoming>(runCts.Token);
        var connection = new KeepAliveTestConnection(
            timeProvider,
            NullLogger.Instance,
            reader: reader,
            pingInterval: TimeSpan.FromSeconds(5),
            failOnWrite: true);

        var monitor = new StreamKeepAliveMonitor(_monitorLogger, tickInterval: TimeSpan.FromMilliseconds(50));

        using var monitorCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await monitor.StartAsync(monitorCts.Token);
        monitor.Register(connection);

        var runTask = connection.RunAsync(CancellationToken.None);

        // Advance past ping interval to trigger a ping attempt that will fail
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        // Wait for monitor tick and the resulting close
        await Task.Delay(300);

        await runTask;

        Assert.True(connection.IsClosed, "Connection should be closed after ping failure");

        monitorCts.Cancel();
        try { await monitor.StopAsync(CancellationToken.None); } catch { }
        monitor.Dispose();
    }
}

/// <summary>
/// Concrete connection for testing keep-alive and monitor integration.
/// </summary>
internal class KeepAliveTestConnection : StreamConnectionBase<TestIncoming, TestOutgoing>
{
    private readonly IAsyncStreamReader<TestIncoming> _reader;
    private readonly List<TestOutgoing> _written = new();
    private readonly bool _failOnWrite;

    public IReadOnlyList<TestOutgoing> SentMessages => _written;
    public CloseReason? LastCloseReason { get; private set; }
    public Exception? LastCloseException { get; private set; }

    public KeepAliveTestConnection(
        TimeProvider timeProvider,
        ILogger logger,
        IAsyncStreamReader<TestIncoming>? reader = null,
        TimeSpan? pingInterval = null,
        TimeSpan? idleTimeout = null,
        bool failOnWrite = false)
        : base(timeProvider, CancellationToken.None, logger, pingInterval, idleTimeout)
    {
        _reader = reader ?? new InMemoryStreamReader<TestIncoming>(Array.Empty<TestIncoming>());
        _failOnWrite = failOnWrite;
    }

    private protected override IAsyncStreamReader<TestIncoming> GetReader() => _reader;

    private protected override Task WriteMessageAsync(TestOutgoing message)
    {
        if (_failOnWrite)
            throw new InvalidOperationException("Simulated write failure");

        _written.Add(message);
        return Task.CompletedTask;
    }

    private protected override Task OnCloseAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected override Task OnDisposeAsync()
        => Task.CompletedTask;

    protected override TestOutgoing CreatePingMessage()
        => new() { Data = "ping" };

    protected override void OnConnectionClosed(StreamConnectionClosedArgs args)
    {
        LastCloseReason = args.Reason;
        LastCloseException = args.Exception;
    }
}
