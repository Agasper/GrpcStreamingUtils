using GrpcStreamingUtils.Tests.Proto;
using Microsoft.Extensions.Logging.Abstractions;
using Niarru.GrpcStreamingUtils.Connection;

namespace GrpcStreamingUtils.Tests.E2E;

[Collection("GrpcServer")]
public class ConnectionCloseTests : IDisposable
{
    private readonly GrpcServerFixture _fixture;

    public ConnectionCloseTests(GrpcServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetForTest();
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task ClientClose_ServerSeesNormalClose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance);

        var runTask = clientConn.RunAsync(cts.Token);
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        await clientConn.CloseAsync(cts.Token).ConfigureAwait(false);

        await serverConn.Closed.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.Equal(CloseReason.Normal, serverConn.LastCloseReason);

        await clientConn.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task ServerClose_ClientSeesNormalClose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance);

        var runTask = clientConn.RunAsync(cts.Token);
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        await serverConn.CloseAsync(cts.Token).ConfigureAwait(false);

        await clientConn.Closed.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.Equal(CloseReason.Normal, clientConn.LastCloseReason);

        await clientConn.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task ClientDispose_ServerSeesClose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance);

        var runTask = clientConn.RunAsync(cts.Token);
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        await clientConn.DisposeAsync().ConfigureAwait(false);

        await serverConn.Closed.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.NotNull(serverConn.LastCloseReason);
    }

    [Fact]
    public async Task ConnectionClosed_Token_Triggered_OnBothSides()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance);

        var runTask = clientConn.RunAsync(cts.Token);
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        // Capture tokens before close
        var clientToken = clientConn.ConnectionClosed;
        var serverToken = serverConn.ConnectionClosed;

        Assert.False(clientToken.IsCancellationRequested);

        await clientConn.CloseAsync(cts.Token).ConfigureAwait(false);

        // Wait for both sides to be notified
        await clientConn.Closed.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        await serverConn.Closed.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.True(clientToken.IsCancellationRequested);
        Assert.True(serverToken.IsCancellationRequested);

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
