using System.Collections.Concurrent;
using System.Net;
using Grpc.Net.Client;
using GrpcStreamingUtils.Tests.Rpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Niarru.GrpcStreamingUtils.KeepAlive;

namespace GrpcStreamingUtils.Tests.E2E;

public class GrpcServerFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public ConcurrentBag<TestServerConnection> ServerConnections { get; } = new();
    public TestRpcHandler ServerRpcHandler { get; set; } = new();

    public GrpcChannel Channel { get; private set; } = null!;
    public StreamKeepAliveMonitor Monitor { get; private set; } = null!;

    public TimeSpan? ServerPingInterval { get; set; }
    public TimeSpan? ServerIdleTimeout { get; set; }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<StreamKeepAliveMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamKeepAliveMonitor>());
        builder.Services.AddSingleton(this);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        _app = builder.Build();
        _app.MapGrpcService<TestStreamServiceImpl>();

        await _app.StartAsync().ConfigureAwait(false);

        var server = _app.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>()!;
        var address = addressFeature.Addresses.First();
        Channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true
            }
        });
        Monitor = _app.Services.GetRequiredService<StreamKeepAliveMonitor>();
    }

    public async Task DisposeAsync()
    {
        Channel?.Dispose();

        if (_app != null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void ResetForTest()
    {
        ServerRpcHandler = new TestRpcHandler();
        ServerPingInterval = null;
        ServerIdleTimeout = null;
        while (ServerConnections.TryTake(out _)) { }
    }
}

[CollectionDefinition("GrpcServer")]
public class GrpcServerCollection : ICollectionFixture<GrpcServerFixture>
{
}
