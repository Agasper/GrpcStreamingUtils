using Grpc.Core;

namespace Niarru.GrpcStreamingUtils.Rpc;

public class StreamRpcException : Exception
{
    public StatusCode StatusCode { get; }
    public string? RequestId { get; }

    public StreamRpcException(StatusCode statusCode, string message, string? requestId = null)
        : base(message)
    {
        StatusCode = statusCode;
        RequestId = requestId;
    }

    public StreamRpcException(StatusCode statusCode, string message, Exception innerException, string? requestId = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RequestId = requestId;
    }
}
