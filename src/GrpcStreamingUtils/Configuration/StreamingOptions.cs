using System.ComponentModel.DataAnnotations;

namespace Niarru.GrpcStreamingUtils.Configuration;

public class StreamingOptions
{
    public const string SectionName = "Streaming";

    public int DefaultCommandTimeoutSeconds { get; set; } = 15;

    public int IdleTimeoutSeconds { get; set; } = 30;

    public int PingIntervalSeconds { get; set; } = 10;

    [Range(1, 300)]
    public int InitialReconnectIntervalSeconds { get; set; } = 1;

    [Range(1, 3600)]
    public int MaxReconnectIntervalSeconds { get; set; } = 60;
}
