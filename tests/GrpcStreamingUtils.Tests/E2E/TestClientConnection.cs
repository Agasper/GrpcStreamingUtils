using Grpc.Core;
using GrpcStreamingUtils.Tests.Proto;
using Microsoft.Extensions.Logging;
using Niarru.GrpcStreamingUtils.Connection;
using Niarru.GrpcStreamingUtils.Rpc;

namespace GrpcStreamingUtils.Tests.E2E;

public class TestClientConnection : ClientStreamConnection<TestStreamMessage, TestStreamMessage>
{
    public StreamRpcClient? RpcClient { get; set; }
    public CloseReason? LastCloseReason { get; private set; }
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action<TestStreamMessage>? OnMessage { get; set; }

    public TestClientConnection(
        AsyncDuplexStreamingCall<TestStreamMessage, TestStreamMessage> stream,
        TimeProvider timeProvider,
        ILogger logger,
        TimeSpan? pingInterval = null,
        TimeSpan? idleTimeout = null)
        : base(stream, timeProvider, logger, pingInterval, idleTimeout)
    {
    }

    protected override async Task OnMessageReceivedAsync(TestStreamMessage message, CancellationToken cancellationToken)
    {
        await base.OnMessageReceivedAsync(message, cancellationToken).ConfigureAwait(false);

        if (message.ContentCase == TestStreamMessage.ContentOneofCase.RpcResponse && RpcClient != null)
        {
            RpcClient.TryComplete(message.RpcResponse);
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
