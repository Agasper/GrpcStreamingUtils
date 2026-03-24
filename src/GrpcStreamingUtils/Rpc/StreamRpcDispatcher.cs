using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Niarru.GrpcStreamingUtils.Rpc;

public sealed class StreamRpcDispatcher
{
    private readonly Dictionary<string, MethodEntry> _methods;
    private readonly object _handler;
    private readonly Func<ResponseEnvelope, CancellationToken, Task> _sendFunc;
    private readonly ILogger? _logger;

    private StreamRpcDispatcher(
        Dictionary<string, MethodEntry> methods,
        object handler,
        Func<ResponseEnvelope, CancellationToken, Task> sendFunc,
        ILogger? logger)
    {
        _methods = methods;
        _handler = handler;
        _sendFunc = sendFunc;
        _logger = logger;
    }

    public static StreamRpcDispatcher Create<TInterface>(
        TInterface handler,
        Func<ResponseEnvelope, CancellationToken, Task> sendFunc,
        ILogger? logger = null) where TInterface : class
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (sendFunc == null) throw new ArgumentNullException(nameof(sendFunc));

        var interfaceType = typeof(TInterface);
        if (!interfaceType.IsInterface)
            throw new ArgumentException($"{interfaceType.Name} must be an interface.", nameof(TInterface));

        var methods = new Dictionary<string, MethodEntry>();

        foreach (var method in interfaceType.GetMethods())
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0)
                throw new ArgumentException($"Method '{method.Name}' must have at least one parameter.");

            var requestParamType = parameters[0].ParameterType;
            if (!typeof(IMessage).IsAssignableFrom(requestParamType))
                throw new ArgumentException($"First parameter of '{method.Name}' must implement IMessage.");

            var returnType = method.ReturnType;
            if (returnType != typeof(Task) && !(returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)))
                throw new ArgumentException($"Method '{method.Name}' must return Task or Task<T>.");

            // Instantiate request type to get its descriptor for typeUrl
            var requestInstance = (IMessage)Activator.CreateInstance(requestParamType)!;
            var typeUrl = Any.GetTypeName(Google.Protobuf.WellKnownTypes.Any.Pack(requestInstance).TypeUrl);
            var fullTypeUrl = Google.Protobuf.WellKnownTypes.Any.Pack(requestInstance).TypeUrl;
            var parser = requestInstance.Descriptor.Parser;

            bool hasResponse = returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>);
            Func<Task, object?>? resultExtractor = null;

            if (hasResponse)
            {
                var resultType = returnType.GetGenericArguments()[0];
                var resultProperty = typeof(Task<>).MakeGenericType(resultType).GetProperty("Result")!;
                resultExtractor = task => resultProperty.GetValue(task);
            }

            var entry = new MethodEntry(method, parser, hasResponse, resultExtractor);
            methods[fullTypeUrl] = entry;
        }

        return new StreamRpcDispatcher(methods, handler, sendFunc, logger);
    }

    public async Task DispatchAsync(RequestEnvelope request, CancellationToken ct)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var response = new ResponseEnvelope
        {
            InReplyToRequestId = request.RequestId
        };

        try
        {
            if (request.Payload == null)
            {
                response.Status = (int)StatusCode.InvalidArgument;
                response.Error = "Request payload is null.";
                await _sendFunc(response, ct).ConfigureAwait(false);
                return;
            }

            if (!_methods.TryGetValue(request.Payload.TypeUrl, out var entry))
            {
                response.Status = (int)StatusCode.Unimplemented;
                response.Error = $"No handler for '{request.Payload.TypeUrl}'.";
                await _sendFunc(response, ct).ConfigureAwait(false);
                return;
            }

            IMessage requestMessage;
            try
            {
                requestMessage = entry.RequestParser.ParseFrom(request.Payload.Value);
            }
            catch (Exception ex)
            {
                response.Status = (int)StatusCode.InvalidArgument;
                response.Error = $"Failed to parse payload: {ex.Message}";
                await _sendFunc(response, ct).ConfigureAwait(false);
                return;
            }

            // Build arguments: (IMessage request, CancellationToken ct)
            var methodParams = entry.Method.GetParameters();
            var args = new object?[methodParams.Length];
            args[0] = requestMessage;
            for (int i = 1; i < methodParams.Length; i++)
            {
                if (methodParams[i].ParameterType == typeof(CancellationToken))
                    args[i] = ct;
                else
                    args[i] = methodParams[i].DefaultValue;
            }

            var task = (Task)entry.Method.Invoke(_handler, args)!;
            await task.ConfigureAwait(false);

            response.Status = (int)StatusCode.OK;

            if (entry.HasResponse && entry.ResultExtractor != null)
            {
                var result = entry.ResultExtractor(task);
                if (result is IMessage resultMessage)
                {
                    response.Payload = Any.Pack(resultMessage);
                }
            }
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            HandleException(tie.InnerException, response);
        }
        catch (StreamRpcException ex)
        {
            HandleException(ex, response);
        }
        catch (Exception ex)
        {
            HandleException(ex, response);
        }

        await _sendFunc(response, ct).ConfigureAwait(false);
    }

    private void HandleException(Exception ex, ResponseEnvelope response)
    {
        if (ex is StreamRpcException rpcEx)
        {
            response.Status = (int)rpcEx.StatusCode;
            response.Error = rpcEx.Message;
        }
        else
        {
            _logger?.LogError(ex, "Unhandled exception in RPC handler");
            response.Status = (int)StatusCode.Internal;
            response.Error = ex.Message;
        }
    }

    private sealed record MethodEntry(
        MethodInfo Method,
        MessageParser RequestParser,
        bool HasResponse,
        Func<Task, object?>? ResultExtractor);
}
