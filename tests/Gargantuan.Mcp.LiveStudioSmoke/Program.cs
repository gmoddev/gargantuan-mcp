using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

string ServerAssembly = GetRequiredOption(args, "--server");
string DescriptorPath = GetRequiredOption(args, "--descriptor");
bool WaitForInvalidation = args.Contains("--wait-for-invalidation", StringComparer.Ordinal);
bool WrongTokenCheck = args.Contains("--wrong-token-check", StringComparer.Ordinal);

if (WrongTokenCheck)
{
    await VerifyWrongTokenAsync(ServerAssembly, DescriptorPath);
    return;
}

StdioClientTransport Transport = new(new StdioClientTransportOptions
{
    Name = "Gargantuan MCP live Studio smoke",
    Command = "dotnet",
    Arguments =
    [
        ServerAssembly,
        "--studio-bridge-descriptor", DescriptorPath,
        "--allow-studio-local-write",
    ],
    ShutdownTimeout = TimeSpan.FromSeconds(2),
});

await using McpClient Client = await McpClient.CreateAsync(Transport);
Require(Client.NegotiatedProtocolVersion == "2026-07-28", "MCP protocol negotiation failed.");
IList<McpClientTool> Tools = await Client.ListToolsAsync();
string[] Names = Tools.Select(Tool => Tool.Name).Order(StringComparer.Ordinal).ToArray();
string[] ExpectedTools =
[
    "instance.get", "instance.get_children", "project.get_info", "project.list_instances",
    "schema.get_class", "schema.list_classes", "studio.get_selection", "studio.set_selection",
];
Require(Names.SequenceEqual(ExpectedTools), "Live Studio tool discovery did not match capability/policy intersection.");

JsonElement Project = Result(await Client.CallToolAsync("project.get_info", new Dictionary<string, object?>()));
string RootId = Project.GetProperty("RootId").GetProperty("Value").GetString()
    ?? throw new InvalidOperationException("Project root identity is missing.");
JsonElement Instances = Result(await Client.CallToolAsync("project.list_instances", new Dictionary<string, object?>
{
    ["ParentId"] = RootId,
    ["RecursiveDepth"] = 8,
    ["PageSize"] = 50,
}));
_ = Instances.GetProperty("Items");

JsonElement Instance = Result(await Client.CallToolAsync("instance.get", new Dictionary<string, object?> { ["Id"] = RootId }));
Require(Instance.GetProperty("Id").GetProperty("Value").GetString() == RootId, "Instance identity did not round-trip.");

JsonElement Classes = Result(await Client.CallToolAsync("schema.list_classes", new Dictionary<string, object?> { ["PageSize"] = 50 }));
JsonElement ClassItems = Classes.GetProperty("Items");
Require(ClassItems.GetArrayLength() > 0, "Studio schema returned no classes.");
string ClassId = ClassItems[0].GetProperty("Id").GetString()
    ?? throw new InvalidOperationException("Schema class identity is missing.");
JsonElement Class = Result(await Client.CallToolAsync("schema.get_class", new Dictionary<string, object?> { ["ClassId"] = ClassId }));
Require(Class.GetProperty("Id").GetString() == ClassId, "Schema class identity did not round-trip.");

_ = Result(await Client.CallToolAsync("studio.get_selection", new Dictionary<string, object?>()));
JsonElement AcceptedSelection = Result(await Client.CallToolAsync("studio.set_selection", new Dictionary<string, object?>
{
    ["ObjectIds"] = new[] { RootId },
}));
Require(AcceptedSelection.GetArrayLength() == 1 &&
    AcceptedSelection[0].GetProperty("Value").GetString() == RootId,
    "Studio selection write was not accepted through SelectionService.");

Console.WriteLine($"LIVE_STUDIO_SMOKE_OK Session={Project.GetProperty("ProjectId").GetString()} Tools={Names.Length} Classes={ClassItems.GetArrayLength()}");

