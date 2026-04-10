using Niarru.Logging.Grpc;

namespace Niarru.GrpcStreamingUtils.Connection;

public record StreamConnectionContext(GrpcLogger GrpcLogger, string GrpcMethodName);
