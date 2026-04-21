using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Niarru.GrpcStreamingUtils.Logging;

public static class GrpcLoggingSetup
{
    public static IServiceCollection AddNiarruGrpcLogging(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection($"Logging:{GrpcLoggingConfiguration.SectionName}");
        services.Configure<GrpcLoggingConfiguration>(section);

        var config = new GrpcLoggingConfiguration();
        section.Bind(config);
        SensitiveDataRedactor.SkipDefaultFields = config.SkipDefaultFields;

        services.TryAddSingleton<GrpcLogger>();
        services.TryAddSingleton<GrpcClientLoggingInterceptor>();

        return services;
    }
}
