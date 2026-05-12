using ArcGisProBridgeContracts;
using ArcGisProMcpServer.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

await Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    })
    .ConfigureServices(services =>
    {
        var config = BridgeConfiguration.Load();

        services.AddSingleton(config);
        services.AddSingleton(sp => new BridgeClient(
            config.PipeName,
            config,
            sp.GetRequiredService<ILogger<BridgeClient>>()));
        services.AddSingleton(sp => new BridgeInvoker(
            sp.GetRequiredService<BridgeClient>(),
            config,
            new BridgeInvokerOptions(config.Timeouts.DefaultMs, config.Timeouts.MaxMs)));
        services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();
    })
    .RunConsoleAsync();
