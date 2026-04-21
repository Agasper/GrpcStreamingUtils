namespace Niarru.GrpcStreamingUtils.Logging;

public class GrpcLoggingConfiguration
{
    public const string SectionName = "NiarruGrpcLogging";

    public bool LogGrpcMessageBody { get; set; } = true;

    public bool SkipDefaultFields { get; set; } = true;
}
