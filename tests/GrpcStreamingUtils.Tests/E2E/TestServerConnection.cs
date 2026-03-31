using Grpc.Core;
using GrpcStreamingUtils.Tests.Proto;
using Microsoft.Extensions.Logging;
using Niarru.GrpcStreamingUtils.Connection;
using Niarru.GrpcStreamingUtils.Rpc;

namespace GrpcStreamingUtils.Tests.E2E;

public class TestServerConnection : ServerStreamConnection<TestStreamMessage, TestStreamMessage>
{
    private readonly StreamRpcDispatcher? _dispatcher;

    public CloseReason? LastCloseReason { get; private set; }
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action<TestStreamMessage>? OnMessage { get; set; }

    public TestServerConnection(
        IAsyncStreamReader<TestStreamMessage> requestStream,
        IServerStreamWriter<TestStreamMessage> responseStream,
        TimeProvider timeProvider,
        CancellationToken grpcCallCancellation,
        ILogger logger,
        StreamRpcDispatcher? dispatcher = null,
        TimeSpan? pingInterval = null,
        TimeSpan? idleTimeout = null)
        : base(requestStream, responseStream, timeProvider, grpcCallCancellation, logger, pingInterval, idleTimeout)
    {
        _dispatcher = dispatcher;
    }

    protected override async Task OnMessageReceivedAsync(TestStreamMessage message, CancellationToken cancellationToken)
    {
        await base.OnMessageReceivedAsync(message, cancellationToken).ConfigureAwait(false);

        if (message.ContentCase == TestStreamMessage.ContentOneofCase.RpcRequest && _dispatcher != null)
        {
            await _dispatcher.DispatchAsync(message.RpcRequest, cancellationToken).ConfigureAwait(false);
        }

        OnMessage?.Invoke(message);
    }

    protected override TestStreamMessage CreatePingMessage()
    {
        return new TestStreamMessage { Ping = new Ping() };
    }

    protected override void OnConnectionClosed(StreamConnectionClosedArgs args)
    {
        LastCloseReason = args.Reason;
        Closed.TrySetResult();
    }
}
