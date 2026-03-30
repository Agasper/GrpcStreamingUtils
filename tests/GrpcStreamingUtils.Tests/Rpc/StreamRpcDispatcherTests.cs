using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using GrpcStreamingUtils.Tests.Proto;
using Niarru.GrpcStreamingUtils.Rpc;

namespace GrpcStreamingUtils.Tests.Rpc;

public class TestRpcHandler : ITestRpc
{
    public Func<TestRequest, CancellationToken, Task<TestResponse>>? EchoHandler { get; set; }
    public Func<VoidRequest, CancellationToken, Task>? FireAndForgetHandler { get; set; }

    public Task<TestResponse> Echo(TestRequest request, CancellationToken ct = default)
    {
        return EchoHandler != null
            ? EchoHandler(request, ct)
            : Task.FromResult(new TestResponse { Result = $"echo: {request.Value}" });
    }

    public Task FireAndForget(VoidRequest request, CancellationToken ct = default)
    {
        return FireAndForgetHandler != null
            ? FireAndForgetHandler(request, ct)
            : Task.CompletedTask;
    }
}

public class StreamRpcDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_RoutesToCorrectHandler_ByTypeUrl()
    {
        ResponseEnvelope? captured = null;
        var handler = new TestRpcHandler();
        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => { captured = env; });

        var request = new RequestEnvelope
        {
            RequestId = "req-1",
            Payload = Any.Pack(new TestRequest { Value = "hello" })
        };

        await dispatcher.DispatchAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("req-1", captured!.InReplyToRequestId);
        Assert.Equal((int)StatusCode.OK, captured.Status);

        var result = captured.Payload.Unpack<TestResponse>();
        Assert.Equal("echo: hello", result.Result);
    }

    [Fact]
    public async Task DispatchAsync_Void_ReturnsOK_WithoutPayload()
    {
        ResponseEnvelope? captured = null;
        var handler = new TestRpcHandler();
        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => { captured = env; });

        var request = new RequestEnvelope
        {
            RequestId = "req-2",
            Payload = Any.Pack(new VoidRequest { Value = "fire" })
        };

        await dispatcher.DispatchAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("req-2", captured!.InReplyToRequestId);
        Assert.Equal((int)StatusCode.OK, captured.Status);
        Assert.Null(captured.Payload);
    }

    [Fact]
    public async Task DispatchAsync_HandlerThrowsStreamRpcException_ReturnsCorrectStatus()
    {
        ResponseEnvelope? captured = null;
        var handler = new TestRpcHandler
        {
            EchoHandler = (_, _) => throw new StreamRpcException(StatusCode.NotFound, "Room not found")
        };
        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => { captured = env; });

        var request = new RequestEnvelope
        {
            RequestId = "req-3",
            Payload = Any.Pack(new TestRequest { Value = "test" })
        };

        await dispatcher.DispatchAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal((int)StatusCode.NotFound, captured!.Status);
        Assert.Equal("Room not found", captured.Error);
    }

    [Fact]
    public async Task DispatchAsync_HandlerThrowsGenericException_ReturnsInternal()
    {
        ResponseEnvelope? captured = null;
        var handler = new TestRpcHandler
        {
            EchoHandler = (_, _) => throw new InvalidOperationException("Something broke")
        };
        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => { captured = env; });

        var request = new RequestEnvelope
        {
            RequestId = "req-4",
            Payload = Any.Pack(new TestRequest { Value = "test" })
        };

        await dispatcher.DispatchAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal((int)StatusCode.Internal, captured!.Status);
        Assert.Equal("Something broke", captured.Error);
    }

    [Fact]
    public async Task DispatchAsync_UnknownTypeUrl_ReturnsUnimplemented()
    {
        ResponseEnvelope? captured = null;
        var handler = new TestRpcHandler();
        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => { captured = env; });

        var request = new RequestEnvelope
        {
            RequestId = "req-5",
            Payload = new Any
            {
                TypeUrl = "type.googleapis.com/unknown.Message",
                Value = Google.Protobuf.ByteString.Empty
            }
        };

        await dispatcher.DispatchAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal((int)StatusCode.Unimplemented, captured!.Status);
        Assert.Contains("No handler", captured.Error);
    }

    [Fact]
    public async Task DispatchAsync_PreservesInReplyToRequestId()
    {
        ResponseEnvelope? captured = null;
        var handler = new TestRpcHandler();
        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => { captured = env; });

        var requestId = Guid.NewGuid().ToString();
        var request = new RequestEnvelope
        {
            RequestId = requestId,
            Payload = Any.Pack(new TestRequest { Value = "test" })
        };

        await dispatcher.DispatchAsync(request, CancellationToken.None);

        Assert.Equal(requestId, captured!.InReplyToRequestId);
    }

    [Fact]
    public void Create_InvalidInterface_ThrowsException()
    {
        // Interface with non-IMessage parameter
        Assert.Throws<ArgumentException>(() =>
            StreamRpcDispatcher.Create<IBadRpc>(
                new BadRpcHandler(),
                async (env, ct) => { }));
    }

    [Fact]
    public async Task DispatchAsync_NullPayload_ReturnsInvalidArgument()
    {
        ResponseEnvelope? captured = null;
        var handler = new TestRpcHandler();
        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => { captured = env; });

        var request = new RequestEnvelope
        {
            RequestId = "req-null",
            Payload = null
        };

        await dispatcher.DispatchAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal((int)StatusCode.InvalidArgument, captured!.Status);
        Assert.Contains("null", captured.Error);
    }

    [Fact]
    public async Task DispatchAsync_InvalidPayloadData_ReturnsInvalidArgument()
    {
        ResponseEnvelope? captured = null;
        var handler = new TestRpcHandler();
        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => { captured = env; });

        var request = new RequestEnvelope
        {
            RequestId = "req-bad-parse",
            Payload = new Any
            {
                TypeUrl = Any.Pack(new TestRequest()).TypeUrl,
                Value = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0xFF, 0xFF, 0xFF })
            }
        };

        await dispatcher.DispatchAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal((int)StatusCode.InvalidArgument, captured!.Status);
        Assert.Contains("parse", captured.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_ParameterWithoutDefaultValue_ThrowsException()
    {
        Assert.Throws<ArgumentException>(() =>
            StreamRpcDispatcher.Create<IBadRpcRequiredParam>(
                new BadRpcRequiredParamHandler(),
                async (env, ct) => { }));
    }

    [Fact]
    public void Create_DuplicateRequestType_ThrowsException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            StreamRpcDispatcher.Create<IDuplicateRequestRpc>(
                new DuplicateRequestRpcHandler(),
                async (env, ct) => { }));

        Assert.Contains("Duplicate request type", ex.Message);
        Assert.Contains("TestRequest", ex.Message);
    }

    [Fact]
    public async Task DispatchAsync_ForwardsCancellationToken_ToHandler()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken? receivedCt = null;
        var handler = new TestRpcHandler
        {
            EchoHandler = (req, ct) =>
            {
                receivedCt = ct;
                return Task.FromResult(new TestResponse { Result = "ok" });
            }
        };
        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => { });

        var request = new RequestEnvelope
        {
            RequestId = "req-ct",
            Payload = Any.Pack(new TestRequest { Value = "test" })
        };

        await dispatcher.DispatchAsync(request, cts.Token);

        Assert.NotNull(receivedCt);
        Assert.Equal(cts.Token, receivedCt!.Value);
    }
}

public interface IDuplicateRequestRpc
{
    Task<TestResponse> Method1(TestRequest request, CancellationToken ct = default);
    Task<TestResponse> Method2(TestRequest request, CancellationToken ct = default);
}

public class DuplicateRequestRpcHandler : IDuplicateRequestRpc
{
    public Task<TestResponse> Method1(TestRequest request, CancellationToken ct = default)
        => Task.FromResult(new TestResponse { Result = "1" });
    public Task<TestResponse> Method2(TestRequest request, CancellationToken ct = default)
        => Task.FromResult(new TestResponse { Result = "2" });
}

public interface IBadRpc
{
    Task DoSomething(string notAMessage, CancellationToken ct = default);
}

public class BadRpcHandler : IBadRpc
{
    public Task DoSomething(string notAMessage, CancellationToken ct = default) => Task.CompletedTask;
}

public interface IBadRpcRequiredParam
{
    Task DoSomething(TestRequest request, int requiredParam);
}

public class BadRpcRequiredParamHandler : IBadRpcRequiredParam
{
    public Task DoSomething(TestRequest request, int requiredParam) => Task.CompletedTask;
}
