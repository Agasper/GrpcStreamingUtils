namespace Niarru.GrpcStreamingUtils.Exceptions;

public class StreamNotEstablishedException : InvalidOperationException
{
    public StreamNotEstablishedException()
        : base("Stream not established - cannot send message")
    {
    }

    public StreamNotEstablishedException(string message)
        : base(message)
    {
    }

    public StreamNotEstablishedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
