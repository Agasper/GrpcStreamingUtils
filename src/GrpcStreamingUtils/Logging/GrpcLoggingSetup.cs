using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Niarru.GrpcStreamingUtils.Logging;

public static class GrpcLoggingSetup
{
    public static IServiceCollection AddNiarruGrpcLogging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GrpcLoggingConfiguration>(
            configuration.GetSection($"Logging:{GrpcLoggingConfiguration.SectionName}"));

        services.TryAddSingleton<GrpcLogger>();
        services.TryAddSingleton<GrpcClientLoggingInterceptor>();

        return services;
    }
}
