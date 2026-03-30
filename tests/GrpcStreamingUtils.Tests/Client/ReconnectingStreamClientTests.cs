using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Niarru.GrpcStreamingUtils.Client;
using Niarru.GrpcStreamingUtils.Connection;
using Niarru.GrpcStreamingUtils.Exceptions;
using Niarru.GrpcStreamingUtils.KeepAlive;

namespace GrpcStreamingUtils.Tests.Client;

public class ReconnectingStreamClientTests
{
    private readonly ILogger<StreamKeepAliveMonitor> _monitorLogger =
        NullLoggerFactory.Instance.CreateLogger<StreamKeepAliveMonitor>();
    private readonly ILogger _clientLogger = NullLogger.Instance;

    [Fact]
    public async Task SendAsync_WhenNotConnected_ThrowsStreamNotEstablished()
    {
        var monitor = new StreamKeepAliveMonitor(_monitorLogger);
        var client = new TestReconnectingClient(
            monitor, _clientLogger,
            streamFactory: _ => CreateFakeStream(Array.Empty<RpcIncoming>()));

        // Don't start the client — no connection
        await Assert.ThrowsAsync<StreamNotEstablishedException>(() =>
            client.PublicSendAsync(new RpcOutgoing(), CancellationToken.None));

        monitor.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var monitor = new StreamKeepAliveMonitor(_monitorLogger);
        var client = new TestReconnectingClient(
            monitor, _clientLogger,
            streamFactory: _ => CreateFakeStream(Array.Empty<RpcIncoming>()));

        await client.DisposeAsync();
        await client.DisposeAsync(); // should not throw

        monitor.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_ReconnectsAfterStreamError()
    {
        var monitor = new StreamKeepAliveMonitor(_monitorLogger);
        int connectCount = 0;

        var client = new TestReconnectingClient(
            monitor, _clientLogger,
            initialReconnectInterval: TimeSpan.FromMilliseconds(10),
            streamFactory: _ =>
            {
                var count = Interlocked.Increment(ref connectCount);
                if (count == 1)
                {
                    // First connection: reader throws
                    return CreateFakeStream(
                        new ThrowingRpcStreamReader(new InvalidOperationException("connection lost")));
                }
                // Second connection: succeeds with empty stream (will complete immediately)
                return CreateFakeStream(Array.Empty<RpcIncoming>());
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.StartAsync(cts.Token);

        // Wait for at least 2 connections
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref connectCount) < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        cts.Cancel();
        try { await client.StopAsync(CancellationToken.None); } catch { }
        client.Dispose();
        monitor.Dispose();

        Assert.True(connectCount >= 2, $"Expected at least 2 connections, got {connectCount}");
    }

    [Fact]
    public async Task ExecuteAsync_SetsIsConnected_DuringActiveConnection()
    {
        var monitor = new StreamKeepAliveMonitor(_monitorLogger);
        var connectionEstablished = new TaskCompletionSource();
        var keepConnectionOpen = new TaskCompletionSource();

        var client = new TestReconnectingClient(
            monitor, _clientLogger,
            streamFactory: _ => CreateFakeStream(
                new ControlledStreamReader(connectionEstablished, keepConnectionOpen.Task)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.StartAsync(cts.Token);

        // Wait for connection to be established (reader signals on first MoveNext)
        await connectionEstablished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(client.PublicIsConnected);

        // Close the connection
        keepConnectionOpen.SetResult();
        await Task.Delay(100);

        // After stream completes, connection is cleaned up
        Assert.False(client.PublicIsConnected);

        cts.Cancel();
        try { await client.StopAsync(CancellationToken.None); } catch { }
        client.Dispose();
        monitor.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_CallsHandshake_BeforeCreatingConnection()
    {
        var monitor = new StreamKeepAliveMonitor(_monitorLogger);
        var handshakeCalled = false;

        var client = new TestReconnectingClient(
            monitor, _clientLogger,
            streamFactory: _ => CreateFakeStream(Array.Empty<RpcIncoming>()),
            onHandshake: (_, _) => { handshakeCalled = true; return Task.CompletedTask; });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await client.StartAsync(cts.Token);

        await Task.Delay(200);

        cts.Cancel();
        try { await client.StopAsync(CancellationToken.None); } catch { }
        client.Dispose();
        monitor.Dispose();

        Assert.True(handshakeCalled);
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        var monitor = new StreamKeepAliveMonitor(_monitorLogger);
        var keepOpen = new TaskCompletionSource();
        var connected = new TaskCompletionSource();

        var client = new TestReconnectingClient(
            monitor, _clientLogger,
            streamFactory: _ => CreateFakeStream(
                new ControlledStreamReader(connected, keepOpen.Task)));

        using var cts = new CancellationTokenSource();
        await client.StartAsync(cts.Token);

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cts.Cancel();

        // StopAsync should complete without hanging
        var stopTask = client.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Equal(stopTask, completed);

        client.Dispose();
        monitor.Dispose();
    }

    private static AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming> CreateFakeStream(
        IEnumerable<RpcIncoming> messages)
    {
        return CreateFakeStream(new InMemoryRpcStreamReader(messages));
    }

    private static AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming> CreateFakeStream(
        IAsyncStreamReader<RpcIncoming> reader)
    {
        return new AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming>(
            new FakeClientStreamWriter(),
            reader,
            Task.FromResult(new Metadata()),
            () => new Status(StatusCode.OK, ""),
            () => new Metadata(),
            () => { });
    }
}

#region Test doubles

internal class RpcIncoming
{
    public string Data { get; set; } = "";
}

internal class RpcOutgoing
{
    public string Data { get; set; } = "";
}

internal class TestClientConnection : ClientStreamConnection<RpcIncoming, RpcOutgoing>
{
    public TestClientConnection(
        AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming> stream,
        TimeProvider timeProvider,
        ILogger logger)
        : base(stream, timeProvider, logger)
    {
    }

    protected override RpcOutgoing CreatePingMessage() => new();
}

internal class TestReconnectingClient
    : ReconnectingStreamClient<TestClientConnection, RpcIncoming, RpcOutgoing>
{
    private readonly Func<CancellationToken, AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming>> _streamFactory;
    private readonly Func<AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming>, CancellationToken, Task>? _onHandshake;

    public TestReconnectingClient(
        StreamKeepAliveMonitor monitor,
        ILogger logger,
        Func<CancellationToken, AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming>> streamFactory,
        Func<AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming>, CancellationToken, Task>? onHandshake = null,
        TimeSpan? initialReconnectInterval = null)
        : base(monitor, TimeProvider.System, logger,
            initialReconnectInterval: initialReconnectInterval ?? TimeSpan.FromMilliseconds(10))
    {
        _streamFactory = streamFactory;
        _onHandshake = onHandshake;
    }

    protected override string ClientName => "TestClient";

    protected override AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming> CreateStream(CancellationToken ct)
        => _streamFactory(ct);

    protected override TestClientConnection CreateConnection(
        AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming> stream)
        => new(stream, TimeProvider, Logger);

    protected override Task SendHandshakeAsync(
        AsyncDuplexStreamingCall<RpcOutgoing, RpcIncoming> stream,
        CancellationToken cancellationToken)
        => _onHandshake?.Invoke(stream, cancellationToken) ?? Task.CompletedTask;

    public Task PublicSendAsync(RpcOutgoing msg, CancellationToken ct) => SendAsync(msg, ct);
    public bool PublicIsConnected => IsConnected;
}

internal class FakeClientStreamWriter : IClientStreamWriter<RpcOutgoing>
{
    public WriteOptions? WriteOptions { get; set; }
    public Task WriteAsync(RpcOutgoing message) => Task.CompletedTask;
    public Task CompleteAsync() => Task.CompletedTask;
}

internal class InMemoryRpcStreamReader : IAsyncStreamReader<RpcIncoming>
{
    private readonly IEnumerator<RpcIncoming> _enumerator;

    public InMemoryRpcStreamReader(IEnumerable<RpcIncoming> items)
    {
        _enumerator = items.GetEnumerator();
    }

    public RpcIncoming Current => _enumerator.Current;

    public Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_enumerator.MoveNext());
    }
}

internal class ThrowingRpcStreamReader : IAsyncStreamReader<RpcIncoming>
{
    private readonly Exception _exception;
    public ThrowingRpcStreamReader(Exception exception) => _exception = exception;
    public RpcIncoming Current => throw new InvalidOperationException();
    public Task<bool> MoveNext(CancellationToken cancellationToken) => throw _exception;
}

internal class ControlledStreamReader : IAsyncStreamReader<RpcIncoming>
{
    private readonly TaskCompletionSource _onFirstRead;
    private readonly Task _keepOpenUntil;
    private bool _signaled;

    public ControlledStreamReader(TaskCompletionSource onFirstRead, Task keepOpenUntil)
    {
        _onFirstRead = onFirstRead;
        _keepOpenUntil = keepOpenUntil;
    }

    public RpcIncoming Current => throw new InvalidOperationException();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (!_signaled)
        {
            _signaled = true;
            _onFirstRead.TrySetResult();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await _keepOpenUntil.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        return false;
    }
}

#endregion
