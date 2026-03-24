using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using GrpcStreamingUtils.Tests.Proto;
using Niarru.GrpcStreamingUtils.Rpc;

namespace GrpcStreamingUtils.Tests.Rpc;

public class StreamRpcClientTests
{
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CallAsync_SendsRequestEnvelope_WithCorrectTypeUrl()
    {
        RequestEnvelope? captured = null;
        var client = new StreamRpcClient(
            async (env, ct) => { captured = env; },
            defaultTimeout: _defaultTimeout);

        var request = new TestRequest { Value = "hello" };
        var callTask = client.CallAsync(request, timeout: null, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.NotEmpty(captured!.RequestId);
        Assert.Equal(Any.Pack(request).TypeUrl, captured.Payload.TypeUrl);

        // Complete to avoid hanging
        client.TryComplete(new ResponseEnvelope
        {
            InReplyToRequestId = captured.RequestId,
            Status = (int)StatusCode.OK,
            Payload = Any.Pack(new TestResponse { Result = "world" })
        });

        await callTask;
    }

    [Fact]
    public async Task TryComplete_OK_ResolvesTask()
    {
        RequestEnvelope? captured = null;
        var client = new StreamRpcClient(
            async (env, ct) => { captured = env; },
            defaultTimeout: _defaultTimeout);

        var callTask = client.CallAsync(new TestRequest { Value = "test" }, null, CancellationToken.None);

        var result = client.TryComplete(new ResponseEnvelope
        {
            InReplyToRequestId = captured!.RequestId,
            Status = (int)StatusCode.OK,
            Payload = Any.Pack(new TestResponse { Result = "ok" })
        });

        Assert.True(result);
        var response = await callTask;
        Assert.Equal("ok", response.Payload.Unpack<TestResponse>().Result);
    }

    [Fact]
    public async Task TryComplete_WithStatusCode_ThrowsStreamRpcException()
    {
        RequestEnvelope? captured = null;
        var client = new StreamRpcClient(
            async (env, ct) => { captured = env; },
            defaultTimeout: _defaultTimeout);

        var callTask = client.CallAsync(new TestRequest { Value = "test" }, null, CancellationToken.None);

        client.TryComplete(new ResponseEnvelope
        {
            InReplyToRequestId = captured!.RequestId,
            Status = (int)StatusCode.NotFound,
            Error = "Not found"
        });

        var ex = await Assert.ThrowsAsync<StreamRpcException>(() => callTask);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
        Assert.Equal("Not found", ex.Message);
        Assert.Equal(captured.RequestId, ex.RequestId);
    }

    [Fact]
    public void TryComplete_UnknownRequestId_ReturnsFalse()
    {
        var client = new StreamRpcClient(
            async (env, ct) => { },
            defaultTimeout: _defaultTimeout);

        var result = client.TryComplete(new ResponseEnvelope
        {
            InReplyToRequestId = "unknown-id",
            Status = (int)StatusCode.OK
        });

        Assert.False(result);
    }

    [Fact]
    public async Task CallAsync_Timeout_ThrowsTimeoutException()
    {
        var client = new StreamRpcClient(
            async (env, ct) => { },
            defaultTimeout: _defaultTimeout);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.CallAsync(new TestRequest { Value = "test" },
                timeout: TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task CallAsync_Cancellation_ThrowsOperationCanceledException()
    {
        var client = new StreamRpcClient(
            async (env, ct) => { },
            defaultTimeout: _defaultTimeout);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CallAsync(new TestRequest { Value = "test" }, timeout: TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task CancelAll_CancelsAllPending()
    {
        var client = new StreamRpcClient(
            async (env, ct) => { },
            defaultTimeout: _defaultTimeout);

        var task1 = client.CallAsync(new TestRequest { Value = "1" }, TimeSpan.FromSeconds(30), CancellationToken.None);
        var task2 = client.CallAsync(new TestRequest { Value = "2" }, TimeSpan.FromSeconds(30), CancellationToken.None);

        client.CancelAll();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task2);
    }

    [Fact]
    public async Task Dispose_CancelsAllPending()
    {
        var client = new StreamRpcClient(
            async (env, ct) => { },
            defaultTimeout: _defaultTimeout);

        var task = client.CallAsync(new TestRequest { Value = "test" }, TimeSpan.FromSeconds(30), CancellationToken.None);

        client.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task CallAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var client = new StreamRpcClient(
            async (env, ct) => { },
            defaultTimeout: _defaultTimeout);

        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.CallAsync(new TestRequest { Value = "test" }, null, CancellationToken.None));
    }
}
