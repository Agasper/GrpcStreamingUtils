using GrpcStreamingUtils.Tests.Proto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrpcStreamingUtils.Tests.E2E;

[Collection("GrpcServer")]
public class BasicStreamingTests : IDisposable
{
    private readonly GrpcServerFixture _fixture;

    public BasicStreamingTests(GrpcServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetForTest();
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task Client_SendsMessage_ServerReceives()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance);

        var runTask = clientConn.RunAsync(cts.Token);

        // Wait for server connection to appear
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        var received = new TaskCompletionSource<TestStreamMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnMessage = msg => received.TrySetResult(msg);

        var testMessage = new TestStreamMessage
        {
            RpcRequest = new Niarru.GrpcStreamingUtils.Rpc.RequestEnvelope
            {
                RequestId = "test-123",
                Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new TestRequest { Value = "hello-from-client" })
            }
        };

        await clientConn.SendAsync(testMessage, cts.Token).ConfigureAwait(false);

        var receivedMsg = await received.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.Equal(TestStreamMessage.ContentOneofCase.RpcRequest, receivedMsg.ContentCase);
        Assert.Equal("test-123", receivedMsg.RpcRequest.RequestId);

        await clientConn.CloseAsync(cts.Token).ConfigureAwait(false);
        await clientConn.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task Server_SendsMessage_ClientReceives()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance);

        var received = new TaskCompletionSource<TestStreamMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientConn.OnMessage = msg => received.TrySetResult(msg);

        var runTask = clientConn.RunAsync(cts.Token);

        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        var testMessage = new TestStreamMessage
        {
            RpcRequest = new Niarru.GrpcStreamingUtils.Rpc.RequestEnvelope
            {
                RequestId = "from-server",
                Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new TestRequest { Value = "hello-from-server" })
            }
        };

        await serverConn.SendAsync(testMessage, cts.Token).ConfigureAwait(false);

        var receivedMsg = await received.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.Equal(TestStreamMessage.ContentOneofCase.RpcRequest, receivedMsg.ContentCase);
        Assert.Equal("from-server", receivedMsg.RpcRequest.RequestId);

        await clientConn.CloseAsync(cts.Token).ConfigureAwait(false);
        await clientConn.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task BidirectionalExchange()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const int messageCount = 5;

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance);

        var clientReceived = new List<TestStreamMessage>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clientConn.OnMessage = msg =>
        {
            if (msg.ContentCase == TestStreamMessage.ContentOneofCase.Ping)
            {
                lock (clientReceived)
                {
                    if (clientReceived.Count >= messageCount) return;
                    clientReceived.Add(msg);
                    if (clientReceived.Count >= messageCount)
                        allReceived.TrySetResult();
                }
            }
        };

        var runTask = clientConn.RunAsync(cts.Token);
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        // Server echoes back Ping messages
        serverConn.OnMessage = async msg =>
        {
            if (msg.ContentCase == TestStreamMessage.ContentOneofCase.Ping)
            {
                try
                {
                    await serverConn.SendAsync(msg, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            }
        };

        for (int i = 0; i < messageCount; i++)
        {
            var msg = new TestStreamMessage { Ping = new Ping() };
            await clientConn.SendAsync(msg, cts.Token).ConfigureAwait(false);
        }

        await allReceived.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.Equal(messageCount, clientReceived.Count);

        await clientConn.CloseAsync(cts.Token).ConfigureAwait(false);
        await clientConn.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task HundredMessages_DeliveredInOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const int messageCount = 100;

        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: cts.Token);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance);

        var clientReceived = new List<string>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clientConn.OnMessage = msg =>
        {
            if (msg.ContentCase == TestStreamMessage.ContentOneofCase.RpcResponse)
            {
                lock (clientReceived)
                {
                    clientReceived.Add(msg.RpcResponse.InReplyToRequestId);
                    if (clientReceived.Count >= messageCount)
                        allReceived.TrySetResult();
                }
            }
        };

        var runTask = clientConn.RunAsync(cts.Token);
        var serverConn = await WaitForServerConnection(cts.Token).ConfigureAwait(false);

        // Server echoes RpcResponse messages back in order
        var echoLock = new SemaphoreSlim(1, 1);
        serverConn.OnMessage = async msg =>
        {
            if (msg.ContentCase == TestStreamMessage.ContentOneofCase.RpcResponse)
            {
                await echoLock.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    await serverConn.SendAsync(msg, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    echoLock.Release();
                }
            }
        };

        for (int i = 0; i < messageCount; i++)
        {
            var msg = new TestStreamMessage
            {
                RpcResponse = new Niarru.GrpcStreamingUtils.Rpc.ResponseEnvelope
                {
                    InReplyToRequestId = $"{i:D4}"
                }
            };
            await clientConn.SendAsync(msg, cts.Token).ConfigureAwait(false);
        }

        await allReceived.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.Equal(messageCount, clientReceived.Count);
        for (int i = 0; i < messageCount; i++)
        {
            Assert.Equal($"{i:D4}", clientReceived[i]);
        }

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