if (WaitForInvalidation)
{
    Console.WriteLine("LIVE_STUDIO_SMOKE_READY_FOR_INVALIDATION");
    _ = await Console.In.ReadLineAsync();
    var Unavailable = await Client.CallToolAsync("project.get_info", new Dictionary<string, object?>());
    Require(Unavailable.IsError == true && ErrorCode(Unavailable.StructuredContent) == "Unavailable",
        "Studio shutdown/session invalidation did not return bounded Unavailable.");
    Console.WriteLine("LIVE_STUDIO_SMOKE_INVALIDATION_OK");
}

static JsonElement Result(ModelContextProtocol.Protocol.CallToolResult Response)
{
    Require(Response.IsError != true && Response.StructuredContent is { } Content,
        "MCP tool returned an error during live Studio smoke.");
    JsonElement Envelope = Response.StructuredContent!.Value;
    Require(Envelope.GetProperty("Success").GetBoolean(), "MCP tool response was unsuccessful.");
    return Envelope.GetProperty("Result");
}

static async Task VerifyWrongTokenAsync(string ServerAssembly, string DescriptorPath)
{
    await using FileStream DescriptorStream = new(DescriptorPath, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
    JsonObject Descriptor = await JsonNode.ParseAsync(DescriptorStream) as JsonObject
        ?? throw new InvalidOperationException("Studio descriptor was malformed.");
    string OriginalToken = Descriptor["Token"]?.GetValue<string>()
        ?? throw new InvalidOperationException("Studio descriptor token was missing.");
    string WrongToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    Descriptor["Token"] = WrongToken;
    string DerivedPath = Path.Combine(Path.GetDirectoryName(DescriptorPath)!, $"wrong-token-{Guid.NewGuid():N}.json");
    try
    {
        await File.WriteAllTextAsync(DerivedPath, Descriptor.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        ProcessStartInfo StartInfo = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        StartInfo.ArgumentList.Add(ServerAssembly);
        StartInfo.ArgumentList.Add("--studio-bridge-descriptor");
        StartInfo.ArgumentList.Add(DerivedPath);
        using Process Server = Process.Start(StartInfo)
            ?? throw new InvalidOperationException("Wrong-token MCP process did not start.");
        Server.StandardInput.Close();
        using CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            await Server.WaitForExitAsync(Timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Server.Kill(true);
            throw new InvalidOperationException("Wrong-token MCP startup did not fail within its bound.");
        }
        string StandardOutput = await Server.StandardOutput.ReadToEndAsync();
        string StandardError = await Server.StandardError.ReadToEndAsync();
        Require(Server.ExitCode == 2, "Wrong-token MCP startup did not return the stable startup failure code.");
        Require(StandardOutput.Length == 0, "Wrong-token MCP startup polluted protocol stdout.");
        Require(StandardError.Contains("credential was rejected", StringComparison.OrdinalIgnoreCase),
            "Wrong-token MCP startup did not report the safe permission failure.");
        Require(!StandardError.Contains(OriginalToken, StringComparison.Ordinal) &&
            !StandardError.Contains(WrongToken, StringComparison.Ordinal), "Wrong-token MCP startup logged a credential.");
        Console.WriteLine("LIVE_STUDIO_WRONG_TOKEN_OK");
    }
    finally
    {
        if (File.Exists(DerivedPath)) File.Delete(DerivedPath);
    }
}

static string? ErrorCode(JsonElement? StructuredContent)
{
    if (StructuredContent is not { } Content) return null;
    return Content.GetProperty("Error").GetProperty("Code").GetString();
}

static string GetRequiredOption(IReadOnlyList<string> Arguments, string Name)
{
    for (int Index = 0; Index < Arguments.Count - 1; Index++)
        if (Arguments[Index] == Name && !string.IsNullOrWhiteSpace(Arguments[Index + 1])) return Arguments[Index + 1];
    throw new ArgumentException($"{Name} is required.");
}

static void Require(bool Condition, string Message)
{
    if (!Condition) throw new InvalidOperationException(Message);
}
