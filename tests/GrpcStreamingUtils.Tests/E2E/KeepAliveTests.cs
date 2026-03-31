using GrpcStreamingUtils.Tests.Proto;
using Microsoft.Extensions.Logging.Abstractions;
using Niarru.GrpcStreamingUtils.Connection;

namespace GrpcStreamingUtils.Tests.E2E;

[Collection("GrpcServer")]
public class KeepAliveTests : IDisposable
{
    private readonly GrpcServerFixture _fixture;

    public KeepAliveTests(GrpcServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetForTest();
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task Ping_SentThroughRealStream()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance,
            pingInterval: TimeSpan.FromMilliseconds(200));

        _fixture.Monitor.Register(clientConn);

        var runTask = clientConn.RunAsync(cts.Token);
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        var pingReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnMessage = msg =>
        {
            if (msg.ContentCase == TestStreamMessage.ContentOneofCase.Ping)
                pingReceived.TrySetResult();
        };

        await pingReceived.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        _fixture.Monitor.Unregister(clientConn);
        await clientConn.CloseAsync(cts.Token).ConfigureAwait(false);
        await clientConn.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task ServerIdleTimeout_ClosesConnection_ClientSeesClose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Configure server to have a short idle timeout
        _fixture.ServerIdleTimeout = TimeSpan.FromSeconds(2);

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance);

        var runTask = clientConn.RunAsync(cts.Token);
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        // Don't send anything -- let the server time out
        await serverConn.Closed.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.Equal(CloseReason.Timeout, serverConn.LastCloseReason);

        // Client should see the stream end as normal close
        await clientConn.Closed.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        Assert.Equal(CloseReason.Normal, clientConn.LastCloseReason);

        await clientConn.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task ClientIdleTimeout_ClosesConnection_ServerSeesClose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance,
            idleTimeout: TimeSpan.FromSeconds(2));

        _fixture.Monitor.Register(clientConn);

        var runTask = clientConn.RunAsync(cts.Token);
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        // Don't send anything from the server -- let the client time out
        await clientConn.Closed.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.Equal(CloseReason.Timeout, clientConn.LastCloseReason);

        // Server should see the stream end
        await serverConn.Closed.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        _fixture.Monitor.Unregister(clientConn);
        await clientConn.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task PingKeepsConnectionAlive()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Server has idle timeout; client sends pings to keep alive
        _fixture.ServerIdleTimeout = TimeSpan.FromSeconds(2);

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        // Client with ping interval shorter than server idle timeout
        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance,
            pingInterval: TimeSpan.FromMilliseconds(500));

        _fixture.Monitor.Register(clientConn);

        var runTask = clientConn.RunAsync(cts.Token);
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        // Wait longer than the server idle timeout
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false);

        // Connection should still be alive because pings are keeping it alive
        Assert.Null(serverConn.LastCloseReason);
        Assert.False(serverConn.Closed.Task.IsCompleted);

        _fixture.Monitor.Unregister(clientConn);
        await clientConn.CloseAsync(cts.Token).ConfigureAwait(false);
        await clientConn.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<TestServerConnection> WaitForServerConnection(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_fixture.ServerConnections.TryPeek(out var conn))
                return conn;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        throw new OperationCanceledException("Timed out waiting for server connection");
    }
}
