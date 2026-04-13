using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Niarru.GrpcStreamingUtils.Logging;

public class GrpcClientLoggingInterceptor : Interceptor
{
    private readonly GrpcLogger _grpcLogger;

    public GrpcClientLoggingInterceptor(GrpcLogger grpcLogger)
    {
        _grpcLogger = grpcLogger ?? throw new ArgumentNullException(nameof(grpcLogger));
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var stopwatch = Stopwatch.StartNew();
        var call = continuation(request, context);

        return new AsyncUnaryCall<TResponse>(
            HandleUnaryResponse(call.ResponseAsync, request, context.Method.FullName, stopwatch),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        _grpcLogger.LogStreamingStart("Client stream", context.Method.FullName);
        return continuation(request, context);
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var stopwatch = Stopwatch.StartNew();
        var methodName = context.Method.FullName;

        _grpcLogger.LogStreamingStart("Client stream", methodName);

        var call = continuation(context);

        return new AsyncClientStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            HandleClientStreamingResponse(call.ResponseAsync, methodName, stopwatch),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var stopwatch = Stopwatch.StartNew();
        var methodName = context.Method.FullName;

        _grpcLogger.LogStreamingStart("Client stream", methodName);

        var call = continuation(context);

        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            call.ResponseStream,
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            () =>
            {
                stopwatch.Stop();
                _grpcLogger.LogStreamingEnd("Client stream", methodName, stopwatch.ElapsedMilliseconds);
                call.Dispose();
            });
    }

    private async Task<TResponse> HandleUnaryResponse<TRequest, TResponse>(
        Task<TResponse> responseTask,
        TRequest request,
        string methodName,
        Stopwatch stopwatch)
    {
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            stopwatch.Stop();
            _grpcLogger.LogUnaryCall(methodName, request, response, stopwatch.ElapsedMilliseconds, prefix: "Client");
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var exceptionId = (ex is RpcException rpcEx
                               && rpcEx.Trailers.Get("exception-id")?.Value is string exIdStr
                               && Guid.TryParse(exIdStr, out var exId))
                ? exId
                : Guid.NewGuid();

            _grpcLogger.LogUnaryError(methodName, request, ex, exceptionId, stopwatch.ElapsedMilliseconds, prefix: "Client");
            throw;
        }
    }

    private async Task<TResponse> HandleClientStreamingResponse<TResponse>(
        Task<TResponse> responseTask,
        string methodName,
        Stopwatch stopwatch)
    {
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            stopwatch.Stop();
            _grpcLogger.LogStreamingEnd("Client stream", methodName, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var exceptionId = (ex is RpcException rpcEx
                               && rpcEx.Trailers.Get("exception-id")?.Value is string exIdStr
                               && Guid.TryParse(exIdStr, out var exId))
                ? exId
                : Guid.NewGuid();

            _grpcLogger.LogStreamingError("Client stream", methodName, ex, exceptionId,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
