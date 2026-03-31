using Grpc.Core;
using Grpc.Net.Client;
using GrpcStreamingUtils.Tests.Proto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Niarru.GrpcStreamingUtils.Client;
using Niarru.GrpcStreamingUtils.KeepAlive;

namespace GrpcStreamingUtils.Tests.E2E;

[Collection("GrpcServer")]
public class ReconnectionTests : IDisposable
{
    private readonly GrpcServerFixture _fixture;

    public ReconnectionTests(GrpcServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetForTest();
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task ServerClosesStream_ClientReconnects()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var reconnectingClient = new TestReconnectingClient(
            _fixture.Channel,
            _fixture.Monitor,
            NullLoggerFactory.Instance.CreateLogger<TestReconnectingClient>());

        await reconnectingClient.StartAsync(cts.Token).ConfigureAwait(false);

        // Wait for first server connection
        var firstConn = await WaitForServerConnectionCount(1, cts.Token).ConfigureAwait(false);

        // Close the server-side connection
        await firstConn.CloseAsync(cts.Token).ConfigureAwait(false);

        // Wait for reconnection (second server connection)
        await WaitForServerConnectionCount(2, cts.Token).ConfigureAwait(false);

        Assert.True(reconnectingClient.ConnectCount >= 2);

        await reconnectingClient.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task AfterReconnect_HandshakeSentAgain()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var reconnectingClient = new TestReconnectingClient(
            _fixture.Channel,
            _fixture.Monitor,
            NullLoggerFactory.Instance.CreateLogger<TestReconnectingClient>());

        await reconnectingClient.StartAsync(cts.Token).ConfigureAwait(false);

        var firstConn = await WaitForServerConnectionCount(1, cts.Token).ConfigureAwait(false);

        Assert.Equal(1, reconnectingClient.HandshakeCount);

        await firstConn.CloseAsync(cts.Token).ConfigureAwait(false);

        await WaitForServerConnectionCount(2, cts.Token).ConfigureAwait(false);

        Assert.True(reconnectingClient.HandshakeCount >= 2,
            $"Expected at least 2 handshakes, got {reconnectingClient.HandshakeCount}");

        await reconnectingClient.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task IsConnected_ReflectsState()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var reconnectingClient = new TestReconnectingClient(
            _fixture.Channel,
            _fixture.Monitor,
            NullLoggerFactory.Instance.CreateLogger<TestReconnectingClient>());

        Assert.False(reconnectingClient.IsConnectedPublic);

        await reconnectingClient.StartAsync(cts.Token).ConfigureAwait(false);

        // Wait for connection to be established
        var firstConn = await WaitForServerConnectionCount(1, cts.Token).ConfigureAwait(false);

        // Wait for the client to report connected
        await WaitUntil(() => reconnectingClient.IsConnectedPublic, cts.Token).ConfigureAwait(false);
        Assert.True(reconnectingClient.IsConnectedPublic);

        // Close from server side
        await firstConn.CloseAsync(cts.Token).ConfigureAwait(false);

        // Wait for IsConnected to become false (the client is between connections)
        await WaitUntil(() => !reconnectingClient.IsConnectedPublic, cts.Token).ConfigureAwait(false);
        Assert.False(reconnectingClient.IsConnectedPublic);

        // Wait for reconnection
        await WaitForServerConnectionCount(2, cts.Token).ConfigureAwait(false);

        await WaitUntil(() => reconnectingClient.IsConnectedPublic, cts.Token).ConfigureAwait(false);
        Assert.True(reconnectingClient.IsConnectedPublic);

        await reconnectingClient.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<TestServerConnection> WaitForServerConnectionCount(int count, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_fixture.ServerConnections.Count >= count)
                return _fixture.ServerConnections.First();
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        throw new OperationCanceledException($"Timed out waiting for {count} server connections");
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (condition())
                return;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        throw new OperationCanceledException("Timed out waiting for condition");
    }

    private sealed class TestReconnectingClient : ReconnectingStreamClient<TestClientConnection, TestStreamMessage, TestStreamMessage>
    {
        private readonly GrpcChannel _channel;
        private int _connectCount;
        private int _handshakeCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public int HandshakeCount => Volatile.Read(ref _handshakeCount);
        public bool IsConnectedPublic => IsConnected;

        protected override string ClientName => "TestReconnectingClient";

        public TestReconnectingClient(
            GrpcChannel channel,
            StreamKeepAliveMonitor monitor,
            ILogger logger)
            : base(
                monitor,
                TimeProvider.System,
                logger,
                initialReconnectInterval: TimeSpan.FromMilliseconds(100),
                maxReconnectInterval: TimeSpan.FromMilliseconds(500))
        {
            _channel = channel;
        }

        protected override AsyncDuplexStreamingCall<TestStreamMessage, TestStreamMessage> CreateStream(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            var client = new TestStreamService.TestStreamServiceClient(_channel);
            return client.BiDirectionalStream(cancellationToken: cancellationToken);
        }

        protected override TestClientConnection CreateConnection(AsyncDuplexStreamingCall<TestStreamMessage, TestStreamMessage> stream)
        {
            return new TestClientConnection(
                stream,
                TimeProvider.System,
                NullLogger.Instance);
        }

        protected override Task SendHandshakeAsync(
            AsyncDuplexStreamingCall<TestStreamMessage, TestStreamMessage> stream,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _handshakeCount);
            return Task.CompletedTask;
        }
    }
}
