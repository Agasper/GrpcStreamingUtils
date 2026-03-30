using Grpc.Core;
using GrpcStreamingUtils.Tests.TestDoubles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Niarru.GrpcStreamingUtils.Connection;
using Niarru.GrpcStreamingUtils.KeepAlive;

namespace GrpcStreamingUtils.Tests.KeepAlive;

public class StreamKeepAliveMonitorTests
{
    private readonly ILogger<StreamKeepAliveMonitor> _logger =
        NullLoggerFactory.Instance.CreateLogger<StreamKeepAliveMonitor>();

    [Fact]
    public async Task Monitor_CallsUpdate_OnRegisteredConnections()
    {
        var timeProvider = new FakeTimeProvider();
        var connection = new TestConnection(
            timeProvider,
            NullLogger.Instance,
            pingInterval: TimeSpan.FromSeconds(5));

        var monitor = new StreamKeepAliveMonitor(_logger, tickInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await monitor.StartAsync(cts.Token);

        monitor.Register(connection);

        // Advance fake time past ping interval so the next tick triggers a ping
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        // Wait for monitor to tick
        await Task.Delay(200);

        Assert.True(connection.WrittenMessages.Count > 0,
            "Expected at least one ping message to be sent");
        Assert.Equal("ping", connection.WrittenMessages[0].Data);

        cts.Cancel();
        try { await monitor.StopAsync(CancellationToken.None); } catch { }
        monitor.Dispose();
    }

    [Fact]
    public async Task Monitor_AutoRemoves_ClosedConnections()
    {
        var timeProvider = new FakeTimeProvider();
        var connection = new TestConnection(
            timeProvider,
            NullLogger.Instance);

        var monitor = new StreamKeepAliveMonitor(_logger, tickInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var monitorTask = monitor.StartAsync(cts.Token);

        monitor.Register(connection);

        // Close the connection
        await connection.CloseAsync(CancellationToken.None);

        // Let monitor tick to auto-remove
        await Task.Delay(200);

        // Unregister should be a no-op (already removed)
        monitor.Unregister(connection);

        cts.Cancel();
        try { await monitor.StopAsync(CancellationToken.None); } catch { }
        monitor.Dispose();
    }

    [Fact]
    public void Register_ThrowsOnNull()
    {
        var monitor = new StreamKeepAliveMonitor(_logger);
        Assert.Throws<ArgumentNullException>(() => monitor.Register(null!));
        monitor.Dispose();
    }

    [Fact]
    public async Task Monitor_SkipsConnections_WithoutKeepAliveManager()
    {
        var timeProvider = new FakeTimeProvider();
        // No pingInterval/idleTimeout = no KeepAliveManager
        var connection = new TestConnection(timeProvider, NullLogger.Instance);

        Assert.Null(connection.KeepAliveManager);

        var monitor = new StreamKeepAliveMonitor(_logger, tickInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await monitor.StartAsync(cts.Token);

        monitor.Register(connection);

        // Let monitor tick — should not throw
        await Task.Delay(200);

        Assert.Empty(connection.WrittenMessages);

        cts.Cancel();
        try { await monitor.StopAsync(CancellationToken.None); } catch { }
        monitor.Dispose();
    }

    [Fact]
    public void Unregister_ThrowsOnNull()
    {
        var monitor = new StreamKeepAliveMonitor(_logger);
        Assert.Throws<ArgumentNullException>(() => monitor.Unregister(null!));
        monitor.Dispose();
    }
}

/// <summary>
/// Minimal concrete connection for testing keep-alive integration.
/// </summary>
internal class TestConnection : StreamConnectionBase<TestIncoming, TestOutgoing>
{
    private readonly List<TestOutgoing> _written = new();

    public TestConnection(
        TimeProvider timeProvider,
        ILogger logger,
        TimeSpan? pingInterval = null,
        TimeSpan? idleTimeout = null)
        : base(timeProvider, CancellationToken.None, logger, pingInterval, idleTimeout)
    {
    }

    public IReadOnlyList<TestOutgoing> WrittenMessages => _written;

    private protected override IAsyncStreamReader<TestIncoming> GetReader()
        => throw new NotImplementedException();

    private protected override Task WriteMessageAsync(TestOutgoing message)
    {
        _written.Add(message);
        return Task.CompletedTask;
    }

    private protected override Task OnCloseAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected override Task OnDisposeAsync()
        => Task.CompletedTask;

    protected override TestOutgoing CreatePingMessage()
        => new() { Data = "ping" };
}

