namespace Niarru.GrpcStreamingUtils.Connection;

public sealed record StreamConnectionClosedArgs(CloseReason Reason, Exception? Exception = null);
