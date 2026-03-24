namespace Niarru.GrpcStreamingUtils.Connection;

public sealed class StreamConnectionClosedArgs
{
    public CloseReason Reason { get; }
    public Exception? Exception { get; }

    public StreamConnectionClosedArgs(CloseReason reason, Exception? exception = null)
    {
        Reason = reason;
        Exception = exception;
    }
}
