using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;
using Grpc.Core;

namespace Niarru.GrpcStreamingUtils.Rpc;

public class StreamRpcProxy : DispatchProxy
{
    private StreamRpcClient _client = null!;
    private static readonly ConcurrentDictionary<MethodInfo, Func<StreamRpcClient, IMessage, TimeSpan?, CancellationToken, object>> _invokers = new();
    private static readonly ConcurrentDictionary<MethodInfo, MethodMetadata> _metadataCache = new();

    internal void Initialize(StreamRpcClient client)
    {
        _client = client;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            throw new ArgumentNullException(nameof(targetMethod));

        if (_client is null)
            throw new InvalidOperationException("StreamRpcProxy has not been initialized. Use StreamRpcClient.CreateProxy<T>() to create proxies.");

        var metadata = _metadataCache.GetOrAdd(targetMethod, BuildMetadata);

        var request = args![0] as IMessage
            ?? throw new ArgumentException($"First argument of '{targetMethod.Name}' must be a non-null IMessage.");

        var ct = metadata.CancellationTokenIndex >= 0 && args[metadata.CancellationTokenIndex] is CancellationToken token
            ? token
            : CancellationToken.None;

        var timeout = metadata.TimeoutIndex >= 0
            ? args[metadata.TimeoutIndex] as TimeSpan?
            : null;

        if (metadata.IsVoidReturn)
        {
            return CallVoidAsync(_client, request, timeout, ct);
        }

        var invoker = _invokers.GetOrAdd(targetMethod, BuildInvoker);
        return invoker(_client, request, timeout, ct);
    }

    private static MethodMetadata BuildMetadata(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0 || !typeof(IMessage).IsAssignableFrom(parameters[0].ParameterType))
            throw new ArgumentException($"First parameter of '{method.Name}' must implement IMessage.");

        var returnType = method.ReturnType;
        if (returnType != typeof(Task) && !(returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)))
            throw new NotSupportedException($"Method '{method.Name}' must return Task or Task<T>.");

        int ctIndex = -1;
        int timeoutIndex = -1;

        for (int i = 1; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(CancellationToken))
                ctIndex = i;
            else if (parameters[i].ParameterType == typeof(TimeSpan) || parameters[i].ParameterType == typeof(TimeSpan?))
                timeoutIndex = i;
        }

        return new MethodMetadata(returnType == typeof(Task), ctIndex, timeoutIndex);
    }

    private static async Task CallVoidAsync(StreamRpcClient client, IMessage request, TimeSpan? timeout, CancellationToken ct)
    {
        await client.CallAsync(request, timeout, ct).ConfigureAwait(false);
    }

    private static Func<StreamRpcClient, IMessage, TimeSpan?, CancellationToken, object> BuildInvoker(MethodInfo method)
    {
        var responseType = method.ReturnType.GetGenericArguments()[0];
        var factory = typeof(StreamRpcProxy)
            .GetMethod(nameof(CreateTypedInvoker), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(responseType);
        return (Func<StreamRpcClient, IMessage, TimeSpan?, CancellationToken, object>)factory.Invoke(null, null)!;
    }

    private static Func<StreamRpcClient, IMessage, TimeSpan?, CancellationToken, object> CreateTypedInvoker<TResponse>()
        where TResponse : IMessage, new()
    {
        return static (client, request, timeout, ct) => CallWithResponseAsync<TResponse>(client, request, timeout, ct);
    }

    private static async Task<TResponse> CallWithResponseAsync<TResponse>(
        StreamRpcClient client, IMessage request, TimeSpan? timeout, CancellationToken ct) where TResponse : IMessage, new()
    {
        var response = await client.CallAsync(request, timeout, ct).ConfigureAwait(false);
        if (response.Payload == null)
            throw new StreamRpcException(
                StatusCode.Internal,
                $"Server returned OK but no payload for expected response type '{typeof(TResponse).Name}'.");
        return response.Payload.Unpack<TResponse>();
    }

    private sealed record MethodMetadata(bool IsVoidReturn, int CancellationTokenIndex, int TimeoutIndex);
}
