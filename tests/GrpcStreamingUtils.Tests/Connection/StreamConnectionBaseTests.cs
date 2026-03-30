using Grpc.Core;
using GrpcStreamingUtils.Tests.TestDoubles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Niarru.GrpcStreamingUtils.Connection;

namespace GrpcStreamingUtils.Tests.Connection;

public class StreamConnectionBaseTests
{
    private readonly ILogger _logger = NullLogger.Instance;

    [Fact]
    public async Task RunAsync_ReadsAllMessages_AndClosesNormally()
    {
        var messages = new[] { new TestIncoming { Data = "a" }, new TestIncoming { Data = "b" } };
        var connection = new FakeConnection(_logger, messages);

        var received = new List<string>();
        connection.OnMessageReceived = (msg, _) =>
        {
            received.Add(msg.Data);
            return Task.CompletedTask;
        };

        await connection.RunAsync(CancellationToken.None);

        Assert.Equal(["a", "b"], received);
        Assert.Equal(CloseReason.Normal, connection.LastCloseReason);
    }

    [Fact]
    public async Task RunAsync_OnCancellation_ClosesNormally()
    {
        using var cts = new CancellationTokenSource();
        var reader = new BlockingStreamReader<TestIncoming>(cts.Token);
        var connection = new FakeConnection(_logger, reader);

        var runTask = connection.RunAsync(cts.Token);
        cts.Cancel();

        await runTask;

        Assert.Equal(CloseReason.Normal, connection.LastCloseReason);
    }

    [Fact]
    public async Task RunAsync_OnException_ClosesWithError()
    {
        var reader = new ThrowingStreamReader<TestIncoming>(new InvalidOperationException("test error"));
        var connection = new FakeConnection(_logger, reader);

        await connection.RunAsync(CancellationToken.None);

        Assert.Equal(CloseReason.Error, connection.LastCloseReason);
        Assert.NotNull(connection.LastCloseException);
        Assert.Equal("test error", connection.LastCloseException!.Message);
    }

    [Fact]
    public async Task SendAsync_SerializesWrites()
    {
        var connection = new FakeConnection(_logger, Array.Empty<TestIncoming>());
        var barrier = new TaskCompletionSource();

        // First write will block until barrier is released
        int writeCount = 0;
        connection.WriteDelay = async () =>
        {
            if (Interlocked.Increment(ref writeCount) == 1)
                await barrier.Task;
        };

        var send1 = connection.SendAsync(new TestOutgoing { Data = "1" }, CancellationToken.None);
        var send2 = connection.SendAsync(new TestOutgoing { Data = "2" }, CancellationToken.None);

        // send2 should be waiting for send1
        await Task.Delay(50);
        Assert.False(send2.IsCompleted);

        barrier.SetResult();
        await send1;
        await send2;

        Assert.Equal(["1", "2"], connection.WrittenMessages.Select(m => m.Data).ToList());
    }

