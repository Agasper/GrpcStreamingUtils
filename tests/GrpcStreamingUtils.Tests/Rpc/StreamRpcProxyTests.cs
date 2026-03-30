using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using GrpcStreamingUtils.Tests.Proto;
using Niarru.GrpcStreamingUtils.Rpc;

namespace GrpcStreamingUtils.Tests.Rpc;

public interface ITestRpc
{
    Task<TestResponse> Echo(TestRequest request, CancellationToken ct = default);
    Task FireAndForget(VoidRequest request, CancellationToken ct = default);
}

public interface ITestRpcWithTimeout
{
    Task<TestResponse> EchoWithTimeout(TestRequest request, TimeSpan timeout, CancellationToken ct = default);
    Task FireWithTimeout(VoidRequest request, TimeSpan timeout, CancellationToken ct = default);
}

public class StreamRpcProxyTests
{
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Echo_Roundtrip_PacksAndUnpacks()
    {
        RequestEnvelope? captured = null;
        var client = new StreamRpcClient(
            async (env, ct) => { captured = env; },
            defaultTimeout: _defaultTimeout);

        var proxy = client.CreateProxy<ITestRpc>();

        var callTask = proxy.Echo(new TestRequest { Value = "hello" });

        Assert.NotNull(captured);
        Assert.Equal(Any.Pack(new TestRequest()).TypeUrl, captured!.Payload.TypeUrl);

        // Unpack request to verify
        var sentRequest = captured.Payload.Unpack<TestRequest>();
        Assert.Equal("hello", sentRequest.Value);

        // Complete
        client.TryComplete(new ResponseEnvelope
        {
            InReplyToRequestId = captured.RequestId,
            Status = (int)StatusCode.OK,
            Payload = Any.Pack(new TestResponse { Result = "world" })
        });

        var result = await callTask;
        Assert.Equal("world", result.Result);
    }

    [Fact]
    public async Task FireAndForget_VoidReturn_CompletesWithoutPayload()
    {
        RequestEnvelope? captured = null;
        var client = new StreamRpcClient(
            async (env, ct) => { captured = env; },
            defaultTimeout: _defaultTimeout);

        var proxy = client.CreateProxy<ITestRpc>();

        var callTask = proxy.FireAndForget(new VoidRequest { Value = "fire" });

        client.TryComplete(new ResponseEnvelope
        {
            InReplyToRequestId = captured!.RequestId,
            Status = (int)StatusCode.OK
        });

        await callTask; // Should complete without error
    }

    [Fact]
    public async Task Echo_NullPayloadInResponse_ThrowsStreamRpcException()
    {
        RequestEnvelope? captured = null;
        var client = new StreamRpcClient(
            async (env, ct) => { captured = env; },
            defaultTimeout: _defaultTimeout);

        var proxy = client.CreateProxy<ITestRpc>();

        var callTask = proxy.Echo(new TestRequest { Value = "test" });

        client.TryComplete(new ResponseEnvelope
        {
            InReplyToRequestId = captured!.RequestId,
            Status = (int)StatusCode.OK
            // No Payload
        });

        var ex = await Assert.ThrowsAsync<StreamRpcException>(() => callTask);
        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.Contains("no payload", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Echo_StreamRpcException_PreservesStatusCode()
    {
        RequestEnvelope? captured = null;
        var client = new StreamRpcClient(
            async (env, ct) => { captured = env; },
            defaultTimeout: _defaultTimeout);

        var proxy = client.CreateProxy<ITestRpc>();

        var callTask = proxy.Echo(new TestRequest { Value = "test" });

        client.TryComplete(new ResponseEnvelope
        {
            InReplyToRequestId = captured!.RequestId,
            Status = (int)StatusCode.ResourceExhausted,
            Error = "No capacity"
        });

        var ex = await Assert.ThrowsAsync<StreamRpcException>(() => callTask);
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        Assert.Equal("No capacity", ex.Message);
    }

    [Fact]
    public async Task Echo_Timeout_ThrowsTimeoutException()
    {
        var client = new StreamRpcClient(
            async (env, ct) => { },
            defaultTimeout: TimeSpan.FromSeconds(1));

        var proxy = client.CreateProxy<ITestRpc>();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            proxy.Echo(new TestRequest { Value = "slow" }));
    }

    [Fact]
    public async Task EchoWithTimeout_ForwardsTimeoutToCallAsync()
    {
        // Default timeout is 30s, but method timeout is 100ms
        var client = new StreamRpcClient(
            async (env, ct) => { },
            defaultTimeout: TimeSpan.FromSeconds(30));

        var proxy = client.CreateProxy<ITestRpcWithTimeout>();

        // If timeout forwarding works, this should throw in ~100ms, not 30s
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            proxy.EchoWithTimeout(new TestRequest { Value = "test" }, TimeSpan.FromMilliseconds(100)));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Timed out in {sw.Elapsed} — timeout was not forwarded");
    }

    [Fact]
    public async Task FireWithTimeout_ForwardsTimeoutToCallAsync()
    {
        var client = new StreamRpcClient(
            async (env, ct) => { },
            defaultTimeout: TimeSpan.FromSeconds(30));

        var proxy = client.CreateProxy<ITestRpcWithTimeout>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            proxy.FireWithTimeout(new VoidRequest { Value = "test" }, TimeSpan.FromMilliseconds(100)));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Timed out in {sw.Elapsed} — timeout was not forwarded");
    }
}
