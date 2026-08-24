using System.Text;
using Gargantuan.Mcp.Contracts;

namespace Gargantuan.Mcp.Mock;

public sealed class MockGargantuanAdapter : IGargantuanAdapter
{
    private sealed record MockInstance(ObjectIdentity Id, string Name, string ClassName, ObjectIdentity? ParentId, Dictionary<string, PropertyValue> Properties, Dictionary<string, string> Attributes, List<string> Tags, string? CustomSchemaId);

    private readonly object Sync = new();
    private readonly Dictionary<string, MockInstance> Instances;
    private readonly Dictionary<string, ClassDetails> Classes;
    private List<ObjectIdentity> Selection = [new("gtn_workspace_part")];

    public MockGargantuanAdapter()
    {
        Instances = BuildInstances().ToDictionary(Instance => Instance.Id.Value, StringComparer.Ordinal);
        Classes = BuildClasses().ToDictionary(Class => Class.Id, StringComparer.Ordinal);
    }

    public AdapterDescriptor Descriptor { get; } = new(
        "MockGargantuanAdapter", "1.0.0",
        new HashSet<AdapterCapability>
        {
            AdapterCapability.ProjectInspection, AdapterCapability.HierarchyInspection,
            AdapterCapability.SchemaInspection, AdapterCapability.SelectionInspection,
            AdapterCapability.SelectionWrite,
        }, true);

