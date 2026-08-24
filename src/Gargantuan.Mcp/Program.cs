using Gargantuan.Mcp.Contracts;
using Gargantuan.Mcp.Mock;
using Gargantuan.Mcp.Server;
using Gargantuan.Mcp.Studio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

bool AllowStudioLocalWrite = args.Contains("--allow-studio-local-write", StringComparer.Ordinal);
bool AllowProjectWrite = args.Contains("--allow-project-write", StringComparer.Ordinal);
bool AllowDestructiveWrite = args.Contains("--allow-destructive-write", StringComparer.Ordinal);
bool AllowScriptWrite = args.Contains("--allow-script-write", StringComparer.Ordinal);
if (AllowScriptWrite && !AllowProjectWrite)
{
    Console.Error.WriteLine("[MCP:Startup] --allow-script-write requires --allow-project-write.");
    Environment.ExitCode = 2;
    return;
}
string? StudioBridgeDescriptorPath;
try
{
    StudioBridgeDescriptorPath = GetSingleOption(args, "--studio-bridge-descriptor");
}
catch (ArgumentException Exception)
{
    Console.Error.WriteLine($"[MCP:Startup] {Exception.Message}");
    Environment.ExitCode = 2;
    return;
}

StudioSessionClient? StudioClient = null;
IGargantuanAdapter Adapter;
try
{
    if (StudioBridgeDescriptorPath is null)
    {
        Adapter = new MockGargantuanAdapter();
    }
    else
    {
        StudioClient = await StudioSessionClient.CreateAsync(StudioBridgeDescriptorPath);
        Adapter = await StudioGargantuanAdapter.CreateAsync(StudioClient);
    }
}
catch (StudioBridgeException Exception)
{
    if (StudioClient is not null) await StudioClient.DisposeAsync();
    Console.Error.WriteLine($"[MCP:StudioBridge] {Exception.SafeMessage}");
    Environment.ExitCode = 2;
    return;
}
catch (GargantuanAdapterException Exception)
{
    if (StudioClient is not null) await StudioClient.DisposeAsync();
    Console.Error.WriteLine($"[MCP:StudioBridge] {Exception.SafeMessage}");
    Environment.ExitCode = 2;
    return;
}

HostApplicationBuilder Builder = Host.CreateApplicationBuilder(args);
Builder.Logging.ClearProviders();
Builder.Logging.AddConsole(Options => Options.LogToStandardErrorThreshold = LogLevel.Trace);
LocalToolPolicy Policy = new(
    AllowStudioLocalWrite,
    AllowProjectWrite,
    AllowDestructiveWrite,
    AllowScriptWrite);
Builder.Services.AddSingleton<IGargantuanAdapter>(Adapter);
Builder.Services.AddSingleton(Policy);
Builder.Services.AddSingleton<ToolExecutor>();
Builder.Services.AddSingleton<ReadTools>();
Builder.Services.AddSingleton<StudioTools>();
Builder.Services.AddSingleton<ProjectWriteTools>();
Builder.Services.AddSingleton<ScriptReadTools>();

IMcpServerBuilder McpBuilder = Builder.Services.AddMcpServer(Options => Options.ProtocolVersion = "2026-07-28")
    .WithStdioServerTransport();

if (ToolRegistrationPolicy.CanAdvertiseReadTools(Adapter.Descriptor, Policy))
    McpBuilder.WithTools<McpReadTools>();

if (ToolRegistrationPolicy.CanAdvertiseSelectionWrite(Adapter.Descriptor, Policy))
    McpBuilder.WithTools<McpStudioTools>();

if (ToolRegistrationPolicy.CanAdvertiseProjectWrite(Adapter.Descriptor, Policy))
    McpBuilder.WithTools<McpProjectWriteTools>();

if (ToolRegistrationPolicy.CanAdvertiseScriptRead(Adapter.Descriptor, Policy))
    McpBuilder.WithTools<McpScriptReadTools>();

if (ToolRegistrationPolicy.CanAdvertiseScriptWrite(Adapter.Descriptor, Policy))
    McpBuilder.WithTools<McpScriptWriteTools>();

if (ToolRegistrationPolicy.CanAdvertiseDestructiveWrite(Adapter.Descriptor, Policy))
    McpBuilder.WithTools<McpDestructiveWriteTools>();

IHost ServerHost = Builder.Build();
try
{
    await ServerHost.RunAsync();
}
finally
{
    if (StudioClient is not null) await StudioClient.DisposeAsync();
}

static string? GetSingleOption(IReadOnlyList<string> Arguments, string Name)
{
    string? Value = null;
    for (int Index = 0; Index < Arguments.Count; Index++)
    {
        if (!string.Equals(Arguments[Index], Name, StringComparison.Ordinal)) continue;
        if (Value is not null || Index + 1 >= Arguments.Count || string.IsNullOrWhiteSpace(Arguments[Index + 1]) ||
            Arguments[Index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{Name} requires exactly one descriptor path.");
        Value = Arguments[++Index];
    }
    return Value;
}
