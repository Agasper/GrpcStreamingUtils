using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Niarru.GrpcStreamingUtils;
using Niarru.Logging;
using Niarru.GrpcStreamingUtils.Logging;

namespace Niarru.GrpcStreamingUtils.Logging;

public class GrpcLogger
{
    private readonly ILogger<GrpcLogger> _logger;
    private readonly GrpcLoggingConfiguration _config;

    public GrpcLogger(ILogger<GrpcLogger> logger, IOptions<GrpcLoggingConfiguration> config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    // --- Unary ---

    public void LogUnaryCall<TRequest, TResponse>(
        string method, TRequest request, TResponse response, long durationMs,
        string? callerId = null, string? peer = null, string? prefix = null)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        using (_logger.BeginScope(BuildScope(method, durationMs, callerId, peer)))
        {
            if (_config.LogGrpcMessageBody)
            {
                if (prefix != null)
                    _logger.LogDebug("{Prefix} {Method}(): {Request} -> {Response}",
                        prefix, GetMethodShort(method), SensitiveDataRedactor.Redact(request), SensitiveDataRedactor.Redact(response));
                else
                    _logger.LogDebug("{Method}(): {Request} -> {Response}",
                        GetMethodShort(method), SensitiveDataRedactor.Redact(request), SensitiveDataRedactor.Redact(response));
            }
            else
            {
                if (prefix != null)
                    _logger.LogDebug("{Prefix} {Method}()", prefix, GetMethodShort(method));
                else
                    _logger.LogDebug("{Method}()", GetMethodShort(method));
            }
        }
    }

    public void LogUnaryError<TRequest>(
        string method, TRequest request, Exception ex, Guid exceptionId, long durationMs,
        string? callerId = null, string? peer = null, string? prefix = null)
    {
        if (!_logger.IsEnabled(LogLevel.Warning) || ex is OperationCanceledException) return;

        using (_logger.BeginScope(BuildScope(method, durationMs, callerId, peer).AddExceptionId(exceptionId)))
        {
            if (prefix != null)
                _logger.LogError(ex, "{Prefix} {Method}(): ({ExceptionType}) {ExceptionMessage}",
                    prefix, GetMethodShort(method), ex.GetType().Name, ex.Message);
            else
                _logger.LogError(ex, "{Method}(): ({ExceptionType}) {ExceptionMessage}",
                    GetMethodShort(method), ex.GetType().Name, ex.Message);
        }
    }

    // --- Streaming (unified with prefix) ---

    public void LogStreamingStart(string prefix, string method, string? peer = null)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        var scope = new LoggingScope().AddMethod(method);
        if (!string.IsNullOrEmpty(peer))
            scope.AddPeer(peer);

        using (_logger.BeginScope(scope))
            _logger.LogDebug("{Prefix} {Method}() started", prefix, GetMethodShort(method));
    }

    public void LogStreamingEnd(string prefix, string method, long durationMs,
        string? callerId = null, string? peer = null)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        using (_logger.BeginScope(BuildScope(method, durationMs, callerId, peer)))
            _logger.LogDebug("{Prefix} {Method}() ended", prefix, GetMethodShort(method));
    }

    public void LogStreamingError(string prefix, string method, Exception ex, Guid exceptionId,
        long durationMs, string? callerId = null, string? peer = null)
    {
        if (!_logger.IsEnabled(LogLevel.Warning) || ex is OperationCanceledException) return;

        using (_logger.BeginScope(BuildScope(method, durationMs, callerId, peer).AddExceptionId(exceptionId)))
        {
            _logger.LogError(ex, "{Prefix} {Method}(): ({ExceptionType}) {ExceptionMessage}",
                prefix, GetMethodShort(method), ex.GetType().Name, ex.Message);
        }
    }

    // --- Packets ---

    public void LogStreamPacketSent<T>(string method, T message)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        using (_logger.BeginScope(new LoggingScope().AddMethod(method)))
        {
            if (_config.LogGrpcMessageBody)
                _logger.LogDebug("Streaming packet sent: {Message}", SensitiveDataRedactor.Redact(message));
            else
                _logger.LogDebug("Streaming packet sent: {MessageType}", typeof(T).Name);
        }
    }

    public void LogStreamPacketSent<T>(Guid connectionId, T message)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        using (_logger.BeginConnectionScope(connectionId))
        {
            if (_config.LogGrpcMessageBody)
                _logger.LogDebug("Streaming packet sent: {Message}", SensitiveDataRedactor.Redact(message));
            else
                _logger.LogDebug("Streaming packet sent: {MessageType}", typeof(T).Name);
        }
    }

    public void LogStreamPacketReceived<T>(string method, T message)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        using (_logger.BeginScope(new LoggingScope().AddMethod(method)))
        {
            if (_config.LogGrpcMessageBody)
                _logger.LogDebug("Streaming packet received: {Message}", SensitiveDataRedactor.Redact(message));
            else
                _logger.LogDebug("Streaming packet received: {MessageType}", typeof(T).Name);
        }
    }

    public void LogStreamPacketReceived<T>(Guid connectionId, T message)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        using (_logger.BeginConnectionScope(connectionId))
        {
            if (_config.LogGrpcMessageBody)
                _logger.LogDebug("Streaming packet received: {Message}", SensitiveDataRedactor.Redact(message));
            else
                _logger.LogDebug("Streaming packet received: {MessageType}", typeof(T).Name);
        }
    }

    // --- Helpers ---

    private static LoggingScope BuildScope(string method, long durationMs, string? callerId, string? peer)
    {
        var scope = new LoggingScope().AddMethod(method).AddDuration(durationMs);
        if (!string.IsNullOrEmpty(callerId))
            scope.AddCustom("CallerId", callerId);
        if (!string.IsNullOrEmpty(peer))
            scope.AddPeer(peer);
        return scope;
    }

    private static string GetMethodShort(string methodLong) => methodLong.Split('/').Last();
}
