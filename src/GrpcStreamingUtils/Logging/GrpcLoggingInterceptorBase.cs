using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Niarru.GrpcStreamingUtils.Logging;

public abstract class GrpcLoggingInterceptorBase : Interceptor
{
    private readonly GrpcLogger _grpcLogger;

    protected GrpcLoggingInterceptorBase(GrpcLogger grpcLogger)
    {
        _grpcLogger = grpcLogger ?? throw new ArgumentNullException(nameof(grpcLogger));
    }

    protected abstract string? GetCallerId(ServerCallContext context);

    protected abstract RpcException CreateRpcException(Exception ex, Guid exceptionId);

    protected virtual bool IsNormalShutdownException(Exception ex) =>
        ex is OperationCanceledException
        || (ex is RpcException rpcEx && rpcEx.StatusCode == StatusCode.Cancelled)
        || (ex is IOException ioEx && ioEx.Message.Contains("The request was aborted", StringComparison.OrdinalIgnoreCase));

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await continuation(request, context).ConfigureAwait(false);
            stopwatch.Stop();
            _grpcLogger.LogUnaryCall(context.Method, request, response, stopwatch.ElapsedMilliseconds,
                GetCallerId(context), context.Peer);
            return response;
        }
        catch (RpcException) { throw; }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var exceptionId = Guid.NewGuid();
            _grpcLogger.LogUnaryError(context.Method, request, ex, exceptionId, stopwatch.ElapsedMilliseconds,
                GetCallerId(context), context.Peer);
            throw CreateRpcException(ex, exceptionId);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var stopwatch = Stopwatch.StartNew();
        _grpcLogger.LogStreamingStart("Server streaming", context.Method, context.Peer);
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
            stopwatch.Stop();
            _grpcLogger.LogStreamingEnd("Server streaming", context.Method, stopwatch.ElapsedMilliseconds,
                GetCallerId(context), context.Peer);
        }
        catch (RpcException) { throw; }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (IsNormalShutdownException(ex)) throw;
            var exceptionId = Guid.NewGuid();
            _grpcLogger.LogStreamingError("Server streaming", context.Method, ex, exceptionId,
                stopwatch.ElapsedMilliseconds, GetCallerId(context), context.Peer);
            throw CreateRpcException(ex, exceptionId);
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var stopwatch = Stopwatch.StartNew();
        _grpcLogger.LogStreamingStart("Client streaming", context.Method, context.Peer);
        try
        {
            var response = await continuation(requestStream, context).ConfigureAwait(false);
            stopwatch.Stop();
            _grpcLogger.LogStreamingEnd("Client streaming", context.Method, stopwatch.ElapsedMilliseconds,
                GetCallerId(context), context.Peer);
            return response;
        }
        catch (RpcException) { throw; }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (IsNormalShutdownException(ex)) throw;
            var exceptionId = Guid.NewGuid();
            _grpcLogger.LogStreamingError("Client streaming", context.Method, ex, exceptionId,
                stopwatch.ElapsedMilliseconds, GetCallerId(context), context.Peer);
            throw CreateRpcException(ex, exceptionId);
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var stopwatch = Stopwatch.StartNew();
        _grpcLogger.LogStreamingStart("Duplex streaming", context.Method, context.Peer);
        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
            stopwatch.Stop();
            _grpcLogger.LogStreamingEnd("Duplex streaming", context.Method, stopwatch.ElapsedMilliseconds,
                GetCallerId(context), context.Peer);
        }
        catch (RpcException) { throw; }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (IsNormalShutdownException(ex)) throw;
            var exceptionId = Guid.NewGuid();
            _grpcLogger.LogStreamingError("Duplex streaming", context.Method, ex, exceptionId,
                stopwatch.ElapsedMilliseconds, GetCallerId(context), context.Peer);
            throw CreateRpcException(ex, exceptionId);
        }
    }
}
