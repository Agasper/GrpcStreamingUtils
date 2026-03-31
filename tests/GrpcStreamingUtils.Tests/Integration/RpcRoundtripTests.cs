using System.Threading.Channels;
using Grpc.Core;
using GrpcStreamingUtils.Tests.Proto;
using GrpcStreamingUtils.Tests.Rpc;
using Niarru.GrpcStreamingUtils.Rpc;

namespace GrpcStreamingUtils.Tests.Integration;

/// <summary>
/// Interface that uses a message type not registered in the ITestRpc dispatcher,
/// used to exercise the "unknown TypeUrl" path.
/// </summary>
public interface IPingRpc
{
    Task<TestResponse> PingCall(Ping request, CancellationToken ct = default);
}

public class RpcRoundtripTests
{
    private static (StreamRpcClient client, ITestRpc proxy, CancellationTokenSource cts) WireUp(
        TestRpcHandler? handler = null,
        TimeSpan? clientTimeout = null)
    {
        handler ??= new TestRpcHandler();
        var channel = Channel.CreateUnbounded<RequestEnvelope>();

        var client = new StreamRpcClient(
            async (env, ct) => await channel.Writer.WriteAsync(env, ct),
            defaultTimeout: clientTimeout ?? TimeSpan.FromSeconds(5));

        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => client.TryComplete(env));

        var cts = new CancellationTokenSource();

        // Background loop: read from channel, dispatch to handler, response goes back via TryComplete
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var request in channel.Reader.ReadAllAsync(cts.Token))
                {
                    await dispatcher.DispatchAsync(request, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        });

        var proxy = client.CreateProxy<ITestRpc>();
        return (client, proxy, cts);
    }

    [Fact]
    public async Task Echo_Roundtrip_ReturnsExpectedResponse()
    {
        var (client, proxy, cts) = WireUp();
        try
        {
            var response = await proxy.Echo(new TestRequest { Value = "hello" });

            Assert.Equal("echo: hello", response.Result);
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
            cts.Dispose();
        }
    }

    [Fact]
    public async Task FireAndForget_Roundtrip_CompletesSuccessfully()
    {
        var (client, proxy, cts) = WireUp();
        try
        {
            await proxy.FireAndForget(new VoidRequest { Value = "fire" });
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
            cts.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentRequests_AllMatchedByRequestId()
    {
        var (client, proxy, cts) = WireUp();
        try
        {
            var tasks = Enumerable.Range(0, 5)
                .Select(i => proxy.Echo(new TestRequest { Value = $"req-{i}" }))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            var sorted = results.Select(r => r.Result).OrderBy(r => r).ToList();
            var expected = Enumerable.Range(0, 5)
                .Select(i => $"echo: req-{i}")
                .OrderBy(r => r)
                .ToList();

            Assert.Equal(expected, sorted);
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
            cts.Dispose();
        }
    }

    [Fact]
    public async Task HandlerThrowsStreamRpcException_NotFound_ClientGetsNotFound()
    {
        var handler = new TestRpcHandler
        {
            EchoHandler = (_, _) => throw new StreamRpcException(StatusCode.NotFound, "not found")
        };

        var (client, proxy, cts) = WireUp(handler);
        try
        {
            var ex = await Assert.ThrowsAsync<StreamRpcException>(
                () => proxy.Echo(new TestRequest { Value = "test" }));

            Assert.Equal(StatusCode.NotFound, ex.StatusCode);
            Assert.Equal("not found", ex.Message);
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
            cts.Dispose();
        }
    }

    [Fact]
    public async Task HandlerThrowsStreamRpcException_ResourceExhausted_ClientGetsCorrectStatusCode()
    {
        var handler = new TestRpcHandler
        {
            EchoHandler = (_, _) => throw new StreamRpcException(StatusCode.ResourceExhausted, "exhausted")
        };

        var (client, proxy, cts) = WireUp(handler);
        try
        {
            var ex = await Assert.ThrowsAsync<StreamRpcException>(
                () => proxy.Echo(new TestRequest { Value = "test" }));

            Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
            Assert.Equal("exhausted", ex.Message);
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
            cts.Dispose();
        }
    }

    [Fact]
    public async Task HandlerThrowsGenericException_ClientGetsInternal()
    {
        var handler = new TestRpcHandler
        {
            EchoHandler = (_, _) => throw new InvalidOperationException("something broke")
        };

        var (client, proxy, cts) = WireUp(handler);
        try
        {
            var ex = await Assert.ThrowsAsync<StreamRpcException>(
                () => proxy.Echo(new TestRequest { Value = "test" }));

            Assert.Equal(StatusCode.Internal, ex.StatusCode);
            Assert.Equal("something broke", ex.Message);
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
            cts.Dispose();
        }
    }

    [Fact]
    public async Task Timeout_HandlerDelays_ClientGetsTimeoutException()
    {
        var handler = new TestRpcHandler
        {
            EchoHandler = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new TestResponse { Result = "late" };
            }
        };

        var (client, proxy, cts) = WireUp(handler, clientTimeout: TimeSpan.FromMilliseconds(200));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => proxy.Echo(new TestRequest { Value = "slow" }));
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
            cts.Dispose();
        }
    }

    [Fact]
    public async Task UnknownTypeUrl_ClientGetsUnimplemented()
    {
        // Wire up a dispatcher that only handles ITestRpc (TestRequest, VoidRequest).
        // Sending a Ping message (from e2e_service.proto) will not match any handler.
        var channel = Channel.CreateUnbounded<RequestEnvelope>();
        var client = new StreamRpcClient(
            async (env, ct) => await channel.Writer.WriteAsync(env, ct),
            defaultTimeout: TimeSpan.FromSeconds(5));

        var handler = new TestRpcHandler();
        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            handler,
            async (env, ct) => client.TryComplete(env));

        using var cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var request in channel.Reader.ReadAllAsync(cts.Token))
                {
                    await dispatcher.DispatchAsync(request, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        try
        {
            // Ping is a valid proto message but not registered in the ITestRpc dispatcher
            var pingProxy = client.CreateProxy<IPingRpc>();

            var ex = await Assert.ThrowsAsync<StreamRpcException>(
                () => pingProxy.PingCall(new Ping()));

            Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
        }
    }

    [Fact]
    public async Task CancelAll_PendingRequests_GetOperationCanceledException()
    {
        var handler = new TestRpcHandler
        {
            EchoHandler = async (req, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new TestResponse { Result = "never" };
            }
        };

        var (client, proxy, cts) = WireUp(handler);
        try
        {
            var task1 = proxy.Echo(new TestRequest { Value = "a" });
            var task2 = proxy.Echo(new TestRequest { Value = "b" });
            var task3 = proxy.Echo(new TestRequest { Value = "c" });

            // Give requests time to be sent and reach the dispatcher
            await Task.Delay(100);

            client.CancelAll();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task2);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task3);
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
            cts.Dispose();
        }
    }
}