    [Fact]
    public async Task SendAsync_ThrowsAfterDispose()
    {
        var connection = new FakeConnection(_logger, Array.Empty<TestIncoming>());
        await connection.DisposeAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            connection.SendAsync(new TestOutgoing { Data = "x" }, CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var connection = new FakeConnection(_logger, Array.Empty<TestIncoming>());

        await connection.DisposeAsync();
        await connection.DisposeAsync(); // should not throw
    }

    [Fact]
    public async Task CloseAsync_InvokesOnConnectionClosed()
    {
        var connection = new FakeConnection(_logger, Array.Empty<TestIncoming>());

        await connection.CloseAsync(CancellationToken.None);

        Assert.Equal(CloseReason.Normal, connection.LastCloseReason);
        Assert.True(connection.IsClosed);
    }

    [Fact]
    public async Task CloseAsync_IsIdempotent()
    {
        var connection = new FakeConnection(_logger, Array.Empty<TestIncoming>());
        int closedCount = 0;
        connection.OnClosed = _ => closedCount++;

        await connection.CloseAsync(CancellationToken.None);
        await connection.CloseAsync(CancellationToken.None);

        Assert.Equal(1, closedCount);
    }

    [Fact]
    public void KeepAliveManager_NotCreated_WhenNoPingOrTimeout()
    {
        var connection = new FakeConnection(_logger, Array.Empty<TestIncoming>());
        Assert.Null(connection.KeepAliveManager);
    }

    [Fact]
    public void KeepAliveManager_Created_WhenPingIntervalSet()
    {
        var connection = new FakeConnection(_logger, Array.Empty<TestIncoming>(),
            pingInterval: TimeSpan.FromSeconds(5));
        Assert.NotNull(connection.KeepAliveManager);
    }

    [Fact]
    public void KeepAliveManager_Created_WhenIdleTimeoutSet()
    {
        var connection = new FakeConnection(_logger, Array.Empty<TestIncoming>(),
            idleTimeout: TimeSpan.FromSeconds(30));
        Assert.NotNull(connection.KeepAliveManager);
    }

    [Fact]
    public async Task RunAsync_OnRpcExceptionCancelled_ClosesNormally()
    {
        var reader = new ThrowingStreamReader<TestIncoming>(
            new RpcException(new Status(StatusCode.Cancelled, "cancelled")));
        var connection = new FakeConnection(_logger, reader);

        await connection.RunAsync(CancellationToken.None);

        Assert.Equal(CloseReason.Normal, connection.LastCloseReason);
    }

    [Fact]
    public async Task RunAsync_Timeout_ClosesWithTimeoutReason()
    {
        var timeProvider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var reader = new BlockingStreamReader<TestIncoming>(cts.Token);
        var connection = new FakeConnection(
            _logger, reader, timeProvider,
            idleTimeout: TimeSpan.FromSeconds(10));

        var runTask = connection.RunAsync(CancellationToken.None);

        // Simulate timeout by advancing time and calling Update
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await connection.KeepAliveManager!.Update(CancellationToken.None);

        await runTask;

        Assert.Equal(CloseReason.Timeout, connection.LastCloseReason);
    }

    [Fact]
    public async Task ConnectionClosed_IsTriggered_OnClose()
    {
        var connection = new FakeConnection(_logger, Array.Empty<TestIncoming>());

        Assert.False(connection.ConnectionClosed.IsCancellationRequested);

        await connection.CloseAsync(CancellationToken.None);

        Assert.True(connection.ConnectionClosed.IsCancellationRequested);
    }

    [Fact]
    public async Task OnConnectionClosed_HandlerException_IsCaughtAndDoesNotPropagate()
    {
        var connection = new FakeConnection(_logger, Array.Empty<TestIncoming>());
        connection.OnClosed = _ => throw new InvalidOperationException("handler error");

        // Should not throw despite handler exception
        await connection.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SendAsync_ThrowsWhenConnectionCancelled()
    {
        using var cts = new CancellationTokenSource();
        var reader = new BlockingStreamReader<TestIncoming>(cts.Token);
        var connection = new FakeConnection(_logger, reader);

        // Start and close the connection
        await connection.CloseAsync(CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            connection.SendAsync(new TestOutgoing { Data = "x" }, CancellationToken.None));
    }
}

internal class FakeConnection : StreamConnectionBase<TestIncoming, TestOutgoing>
{
    private readonly IAsyncStreamReader<TestIncoming> _reader;
    private readonly List<TestOutgoing> _written = new();

    public Func<TestIncoming, CancellationToken, Task>? OnMessageReceived;
    public Action<StreamConnectionClosedArgs>? OnClosed;
    public Func<Task>? WriteDelay;
    public CloseReason? LastCloseReason;
    public Exception? LastCloseException;
    public IReadOnlyList<TestOutgoing> WrittenMessages => _written;

    public FakeConnection(
        ILogger logger,
        IEnumerable<TestIncoming> messages,
        TimeSpan? pingInterval = null,
        TimeSpan? idleTimeout = null)
        : base(TimeProvider.System, CancellationToken.None, logger, pingInterval, idleTimeout)
    {
        _reader = new InMemoryStreamReader<TestIncoming>(messages);
    }

    public FakeConnection(
        ILogger logger,
        IAsyncStreamReader<TestIncoming> reader,
        TimeSpan? pingInterval = null,
        TimeSpan? idleTimeout = null)
        : base(TimeProvider.System, CancellationToken.None, logger, pingInterval, idleTimeout)
    {
        _reader = reader;
    }

    public FakeConnection(
        ILogger logger,
        IAsyncStreamReader<TestIncoming> reader,
        TimeProvider timeProvider,
        TimeSpan? pingInterval = null,
        TimeSpan? idleTimeout = null)
        : base(timeProvider, CancellationToken.None, logger, pingInterval, idleTimeout)
    {
        _reader = reader;
    }

    private protected override IAsyncStreamReader<TestIncoming> GetReader() => _reader;

    private protected override async Task WriteMessageAsync(TestOutgoing message)
    {
        if (WriteDelay != null)
            await WriteDelay();
        _written.Add(message);
    }

    private protected override Task OnCloseAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected override Task OnDisposeAsync()
        => Task.CompletedTask;

    protected override TestOutgoing CreatePingMessage()
        => new() { Data = "ping" };

    protected override Task OnMessageReceivedAsync(TestIncoming message, CancellationToken cancellationToken)
        => OnMessageReceived?.Invoke(message, cancellationToken) ?? Task.CompletedTask;

    protected override void OnConnectionClosed(StreamConnectionClosedArgs args)
    {
        LastCloseReason = args.Reason;
        LastCloseException = args.Exception;
        OnClosed?.Invoke(args);
    }
}

