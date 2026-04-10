using Microsoft.Extensions.Logging;
using Niarru.Logging;

namespace Niarru.GrpcStreamingUtils;

internal static class LoggingExtensions
{
    public static IDisposable? BeginConnectionScope(this ILogger logger, Guid connectionId)
        => logger.BeginScope(new LoggingScope().AddConnectionId(connectionId.ToString()));
}
