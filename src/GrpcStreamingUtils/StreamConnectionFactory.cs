using System.Reflection;
using Grpc.Core;
using Niarru.GrpcStreamingUtils.Connection;
using Niarru.Logging.Grpc;

namespace Niarru.GrpcStreamingUtils;

public class StreamConnectionFactory
{
    private static readonly FieldInfo? CallStateField;
    private static readonly FieldInfo? CallbackStateField;

    static StreamConnectionFactory()
    {
        CallStateField = typeof(AsyncDuplexStreamingCall<,>)
            .GetField("callState", BindingFlags.Instance | BindingFlags.NonPublic);

        if (CallStateField != null)
        {
            CallbackStateField = CallStateField.FieldType
                .GetField("callbackState", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }

    private readonly GrpcLogger _grpcLogger;

    public StreamConnectionFactory(GrpcLogger grpcLogger)
    {
        _grpcLogger = grpcLogger ?? throw new ArgumentNullException(nameof(grpcLogger));
    }

    public StreamConnectionContext CreateContext(ServerCallContext callContext)
        => new(_grpcLogger, callContext.Method);

    public StreamConnectionContext CreateContext<TRequest, TResponse>(
        AsyncDuplexStreamingCall<TRequest, TResponse> call)
    {
        var methodName = TryExtractMethodName(call)
            ?? throw new InvalidOperationException(
                "Unable to extract gRPC method name from the call. Use CreateContext(string) overload instead.");
        return new(_grpcLogger, methodName);
    }

    public StreamConnectionContext CreateContext(string grpcMethodName)
        => new(_grpcLogger, grpcMethodName);

    private static string? TryExtractMethodName<TRequest, TResponse>(
        AsyncDuplexStreamingCall<TRequest, TResponse> call)
    {
        if (CallStateField == null || CallbackStateField == null)
            return null;

        var callStateGenericField = call.GetType()
            .GetField("callState", BindingFlags.Instance | BindingFlags.NonPublic);

        if (callStateGenericField == null)
            return null;

        var callState = callStateGenericField.GetValue(call);
        if (callState == null)
            return null;

        var state = CallbackStateField.GetValue(callState);

        if (state is IEnumerable<KeyValuePair<string, object>> enumerable)
        {
            foreach (var entry in enumerable)
            {
                if (entry.Key == "Method" && entry.Value is IMethod method)
                    return method.FullName;
            }
        }

        return null;
    }
}
