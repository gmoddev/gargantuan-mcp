using Gargantuan.Mcp.Contracts;
using Gargantuan.Mcp.Mock;
using Gargantuan.Mcp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

bool AllowStudioLocalWrite = args.Contains("--allow-studio-local-write", StringComparer.Ordinal);
HostApplicationBuilder Builder = Host.CreateApplicationBuilder(args);
Builder.Logging.ClearProviders();
Builder.Logging.AddConsole(Options => Options.LogToStandardErrorThreshold = LogLevel.Trace);
MockGargantuanAdapter Adapter = new();
Builder.Services.AddSingleton<IGargantuanAdapter>(Adapter);
Builder.Services.AddSingleton(new LocalToolPolicy(AllowStudioLocalWrite));
Builder.Services.AddSingleton<ToolExecutor>();

IMcpServerBuilder McpBuilder = Builder.Services.AddMcpServer(Options => Options.ProtocolVersion = "2026-07-28")
    .WithStdioServerTransport();

AdapterCapability[] ReadCapabilities = [AdapterCapability.ProjectInspection, AdapterCapability.HierarchyInspection, AdapterCapability.SchemaInspection, AdapterCapability.SelectionInspection];
if (ReadCapabilities.All(Adapter.Descriptor.Capabilities.Contains))
    McpBuilder.WithTools<ReadTools>();

if (AllowStudioLocalWrite && Adapter.Descriptor.Capabilities.Contains(AdapterCapability.SelectionWrite))
    McpBuilder.WithTools<StudioTools>();

await Builder.Build().RunAsync();
