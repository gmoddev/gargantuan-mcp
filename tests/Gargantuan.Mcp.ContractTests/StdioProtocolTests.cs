using ModelContextProtocol.Client;
using System.Diagnostics;

namespace Gargantuan.Mcp.ContractTests;

public sealed class StdioProtocolTests
{
    [Fact]
    public async Task ActualServerSupportsDiscoveryInvocationAndCleanShutdown()
    {
        for (int Attempt = 0; Attempt < 3; Attempt++)
        {
            await using McpClient Client = await StartClientAsync([]);
            Assert.Equal("2026-07-28", Client.NegotiatedProtocolVersion);
            IList<McpClientTool> Tools = await Client.ListToolsAsync();
            string[] Names = Tools.Select(Tool => Tool.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal([
                "instance.get", "instance.get_children", "project.get_info", "project.list_instances",
                "schema.get_class", "schema.list_classes", "studio.get_selection"
            ], Names);
            Assert.DoesNotContain("studio.set_selection", Names);

            var Result = await Client.CallToolAsync("project.get_info", new Dictionary<string, object?>());
            Assert.NotEmpty(Result.Content);
            Assert.NotEqual(true, Result.IsError);
            await Assert.ThrowsAnyAsync<Exception>(() => Client.CallToolAsync("unknown.tool", new Dictionary<string, object?>()).AsTask());
            var InvalidResult = await Client.CallToolAsync("schema.list_classes", new Dictionary<string, object?> { ["PageSize"] = 201 });
            Assert.True(InvalidResult.IsError);
            Assert.NotNull(InvalidResult.StructuredContent);
        }
    }

    [Fact]
    public async Task ClosingStdinExitsPromptlyWithoutPollutingStdout()
    {
        string ServerAssembly = Path.Combine(AppContext.BaseDirectory, "Gargantuan.Mcp.dll");
        for (int Attempt = 0; Attempt < 3; Attempt++)
        {
            ProcessStartInfo StartInfo = new("dotnet", $"\"{ServerAssembly}\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process Process = Process.Start(StartInfo)!;
            Process.StandardInput.Close();
            using CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(3));
            await Process.WaitForExitAsync(Timeout.Token);
            Assert.Equal(0, Process.ExitCode);
            Assert.Equal(string.Empty, await Process.StandardOutput.ReadToEndAsync(Timeout.Token));
        }
    }

    [Fact]
    public async Task ExplicitLocalPolicyAdvertisesStudioWrite()
    {
        await using McpClient Client = await StartClientAsync(["--allow-studio-local-write"]);
        IList<McpClientTool> Tools = await Client.ListToolsAsync();
        Assert.Contains(Tools, Tool => Tool.Name == "studio.set_selection");
        var Result = await Client.CallToolAsync("studio.set_selection", new Dictionary<string, object?>
        {
            ["ObjectIds"] = new[] { "gtn_workspace" },
        });
        Assert.NotEqual(true, Result.IsError);
    }

    private static Task<McpClient> StartClientAsync(IList<string> ServerArguments)
    {
        string ServerAssembly = Path.Combine(AppContext.BaseDirectory, "Gargantuan.Mcp.dll");
        Assert.True(File.Exists(ServerAssembly), $"Server assembly missing: {ServerAssembly}");
        List<string> Arguments = [ServerAssembly, .. ServerArguments];
        StdioClientTransport Transport = new(new StdioClientTransportOptions
        {
            Name = "Gargantuan MCP contract server",
            Command = "dotnet",
            Arguments = Arguments,
            ShutdownTimeout = TimeSpan.FromSeconds(1),
        });
        return McpClient.CreateAsync(Transport);
    }
}