    public Task<ProjectInfo> GetProjectInfoAsync(CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProjectInfo("mock-project-foundation-1", "Gargantuan MCP Mock Project", new("gtn_root"), "DataModel", 1, "mock-schema-1", Descriptor));
    }

    public Task<PagedResult<InstanceSummary>> ListInstancesAsync(ListInstancesRequest Request, CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        ValidatePage(Request.PageSize, Request.PageToken);
        if (Request.RecursiveDepth is < 0 or > McpLimits.MaximumRecursionDepth)
            throw Error(GargantuanErrorCode.ResourceLimit, $"RecursiveDepth must be between 0 and {McpLimits.MaximumRecursionDepth}.");
        if (Request.NameQuery?.Length > McpLimits.MaximumQueryLength)
            throw Error(GargantuanErrorCode.ResourceLimit, $"NameQuery exceeds {McpLimits.MaximumQueryLength} characters.");

        ObjectIdentity ParentId = Request.ParentId ?? new("gtn_root");
        GetKnown(ParentId);
        List<InstanceSummary> Results = [];
        Walk(ParentId, 1, Request.RecursiveDepth, Results);
        IEnumerable<InstanceSummary> Filtered = Results;
        if (!string.IsNullOrWhiteSpace(Request.ClassFilter))
            Filtered = Filtered.Where(Item => string.Equals(Item.ClassName, Request.ClassFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(Request.NameQuery))
            Filtered = Filtered.Where(Item => Item.Name.Contains(Request.NameQuery, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(Page(Filtered.OrderBy(Item => Item.Depth).ThenBy(Item => Item.Id.Value, StringComparer.Ordinal).Take(McpLimits.MaximumSearchResults), Request.PageToken, Request.PageSize, "instances"));
    }

    public Task<InstanceDetails> GetInstanceAsync(ObjectIdentity Id, CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        MockInstance Item = GetKnown(Id);
        return Task.FromResult(new InstanceDetails(Item.Id, Item.Name, Item.ClassName, Item.ParentId,
            Item.Properties.OrderBy(Pair => Pair.Key, StringComparer.Ordinal).Take(McpLimits.MaximumPropertyCount).ToDictionary(),
            Item.Attributes.OrderBy(Pair => Pair.Key, StringComparer.Ordinal).ToDictionary(),
            Item.Tags.Order(StringComparer.Ordinal).ToArray(), Item.CustomSchemaId));
    }

    public Task<PagedResult<InstanceSummary>> GetChildrenAsync(GetChildrenRequest Request, CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        ValidatePage(Request.PageSize, Request.PageToken);
        GetKnown(Request.ParentId);
        IEnumerable<InstanceSummary> Children = Instances.Values.Where(Item => Item.ParentId == Request.ParentId)
            .OrderBy(Item => Item.Id.Value, StringComparer.Ordinal).Select(Item => Summary(Item, 1));
        return Task.FromResult(Page(Children, Request.PageToken, Request.PageSize, $"children:{Request.ParentId.Value}"));
    }

    public Task<PagedResult<ClassSummary>> ListClassesAsync(ListClassesRequest Request, CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        ValidatePage(Request.PageSize, Request.PageToken);
        IEnumerable<ClassSummary> Items = Classes.Values.OrderBy(Item => Item.Id, StringComparer.Ordinal)
            .Select(Item => new ClassSummary(Item.Id, Item.Name, Item.BaseClassId, Item.Constructible, Item.Provenance));
        return Task.FromResult(Page(Items, Request.PageToken, Request.PageSize, "classes"));
    }

    public Task<ClassDetails> GetClassAsync(string ClassId, CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(ClassId) || ClassId.Length > McpLimits.MaximumStringLength)
            throw Error(GargantuanErrorCode.InvalidArgument, "ClassId is invalid.");
        return Task.FromResult(Classes.TryGetValue(ClassId, out ClassDetails? Result)
            ? Result : throw Error(GargantuanErrorCode.NotFound, "The requested class was not found."));
    }

    public Task<IReadOnlyList<ObjectIdentity>> GetSelectionAsync(CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        lock (Sync) return Task.FromResult<IReadOnlyList<ObjectIdentity>>(Selection.ToArray());
    }

    public Task<IReadOnlyList<ObjectIdentity>> SetSelectionAsync(IReadOnlyList<ObjectIdentity> NewSelection, CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (NewSelection.Count > McpLimits.MaximumSelectionSize)
            throw Error(GargantuanErrorCode.ResourceLimit, $"Selection exceeds {McpLimits.MaximumSelectionSize} objects.");
        if (NewSelection.Distinct().Count() != NewSelection.Count)
            throw Error(GargantuanErrorCode.InvalidArgument, "Selection contains duplicate identifiers.");
        foreach (ObjectIdentity Id in NewSelection) GetKnown(Id);
        lock (Sync) Selection = NewSelection.ToList();
        return Task.FromResult<IReadOnlyList<ObjectIdentity>>(NewSelection.ToArray());
    }

    public Task<ProjectWriteResult> CreateInstanceAsync(CreateInstanceRequest Request, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ProjectWriteResult>(CancellationToken);

    public Task<ProjectWriteResult> DeleteInstanceAsync(DeleteInstanceRequest Request, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ProjectWriteResult>(CancellationToken);

    public Task<ProjectWriteResult> DuplicateInstanceAsync(DuplicateInstanceRequest Request, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ProjectWriteResult>(CancellationToken);

    public Task<ProjectWriteResult> ReparentInstanceAsync(ReparentInstanceRequest Request, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ProjectWriteResult>(CancellationToken);

    public Task<ProjectWriteResult> SetPropertyAsync(SetPropertyRequest Request, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ProjectWriteResult>(CancellationToken);

    public Task<ProjectWriteResult> SaveProjectAsync(ProjectRevisionRequest Request, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ProjectWriteResult>(CancellationToken);

    public Task<ProjectWriteResult> UndoAsync(ProjectRevisionRequest Request, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ProjectWriteResult>(CancellationToken);

    public Task<ProjectWriteResult> RedoAsync(ProjectRevisionRequest Request, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ProjectWriteResult>(CancellationToken);

    public Task<ScriptSourceResult> GetScriptSourceAsync(ObjectIdentity ObjectId, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ScriptSourceResult>(CancellationToken);

    public Task<ScriptWriteResult> CreateScriptAsync(CreateScriptRequest Request, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ScriptWriteResult>(CancellationToken);

    public Task<ScriptWriteResult> SetScriptSourceAsync(SetScriptSourceRequest Request, CancellationToken CancellationToken) =>
        UnsupportedProjectWrite<ScriptWriteResult>(CancellationToken);

    private static Task<T> UnsupportedProjectWrite<T>(CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<T>(Error(
            GargantuanErrorCode.CapabilityUnavailable,
            "The deterministic mock does not provide ProjectWrite."));
    }

    private void Walk(ObjectIdentity ParentId, int Depth, int MaximumDepth, List<InstanceSummary> Results)
    {
        if (Depth > MaximumDepth) return;
        foreach (MockInstance Child in Instances.Values.Where(Item => Item.ParentId == ParentId).OrderBy(Item => Item.Id.Value, StringComparer.Ordinal))
        {
            Results.Add(Summary(Child, Depth));
            Walk(Child.Id, Depth + 1, MaximumDepth, Results);
        }
    }

    private MockInstance GetKnown(ObjectIdentity Id)
    {
        if (string.IsNullOrWhiteSpace(Id.Value) || Id.Value.Length > 128 || !Id.Value.StartsWith("gtn_", StringComparison.Ordinal))
            throw Error(GargantuanErrorCode.InvalidArgument, "Object identifier is invalid.");
        return Instances.TryGetValue(Id.Value, out MockInstance? Item) ? Item : throw Error(GargantuanErrorCode.NotFound, "The requested object was not found.");
    }

    private static InstanceSummary Summary(MockInstance Item, int Depth) => new(Item.Id, Item.Name, Item.ClassName, Item.ParentId, Depth);

    private static void ValidatePage(int PageSize, string? Token)
    {
        if (PageSize is < 1 or > McpLimits.MaximumPageSize)
            throw Error(GargantuanErrorCode.ResourceLimit, $"PageSize must be between 1 and {McpLimits.MaximumPageSize}.");
        if (Token?.Length > McpLimits.MaximumContinuationTokenLength)
            throw Error(GargantuanErrorCode.InvalidArgument, "Continuation token is invalid.");
    }

    private static PagedResult<T> Page<T>(IEnumerable<T> Source, string? Token, int Size, string Scope)
    {
        int Offset = DecodeToken(Token, Scope);
        T[] Window = Source.Skip(Offset).Take(Size + 1).ToArray();
        bool More = Window.Length > Size;
        return new(Window.Take(Size).ToArray(), More ? EncodeToken(Offset + Size, Scope) : null);
    }

    private static string EncodeToken(int Offset, string Scope) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"1:{Scope}:{Offset}"));

    private static int DecodeToken(string? Token, string Scope)
    {
        if (Token is null) return 0;
        try
        {
            string Value = Encoding.UTF8.GetString(Convert.FromBase64String(Token));
            string Prefix = $"1:{Scope}:";
            if (!Value.StartsWith(Prefix, StringComparison.Ordinal) || !int.TryParse(Value[Prefix.Length..], out int Offset) || Offset < 0 || Offset > McpLimits.MaximumSearchResults)
                throw new FormatException();
            return Offset;
        }
        catch (Exception Exception) when (Exception is FormatException or ArgumentException)
        {
            throw Error(GargantuanErrorCode.InvalidArgument, "Continuation token is invalid or stale.");
        }
    }

    private static GargantuanAdapterException Error(GargantuanErrorCode Code, string Message) => new(Code, Message);

    private static IEnumerable<MockInstance> BuildInstances()
    {
        yield return Node("gtn_root", "DataModel", "DataModel", null);
        yield return Node("gtn_workspace", "Workspace", "Workspace", "gtn_root");
        yield return Node("gtn_workspace_part", "Part", "Part", "gtn_workspace", new() { ["Anchored"] = new("Boolean", "true"), ["Material"] = new("Enum:Material", "Plastic") }, new() { ["Health"] = "100" }, ["Gameplay"]);
        yield return Node("gtn_workspace_folder", "Geometry", "Folder", "gtn_workspace");
        yield return Node("gtn_nested_part", "NestedPart", "Part", "gtn_workspace_folder", new() { ["Anchored"] = new("Boolean", "false") });
        yield return Node("gtn_custom", "SpawnMarker", "GameSpawnMarker", "gtn_workspace", new() { ["Team"] = new("String", "Blue") }, new() { ["Weight"] = "2" }, ["Editor", "Spawn"], "custom.game_spawn_marker");
        yield return Node("gtn_shared", "Shared", "Folder", "gtn_root");
        yield return Node("gtn_server", "Server", "Folder", "gtn_root");
    }

    private static MockInstance Node(string Id, string Name, string ClassName, string? ParentId, Dictionary<string, PropertyValue>? Properties = null, Dictionary<string, string>? Attributes = null, List<string>? Tags = null, string? Schema = null)
        => new(new(Id), Name, ClassName, ParentId is null ? null : new(ParentId), Properties ?? [], Attributes ?? [], Tags ?? [], Schema);

    private static IEnumerable<ClassDetails> BuildClasses()
    {
        yield return Class("Instance", null, false, "Native", [], [new("Name", "String", false, null)]);
        yield return Class("DataModel", "Instance", false, "Native", ["Instance"], []);
        yield return Class("Folder", "Instance", true, "Native", ["Instance"], []);
        yield return Class("Workspace", "Instance", false, "Native", ["Instance"], []);
        yield return Class("Part", "Instance", true, "Native", ["Instance"], [new("Anchored", "Boolean", false, null), new("Material", "Enum", false, "Material")]);
        yield return Class("GameSpawnMarker", "Instance", true, "Custom", ["Instance"], [new("Team", "String", false, null)]);
    }

    private static ClassDetails Class(string Id, string? Base, bool Constructible, string Provenance, IReadOnlyList<string> Inheritance, IReadOnlyList<PropertyMetadata> Properties)
        => new(Id, Id, Base, Constructible, Provenance, Inheritance, Properties);
}
