using System.Text.Json;
using System.Diagnostics;
using Gargantuan.Mcp.Studio;

namespace Gargantuan.Mcp.Tests;

public sealed class StudioSessionClientTests
{
    [Fact]
    public async Task DescriptorPathMustBeExplicitAbsoluteAndLocal()
    {
        StudioBridgeException Relative = await Assert.ThrowsAsync<StudioBridgeException>(
            () => StudioSessionClient.CreateAsync("session.json"));
        Assert.Equal(StudioBridgeErrorCode.InvalidArgument, Relative.Code);

        string OutsideLocal = Path.Combine(
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!,
            "gargantuan-mcp-outside-local.json");
        StudioBridgeException Outside = await Assert.ThrowsAsync<StudioBridgeException>(
            () => StudioSessionClient.CreateAsync(OutsideLocal));
        Assert.Equal(StudioBridgeErrorCode.InvalidArgument, Outside.Code);
    }

    [Theory]
    [InlineData("tcp", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("windows-named-pipe", "not-base64")]
    [InlineData("windows-named-pipe", "AQID")]
    public async Task InvalidDescriptorFieldsAreRejected(string Transport, string Token)
    {
        string DirectoryPath = CreateTestDirectory();
        string DescriptorPath = Path.Combine(DirectoryPath, "session.json");
        try
        {
            await File.WriteAllTextAsync(DescriptorPath, JsonSerializer.Serialize(new
            {
                Version = StudioSessionClient.ProtocolVersion,
                Transport,
                PipeName = "GargantuanStudio.Mcp.test",
                SessionId = "gtn-studio-session-test",
                Token,
                ProcessId = 1,
            }));
            StudioBridgeException Error = await Assert.ThrowsAsync<StudioBridgeException>(
                () => StudioSessionClient.CreateAsync(DescriptorPath));
            Assert.Equal(StudioBridgeErrorCode.InvalidArgument, Error.Code);
            Assert.DoesNotContain(Token, Error.SafeMessage, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(DirectoryPath, true);
        }
    }

    [Fact]
    public async Task CallerCancellationStopsPipeConnection()
    {
        string DirectoryPath = CreateTestDirectory();
        string DescriptorPath = Path.Combine(DirectoryPath, "session.json");
        try
        {
            await File.WriteAllTextAsync(DescriptorPath, JsonSerializer.Serialize(new
            {
                Version = StudioSessionClient.ProtocolVersion,
                Transport = "windows-named-pipe",
                PipeName = "GargantuanStudio.Mcp." + Guid.NewGuid().ToString("N"),
                SessionId = "gtn-studio-session-test",
                Token = Convert.ToBase64String(new byte[32]),
                ProcessId = 1,
            }));
            await using StudioSessionClient Client = await StudioSessionClient.CreateAsync(DescriptorPath);
            using CancellationTokenSource Cancellation = new(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client.DescribeSessionAsync(Cancellation.Token));
        }
        finally
        {
            Directory.Delete(DirectoryPath, true);
        }
    }

    [Fact]
    public async Task DescriptorReadRejectsJunctionComponents()
    {
        if (!OperatingSystem.IsWindows()) return;
        string SafeRoot = CreateTestDirectory();
        string OutsideRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "GargantuanMcpOutside", Guid.NewGuid().ToString("N"));
        string LinkPath = Path.Combine(SafeRoot, "link");
        Directory.CreateDirectory(OutsideRoot);
        string OutsideDescriptor = Path.Combine(OutsideRoot, "session.json");
        await File.WriteAllTextAsync(OutsideDescriptor, JsonSerializer.Serialize(new
        {
            Version = StudioSessionClient.ProtocolVersion,
            Transport = "windows-named-pipe",
            PipeName = "GargantuanStudio.Mcp.test",
            SessionId = "gtn-studio-session-test",
            Token = Convert.ToBase64String(new byte[32]),
            ProcessId = 1,
        }));
        try
        {
            CreateJunction(LinkPath, OutsideRoot);
            StudioBridgeException Error = await Assert.ThrowsAsync<StudioBridgeException>(
                () => StudioSessionClient.CreateAsync(Path.Combine(LinkPath, "session.json")));
            Assert.Equal(StudioBridgeErrorCode.InvalidArgument, Error.Code);
        }
        finally
        {
            if (Directory.Exists(LinkPath)) Directory.Delete(LinkPath);
            Directory.Delete(SafeRoot, true);
            Directory.Delete(OutsideRoot, true);
        }
    }

    private static string CreateTestDirectory()
    {
        string DirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "GargantuanMcp", "ClientTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        return DirectoryPath;
    }

    private static void CreateJunction(string LinkPath, string TargetPath)
    {
        ProcessStartInfo Start = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        Start.ArgumentList.Add("/c");
        Start.ArgumentList.Add("mklink");
        Start.ArgumentList.Add("/J");
        Start.ArgumentList.Add(LinkPath);
        Start.ArgumentList.Add(TargetPath);
        using Process ProcessValue = Process.Start(Start) ?? throw new InvalidOperationException("Could not start mklink.");
        ProcessValue.WaitForExit();
        Assert.Equal(0, ProcessValue.ExitCode);
    }
}
