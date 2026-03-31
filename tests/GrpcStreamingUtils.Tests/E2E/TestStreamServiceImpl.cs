using Grpc.Core;
using GrpcStreamingUtils.Tests.Proto;
using GrpcStreamingUtils.Tests.Rpc;
using Microsoft.Extensions.Logging;
using Niarru.GrpcStreamingUtils.KeepAlive;
using Niarru.GrpcStreamingUtils.Rpc;

namespace GrpcStreamingUtils.Tests.E2E;

public class TestStreamServiceImpl : TestStreamService.TestStreamServiceBase
{
    private readonly GrpcServerFixture _fixture;
    private readonly ILoggerFactory _loggerFactory;

    public TestStreamServiceImpl(GrpcServerFixture fixture, ILoggerFactory loggerFactory)
    {
        _fixture = fixture;
        _loggerFactory = loggerFactory;
    }

    public override async Task BiDirectionalStream(
        IAsyncStreamReader<TestStreamMessage> requestStream,
        IServerStreamWriter<TestStreamMessage> responseStream,
        ServerCallContext context)
    {
        var logger = _loggerFactory.CreateLogger<TestServerConnection>();

        var dispatcher = StreamRpcDispatcher.Create<ITestRpc>(
            _fixture.ServerRpcHandler,
            async (env, ct) =>
            {
                var msg = new TestStreamMessage { RpcResponse = env };
                await responseStream.WriteAsync(msg);
            },
            _loggerFactory.CreateLogger<StreamRpcDispatcher>());

        var connection = new TestServerConnection(
            requestStream,
            responseStream,
            TimeProvider.System,
            context.CancellationToken,
            logger,
            dispatcher,
            pingInterval: _fixture.ServerPingInterval,
            idleTimeout: _fixture.ServerIdleTimeout);

        _fixture.ServerConnections.Add(connection);
        _fixture.Monitor.Register(connection);

        try
        {
            await connection.RunAsync(context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fixture.Monitor.Unregister(connection);
        }
    }
}
