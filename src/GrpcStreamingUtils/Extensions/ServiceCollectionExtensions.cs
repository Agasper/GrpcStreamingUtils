using Niarru.GrpcStreamingUtils.Configuration;
using Niarru.GrpcStreamingUtils.KeepAlive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Niarru.GrpcStreamingUtils.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStreaming(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StreamingOptions>()
            .BindConfiguration(StreamingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<StreamKeepAliveMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<StreamKeepAliveMonitor>());

        return services;
    }
}
