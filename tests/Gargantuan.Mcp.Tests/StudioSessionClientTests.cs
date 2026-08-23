using System.Text.Json;
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
                Version = 1,
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
                Version = 1,
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

    private static string CreateTestDirectory()
    {
        string DirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "GargantuanMcp", "ClientTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        return DirectoryPath;
    }
}
