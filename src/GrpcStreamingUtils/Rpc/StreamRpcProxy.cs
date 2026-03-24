using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;

namespace Niarru.GrpcStreamingUtils.Rpc;

public class StreamRpcProxy : DispatchProxy
{
    private StreamRpcClient _client = null!;
    private readonly ConcurrentDictionary<MethodInfo, Func<StreamRpcClient, IMessage, CancellationToken, object>> _invokers = new();

    internal void Initialize(StreamRpcClient client)
    {
        _client = client;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            throw new ArgumentNullException(nameof(targetMethod));

        var parameters = targetMethod.GetParameters();
        if (parameters.Length == 0 || !typeof(IMessage).IsAssignableFrom(parameters[0].ParameterType))
            throw new ArgumentException($"First parameter of '{targetMethod.Name}' must implement IMessage.");

        var returnType = targetMethod.ReturnType;
        if (returnType != typeof(Task) && !(returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)))
            throw new NotSupportedException($"Method '{targetMethod.Name}' must return Task or Task<T>.");

        var request = args![0] as IMessage
            ?? throw new ArgumentException($"First argument of '{targetMethod.Name}' must be a non-null IMessage.");

        var ct = CancellationToken.None;
        if (parameters.Length >= 2 && parameters[1].ParameterType == typeof(CancellationToken))
        {
            ct = args[1] is CancellationToken token ? token : CancellationToken.None;
        }

        if (returnType == typeof(Task))
        {
            return CallVoidAsync(request, ct);
        }

        var invoker = _invokers.GetOrAdd(targetMethod, BuildInvoker);
        return invoker(_client, request, ct);
    }

    private static async Task CallVoidAsync(StreamRpcClient client, IMessage request, CancellationToken ct)
    {
        await client.CallAsync(request, timeout: null, ct).ConfigureAwait(false);
    }

    private Task CallVoidAsync(IMessage request, CancellationToken ct)
    {
        return CallVoidAsync(_client, request, ct);
    }

    private static Func<StreamRpcClient, IMessage, CancellationToken, object> BuildInvoker(MethodInfo method)
    {
        var responseType = method.ReturnType.GetGenericArguments()[0];
        var helperMethod = typeof(StreamRpcProxy)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == nameof(CallWithResponseAsync) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(responseType);

        return (client, request, ct) => (object)helperMethod.Invoke(null, [client, request, ct])!;
    }

    private static async Task<TResponse> CallWithResponseAsync<TResponse>(
        StreamRpcClient client, IMessage request, CancellationToken ct) where TResponse : IMessage, new()
    {
        var response = await client.CallAsync(request, timeout: null, ct).ConfigureAwait(false);
        return response.Payload.Unpack<TResponse>();
    }
}
