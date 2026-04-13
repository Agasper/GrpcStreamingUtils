using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Niarru.GrpcStreamingUtils.Logging;

public abstract class GrpcClientLoggingInterceptorBase : Interceptor
{
    private readonly GrpcLogger _grpcLogger;

    protected GrpcClientLoggingInterceptorBase(GrpcLogger grpcLogger)
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
        _grpcLogger.LogStreamingStart("Client: server streaming", context.Method.FullName);
        return continuation(request, context);
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var stopwatch = Stopwatch.StartNew();
        var methodName = context.Method.FullName;

        _grpcLogger.LogStreamingStart("Client: client streaming", methodName);

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
        _grpcLogger.LogStreamingStart("Client: duplex streaming", context.Method.FullName);
        return continuation(context);
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
            _grpcLogger.LogUnaryCall(methodName, request, response, stopwatch.ElapsedMilliseconds);
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

            _grpcLogger.LogUnaryError(methodName, request, ex, exceptionId, stopwatch.ElapsedMilliseconds);
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
            _grpcLogger.LogStreamingEnd("Client: client streaming", methodName, stopwatch.ElapsedMilliseconds);
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

            _grpcLogger.LogStreamingError("Client: client streaming", methodName, ex, exceptionId,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
