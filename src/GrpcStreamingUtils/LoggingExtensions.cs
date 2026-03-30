using Microsoft.Extensions.Logging;

namespace Niarru.GrpcStreamingUtils;

internal static class LoggingExtensions
{
    public static IDisposable? BeginConnectionScope(this ILogger logger, Guid connectionId)
        => logger.BeginScope(new Dictionary<string, object> { ["ConnectionId"] = connectionId.ToString() });
}
