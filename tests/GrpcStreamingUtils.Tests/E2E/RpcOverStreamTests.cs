using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using GrpcStreamingUtils.Tests.Proto;
using GrpcStreamingUtils.Tests.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using Niarru.GrpcStreamingUtils.Rpc;

namespace GrpcStreamingUtils.Tests.E2E;

[Collection("GrpcServer")]
public class RpcOverStreamTests : IDisposable
{
    private readonly GrpcServerFixture _fixture;

    public RpcOverStreamTests(GrpcServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetForTest();
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task EchoRoundtrip()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var ctx = await CreateClientWithRpc(cts.Token).ConfigureAwait(false);

        var result = await ctx.Proxy.Echo(new TestRequest { Value = "hello" }, cts.Token).ConfigureAwait(false);

        Assert.Equal("echo: hello", result.Result);

        await ctx.Connection.CloseAsync(cts.Token).ConfigureAwait(false);
    }

    [Fact]
    public async Task VoidMethodRoundtrip()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var ctx = await CreateClientWithRpc(cts.Token).ConfigureAwait(false);

        await ctx.Proxy.FireAndForget(new VoidRequest { Value = "fire" }, cts.Token).ConfigureAwait(false);

        await ctx.Connection.CloseAsync(cts.Token).ConfigureAwait(false);
    }

    [Fact]
    public async Task FiveConcurrentCalls()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var ctx = await CreateClientWithRpc(cts.Token).ConfigureAwait(false);

        var tasks = Enumerable.Range(0, 5)
            .Select(i => ctx.Proxy.Echo(new TestRequest { Value = $"concurrent-{i}" }, cts.Token))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal($"echo: concurrent-{i}", results[i].Result);
        }

        await ctx.Connection.CloseAsync(cts.Token).ConfigureAwait(false);
    }

    [Fact]
    public async Task HandlerThrowsStreamRpcException_NotFound()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _fixture.ServerRpcHandler.EchoHandler = (_, _) =>
            throw new StreamRpcException(StatusCode.NotFound, "Not found");

        await using var ctx = await CreateClientWithRpc(cts.Token).ConfigureAwait(false);

        var ex = await Assert.ThrowsAsync<StreamRpcException>(
            () => ctx.Proxy.Echo(new TestRequest { Value = "test" }, cts.Token)).ConfigureAwait(false);

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);

        await ctx.Connection.CloseAsync(cts.Token).ConfigureAwait(false);
    }

    [Fact]
    public async Task HandlerThrowsStreamRpcException_ResourceExhausted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _fixture.ServerRpcHandler.EchoHandler = (_, _) =>
            throw new StreamRpcException(StatusCode.ResourceExhausted, "Too many requests");

        await using var ctx = await CreateClientWithRpc(cts.Token).ConfigureAwait(false);

        var ex = await Assert.ThrowsAsync<StreamRpcException>(
            () => ctx.Proxy.Echo(new TestRequest { Value = "test" }, cts.Token)).ConfigureAwait(false);

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);

        await ctx.Connection.CloseAsync(cts.Token).ConfigureAwait(false);
    }

    [Fact]
    public async Task HandlerThrowsGenericException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _fixture.ServerRpcHandler.EchoHandler = (_, _) =>
            throw new InvalidOperationException("Something broke");

        await using var ctx = await CreateClientWithRpc(cts.Token).ConfigureAwait(false);

        var ex = await Assert.ThrowsAsync<StreamRpcException>(
            () => ctx.Proxy.Echo(new TestRequest { Value = "test" }, cts.Token)).ConfigureAwait(false);

        Assert.Equal(StatusCode.Internal, ex.StatusCode);

        await ctx.Connection.CloseAsync(cts.Token).ConfigureAwait(false);
    }

    [Fact]
    public async Task Timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _fixture.ServerRpcHandler.EchoHandler = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return new TestResponse { Result = "late" };
        };

        await using var ctx = await CreateClientWithRpc(cts.Token, rpcTimeout: TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);

        await Assert.ThrowsAsync<TimeoutException>(
            () => ctx.Proxy.Echo(new TestRequest { Value = "slow" }, cts.Token)).ConfigureAwait(false);

        await ctx.Connection.CloseAsync(cts.Token).ConfigureAwait(false);
    }

    [Fact]
    public async Task UnknownTypeUrl()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var ctx = await CreateClientWithRpc(cts.Token).ConfigureAwait(false);

        // Send a raw RPC request with an unknown TypeUrl directly
        var envelope = new RequestEnvelope
        {
            RequestId = "unknown-type-test",
            Payload = new Any
            {
                TypeUrl = "type.googleapis.com/unknown.Message",
                Value = ByteString.Empty
            }
        };

        var responseTcs = new TaskCompletionSource<ResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Connection.OnMessage = msg =>
        {
            if (msg.ContentCase == TestStreamMessage.ContentOneofCase.RpcResponse
                && msg.RpcResponse.InReplyToRequestId == "unknown-type-test")
            {
                responseTcs.TrySetResult(msg.RpcResponse);
            }
        };

        var rpcMsg = new TestStreamMessage { RpcRequest = envelope };
        await ctx.Connection.SendAsync(rpcMsg, cts.Token).ConfigureAwait(false);

        var response = await responseTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        Assert.Equal((int)StatusCode.Unimplemented, response.Status);

        await ctx.Connection.CloseAsync(cts.Token).ConfigureAwait(false);
    }

    private async Task<RpcClientContext> CreateClientWithRpc(CancellationToken ct, TimeSpan? rpcTimeout = null)
    {
        var grpcClient = new TestStreamService.TestStreamServiceClient(_fixture.Channel);
        var stream = grpcClient.BiDirectionalStream(cancellationToken: ct);

        var clientConn = new TestClientConnection(
            stream,
            TimeProvider.System,
            NullLogger.Instance);

        var rpcClient = new StreamRpcClient(
            async (env, token) =>
            {
                var msg = new TestStreamMessage { RpcRequest = env };
                await clientConn.SendAsync(msg, token).ConfigureAwait(false);
            },
            defaultTimeout: rpcTimeout ?? TimeSpan.FromSeconds(5));

        clientConn.RpcClient = rpcClient;

        var runTask = clientConn.RunAsync(ct);

        // Wait for server connection
        while (!ct.IsCancellationRequested)
        {
            if (_fixture.ServerConnections.TryPeek(out _))
                break;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        var proxy = rpcClient.CreateProxy<ITestRpc>();

        return new RpcClientContext(clientConn, rpcClient, proxy, runTask);
    }

    private sealed class RpcClientContext : IAsyncDisposable
    {
        public TestClientConnection Connection { get; }
        public StreamRpcClient RpcClient { get; }
        public ITestRpc Proxy { get; }
        public Task RunTask { get; }

        public RpcClientContext(TestClientConnection connection, StreamRpcClient rpcClient, ITestRpc proxy, Task runTask)
        {
            Connection = connection;
            RpcClient = rpcClient;
            Proxy = proxy;
            RunTask = runTask;
        }

        public async ValueTask DisposeAsync()
        {
            RpcClient.Dispose();
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
