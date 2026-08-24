using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

string ServerAssembly = GetRequiredOption(args, "--server");
string DescriptorPath = GetRequiredOption(args, "--descriptor");
bool WaitForInvalidation = args.Contains("--wait-for-invalidation", StringComparer.Ordinal);
bool WrongTokenCheck = args.Contains("--wrong-token-check", StringComparer.Ordinal);
bool ProjectWrite = args.Contains("--project-write", StringComparer.Ordinal);
bool VerifyPersistence = args.Contains("--verify-project-write-persistence", StringComparer.Ordinal);
string? ProjectPath = GetOptionalOption(args, "--project");

if (WrongTokenCheck)
{
    await VerifyWrongTokenAsync(ServerAssembly, DescriptorPath);
    return;
}

List<string> ServerArguments =
[
    ServerAssembly,
    "--studio-bridge-descriptor", DescriptorPath,
    "--allow-studio-local-write",
];
if (ProjectWrite)
{
    ServerArguments.Add("--allow-project-write");
    ServerArguments.Add("--allow-destructive-write");
}
StdioClientTransport Transport = new(new StdioClientTransportOptions
{
    Name = "Gargantuan MCP live Studio smoke",
    Command = "dotnet",
    Arguments = ServerArguments,
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
if (ProjectWrite)
{
    ExpectedTools =
    [
        .. ExpectedTools,
        "instance.create", "instance.delete", "instance.duplicate", "instance.reparent",
        "instance.set_property", "project.save", "studio.undo", "studio.redo",
    ];
    Array.Sort(ExpectedTools, StringComparer.Ordinal);
}
Require(Names.SequenceEqual(ExpectedTools),
    $"Live Studio tool discovery did not match capability/policy intersection. Expected=[{string.Join(",", ExpectedTools)}] Actual=[{string.Join(",", Names)}]");

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

if (VerifyPersistence)
{
    JsonElement PersistedItems = Instances.GetProperty("Items");
    JsonElement Destination = PersistedItems.EnumerateArray().Single(Item =>
        Item.GetProperty("Name").GetString() == "MCP Persisted Destination");
    JsonElement Source = PersistedItems.EnumerateArray().Single(Item =>
        Item.GetProperty("Name").GetString() == "MCP Persisted Source");
    Require(Source.GetProperty("ParentId").GetProperty("Value").GetString() ==
        Destination.GetProperty("Id").GetProperty("Value").GetString(),
        "Reopened project did not preserve MCP-authored hierarchy state.");
    Console.WriteLine("LIVE_STUDIO_PROJECT_WRITE_PERSISTENCE_OK");
    return;
}

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

if (ProjectWrite)
{
    JsonElement ClassPage = Classes;
    JsonElement? FolderClass = null;
    while (true)
    {
        FolderClass = ClassPage.GetProperty("Items").EnumerateArray().FirstOrDefault(Item =>
            Item.GetProperty("Name").GetString() == "Folder" && Item.GetProperty("Constructible").GetBoolean());
        if (FolderClass is { } Found && Found.ValueKind != JsonValueKind.Undefined) break;
        if (ClassPage.GetProperty("NextPageToken").ValueKind == JsonValueKind.Null) break;
        string PageToken = ClassPage.GetProperty("NextPageToken").GetString()
            ?? throw new InvalidOperationException("Schema continuation token was malformed.");
        ClassPage = Result(await Client.CallToolAsync("schema.list_classes", new Dictionary<string, object?>
        {
            ["PageToken"] = PageToken,
            ["PageSize"] = 50,
        }));
    }
    Require(FolderClass is { ValueKind: not JsonValueKind.Undefined }, "Studio schema has no constructible Folder class.");
    string FolderClassId = FolderClass.Value.GetProperty("Id").GetString()
        ?? throw new InvalidOperationException("Folder class identity is missing.");
    JsonElement Workspace = Instances.GetProperty("Items").EnumerateArray().Single(Item =>
        Item.GetProperty("ClassName").GetString() == "Workspace");
    string WorkspaceId = Workspace.GetProperty("Id").GetProperty("Value").GetString()
        ?? throw new InvalidOperationException("Workspace identity is missing.");
    long Revision = Project.GetProperty("Revision").GetInt64();

    JsonElement Destination = Result(await Client.CallToolAsync("instance.create", new Dictionary<string, object?>
    {
        ["ClassId"] = FolderClassId,
        ["ParentId"] = WorkspaceId,
        ["InitialProperties"] = new[] { InitialName("MCP Destination") },
        ["ExpectedRevision"] = Revision,
    }));
    string DestinationId = WriteObjectId(Destination);
    Revision = WriteRevision(Destination);

    JsonElement Source = Result(await Client.CallToolAsync("instance.create", new Dictionary<string, object?>
    {
        ["ClassId"] = FolderClassId,
        ["ParentId"] = WorkspaceId,
        ["InitialProperties"] = new[] { InitialName("MCP Source") },
        ["ExpectedRevision"] = Revision,
    }));
    string SourceId = WriteObjectId(Source);
    Revision = WriteRevision(Source);

    JsonElement Set = Result(await Client.CallToolAsync("instance.set_property", new Dictionary<string, object?>
    {
        ["ObjectId"] = SourceId,
        ["Property"] = NativeProperty("Name"),
        ["Value"] = StringValue("MCP Persisted Source"),
        ["ExpectedRevision"] = Revision,
    }));
    Revision = WriteRevision(Set);

    JsonElement Reparent = Result(await Client.CallToolAsync("instance.reparent", new Dictionary<string, object?>
    {
        ["ObjectId"] = SourceId,
        ["ParentId"] = DestinationId,
        ["ExpectedRevision"] = Revision,
    }));
    Revision = WriteRevision(Reparent);

    JsonElement Duplicate = Result(await Client.CallToolAsync("instance.duplicate", new Dictionary<string, object?>
    {
        ["ObjectId"] = SourceId,
        ["ExpectedRevision"] = Revision,
    }));
    string DuplicateId = WriteObjectId(Duplicate);
    Revision = WriteRevision(Duplicate);

    JsonElement Delete = Result(await Client.CallToolAsync("instance.delete", new Dictionary<string, object?>
    {
        ["ObjectId"] = DuplicateId,
        ["DeleteSubtree"] = true,
        ["ExpectedRevision"] = Revision,
    }));
    Require(Delete.GetProperty("ObjectId").ValueKind == JsonValueKind.Null,
        "Successful delete returned its now-stale identity as a live result.");
    Revision = WriteRevision(Delete);

    JsonElement Undo = Result(await Client.CallToolAsync("studio.undo", new Dictionary<string, object?>
    {
        ["ExpectedRevision"] = Revision,
    }));
    Revision = WriteRevision(Undo);
    JsonElement Redo = Result(await Client.CallToolAsync("studio.redo", new Dictionary<string, object?>
    {
        ["ExpectedRevision"] = Revision,
    }));
    Revision = WriteRevision(Redo);

    long ObservedRevision = Revision;
    JsonElement ConcurrentEdit = Result(await Client.CallToolAsync("instance.set_property", new Dictionary<string, object?>
    {
        ["ObjectId"] = DestinationId,
        ["Property"] = NativeProperty("Name"),
        ["Value"] = StringValue("MCP Persisted Destination"),
        ["ExpectedRevision"] = ObservedRevision,
    }));
    Revision = WriteRevision(ConcurrentEdit);
    var Conflict = await Client.CallToolAsync("instance.set_property", new Dictionary<string, object?>
    {
        ["ObjectId"] = DestinationId,
        ["Property"] = NativeProperty("Name"),
        ["Value"] = StringValue("MCP Stale Write Must Not Apply"),
        ["ExpectedRevision"] = ObservedRevision,
    });
    Require(Conflict.IsError == true && ErrorCode(Conflict.StructuredContent) == "Conflict",
        "Stale expected_revision did not return Conflict.");
    JsonElement DestinationAfterConflict = Result(await Client.CallToolAsync("instance.get",
        new Dictionary<string, object?> { ["Id"] = DestinationId }));
    Require(DestinationAfterConflict.GetProperty("Name").GetString() == "MCP Persisted Destination",
        "A conflicting MCP write changed the authoritative object.");

    JsonElement Saved = Result(await Client.CallToolAsync("project.save", new Dictionary<string, object?>
    {
        ["ExpectedRevision"] = Revision,
    }));
    Require(!Saved.GetProperty("Dirty").GetBoolean() &&
        Saved.GetProperty("PersistedRevision").GetInt64() == Saved.GetProperty("Revision").GetInt64(),
        "project.save did not return a clean authoritative persisted revision.");
    if (ProjectPath is not null) VerifyNoBridgeStatePersisted(ProjectPath, DescriptorPath);
    Console.WriteLine($"LIVE_STUDIO_PROJECT_WRITE_OK Revision={Saved.GetProperty("Revision").GetInt64()}");
}

Console.WriteLine($"LIVE_STUDIO_SMOKE_OK Session={Project.GetProperty("ProjectId").GetString()} Tools={Names.Length} Classes={ClassItems.GetArrayLength()}");

if (WaitForInvalidation)
{
    Console.WriteLine("LIVE_STUDIO_SMOKE_READY_FOR_INVALIDATION");
    _ = await Console.In.ReadLineAsync();
    var Unavailable = await Client.CallToolAsync("project.get_info", new Dictionary<string, object?>());
    Require(Unavailable.IsError == true && ErrorCode(Unavailable.StructuredContent) == "Unavailable",
        "Studio shutdown/session invalidation did not return bounded Unavailable.");
    if (ProjectWrite)
    {
        var StaleUndo = await Client.CallToolAsync("studio.undo", new Dictionary<string, object?>());
        Require(StaleUndo.IsError == true && ErrorCode(StaleUndo.StructuredContent) == "Unavailable",
            "A stale MCP process could reach Studio history after session invalidation.");
    }
    Console.WriteLine("LIVE_STUDIO_SMOKE_INVALIDATION_OK");
}

static JsonElement Result(ModelContextProtocol.Protocol.CallToolResult Response)
{
    Require(Response.IsError != true && Response.StructuredContent is { } Content,
        $"MCP tool returned an error during live Studio smoke: {ErrorDescription(Response.StructuredContent)}");
    JsonElement Envelope = Response.StructuredContent!.Value;
    Require(Envelope.GetProperty("Success").GetBoolean(), "MCP tool response was unsuccessful.");
    return Envelope.GetProperty("Result");
}

static string ErrorDescription(JsonElement? StructuredContent)
{
    if (StructuredContent is not { } Content) return "no structured error";
    if (!Content.TryGetProperty("Error", out JsonElement Error) || Error.ValueKind == JsonValueKind.Null)
        return "missing structured error";
    string Code = Error.TryGetProperty("Code", out JsonElement CodeNode) ? CodeNode.GetString() ?? "Unknown" : "Unknown";
    string Message = Error.TryGetProperty("Message", out JsonElement MessageNode) ? MessageNode.GetString() ?? string.Empty : string.Empty;
    return $"{Code}: {Message}";
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

static Dictionary<string, object?> InitialName(string Name) => new()
{
    ["Property"] = NativeProperty("Name"),
    ["Value"] = StringValue(Name),
};

static Dictionary<string, object?> NativeProperty(string Name) => new()
{
    ["Kind"] = "Native",
    ["Name"] = Name,
    ["DeclaringSchemaId"] = null,
};

static Dictionary<string, object?> StringValue(string Value) => new()
{
    ["Type"] = "String",
    ["Value"] = Value,
    ["Enum"] = null,
    ["SchemaId"] = null,
    ["DefinitionVersion"] = null,
    ["Object"] = null,
};

static string WriteObjectId(JsonElement Result) =>
    Result.GetProperty("ObjectId").GetProperty("Value").GetString()
    ?? throw new InvalidOperationException("ProjectWrite result identity is missing.");

static long WriteRevision(JsonElement Result) => Result.GetProperty("Revision").GetInt64();

static void VerifyNoBridgeStatePersisted(string ProjectPath, string DescriptorPath)
{
    using FileStream DescriptorStream = new(DescriptorPath, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    JsonObject Descriptor = JsonNode.Parse(DescriptorStream) as JsonObject
        ?? throw new InvalidOperationException("Studio bridge descriptor was malformed.");
    string Token = Descriptor["Token"]?.GetValue<string>()
        ?? throw new InvalidOperationException("Studio bridge descriptor token is missing.");
    string SessionId = Descriptor["SessionId"]?.GetValue<string>()
        ?? throw new InvalidOperationException("Studio bridge session identity is missing.");
    foreach (string Path in Directory.EnumerateFiles(ProjectPath, "*", SearchOption.AllDirectories))
    {
        if (new FileInfo(Path).Length > 1024 * 1024) continue;
        string Text;
        try { Text = File.ReadAllText(Path); }
        catch (DecoderFallbackException) { continue; }
        Require(!Text.Contains(Token, StringComparison.Ordinal) &&
            !Text.Contains(SessionId, StringComparison.Ordinal) &&
            !Text.Contains(DescriptorPath, StringComparison.OrdinalIgnoreCase),
            "Project persistence serialized MCP session or credential state.");
    }
}

static string GetRequiredOption(IReadOnlyList<string> Arguments, string Name)
{
    for (int Index = 0; Index < Arguments.Count - 1; Index++)
        if (Arguments[Index] == Name && !string.IsNullOrWhiteSpace(Arguments[Index + 1])) return Arguments[Index + 1];
    throw new ArgumentException($"{Name} is required.");
}

static string? GetOptionalOption(IReadOnlyList<string> Arguments, string Name)
{
    for (int Index = 0; Index < Arguments.Count - 1; Index++)
        if (Arguments[Index] == Name && !string.IsNullOrWhiteSpace(Arguments[Index + 1])) return Arguments[Index + 1];
    return null;
}

static void Require(bool Condition, string Message)
{
    if (!Condition) throw new InvalidOperationException(Message);
}
