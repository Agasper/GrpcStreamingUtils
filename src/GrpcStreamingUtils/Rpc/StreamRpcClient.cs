using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Niarru.GrpcStreamingUtils.Configuration;

namespace Niarru.GrpcStreamingUtils.Rpc;

public sealed class StreamRpcClient : IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseEnvelope>> _pending = new();
    private readonly Func<RequestEnvelope, CancellationToken, Task> _sendFunc;
    private readonly StreamingOptions _options;
    private readonly ILogger? _logger;
    private bool _disposed;

    public StreamRpcClient(
        Func<RequestEnvelope, CancellationToken, Task> sendFunc,
        StreamingOptions options,
        ILogger? logger = null)
    {
        _sendFunc = sendFunc ?? throw new ArgumentNullException(nameof(sendFunc));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public TInterface CreateProxy<TInterface>() where TInterface : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var proxy = DispatchProxy.Create<TInterface, StreamRpcProxy>();
        ((StreamRpcProxy)(object)proxy).Initialize(this);
        return proxy;
    }

    public bool TryComplete(ResponseEnvelope response)
    {
        if (response == null)
            throw new ArgumentNullException(nameof(response));

        if (!_pending.TryRemove(response.InReplyToRequestId, out var tcs))
        {
            _logger?.LogWarning("Received response for unknown requestId: {RequestId}", response.InReplyToRequestId);
            return false;
        }

        if (response.Status != (int)StatusCode.OK)
        {
            tcs.TrySetException(new StreamRpcException(
                (StatusCode)response.Status,
                response.Error ?? "Unknown error",
                response.InReplyToRequestId));
            return true;
        }

        tcs.TrySetResult(response);
        return true;
    }

    public void CancelAll()
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAll();
    }

    internal async Task<ResponseEnvelope> CallAsync(IMessage request, TimeSpan? timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var requestId = Guid.NewGuid().ToString();
        var envelope = new RequestEnvelope
        {
            RequestId = requestId,
            Payload = Any.Pack(request)
        };

        var tcs = new TaskCompletionSource<ResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        try
        {
            await _sendFunc(envelope, ct).ConfigureAwait(false);

            var timeoutValue = timeout ?? TimeSpan.FromSeconds(_options.DefaultCommandTimeoutSeconds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutValue);

            try
            {
                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (tcs.Task.IsCanceled)
            {
                _pending.TryRemove(requestId, out _);
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _pending.TryRemove(requestId, out _);
                var timeoutException = new TimeoutException(
                    $"RPC call timed out after {timeoutValue.TotalSeconds}s (requestId: {requestId})");
                tcs.TrySetException(timeoutException);
                throw timeoutException;
            }
        }
        catch
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }
}
