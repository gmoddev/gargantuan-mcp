namespace Gargantuan.Mcp.Contracts;

public static class McpLimits
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;
    public const int MaximumSearchResults = 1_000;
    public const int MaximumRecursionDepth = 8;
    public const int MaximumResponseBytes = 512 * 1024;
    public const int MaximumPropertyCount = 128;
    public const int MaximumStringLength = 4_096;
    public const int MaximumQueryLength = 256;
    public const int MaximumSelectionSize = 128;
    public const int MaximumConcurrentRequests = 8;
    public const int MaximumContinuationTokenLength = 256;
}

public readonly record struct ObjectIdentity(string Value)
{
    public override string ToString() => Value;
}

public enum AdapterCapability
{
    ProjectInspection,
    HierarchyInspection,
    SchemaInspection,
    SelectionInspection,
    SelectionWrite,
}

public enum ToolRiskClass
{
    Read,
    StudioLocalWrite,
    ProjectWrite,
    DestructiveWrite,
    Execution,
}

public enum PolicyDecision
{
    Allow,
    Deny,
    RequireApproval,
}

public enum GargantuanErrorCode
{
    InvalidArgument,
    NotFound,
    Unavailable,
    PermissionDenied,
    Conflict,
    StaleIdentity,
    CapabilityUnavailable,
    ResourceLimit,
    InternalError,
}

public sealed class GargantuanAdapterException(GargantuanErrorCode Code, string SafeMessage, Exception? InnerException = null)
    : Exception(SafeMessage, InnerException)
{
    public GargantuanErrorCode Code { get; } = Code;
    public string SafeMessage { get; } = SafeMessage;
}

public sealed record AdapterDescriptor(string Name, string Version, IReadOnlySet<AdapterCapability> Capabilities, bool IsMock);
public sealed record ProjectInfo(string ProjectId, string Name, ObjectIdentity RootId, string RootClassName, long Revision, string SchemaVersion, AdapterDescriptor Adapter);
public sealed record InstanceSummary(ObjectIdentity Id, string Name, string ClassName, ObjectIdentity? ParentId, int Depth);
public sealed record PropertyValue(string Type, string Value);
public sealed record InstanceDetails(ObjectIdentity Id, string Name, string ClassName, ObjectIdentity? ParentId, IReadOnlyDictionary<string, PropertyValue> Properties, IReadOnlyDictionary<string, string> Attributes, IReadOnlyList<string> Tags, string? CustomSchemaId);
public sealed record PropertyMetadata(string Name, string Type, bool ReadOnly, string? EnumId);
public sealed record ClassSummary(string Id, string Name, string? BaseClassId, bool Constructible, string Provenance);
public sealed record ClassDetails(string Id, string Name, string? BaseClassId, bool Constructible, string Provenance, IReadOnlyList<string> Inheritance, IReadOnlyList<PropertyMetadata> Properties);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextPageToken);

public sealed record ListInstancesRequest(ObjectIdentity? ParentId, int RecursiveDepth, string? ClassFilter, string? NameQuery, string? PageToken, int PageSize);
public sealed record GetChildrenRequest(ObjectIdentity ParentId, string? PageToken, int PageSize);
public sealed record ListClassesRequest(string? PageToken, int PageSize);

public interface IGargantuanAdapter
{
    AdapterDescriptor Descriptor { get; }
    Task<ProjectInfo> GetProjectInfoAsync(CancellationToken CancellationToken);
    Task<PagedResult<InstanceSummary>> ListInstancesAsync(ListInstancesRequest Request, CancellationToken CancellationToken);
    Task<InstanceDetails> GetInstanceAsync(ObjectIdentity Id, CancellationToken CancellationToken);
    Task<PagedResult<InstanceSummary>> GetChildrenAsync(GetChildrenRequest Request, CancellationToken CancellationToken);
    Task<PagedResult<ClassSummary>> ListClassesAsync(ListClassesRequest Request, CancellationToken CancellationToken);
    Task<ClassDetails> GetClassAsync(string ClassId, CancellationToken CancellationToken);
    Task<IReadOnlyList<ObjectIdentity>> GetSelectionAsync(CancellationToken CancellationToken);
    Task<IReadOnlyList<ObjectIdentity>> SetSelectionAsync(IReadOnlyList<ObjectIdentity> Selection, CancellationToken CancellationToken);
}

public sealed class LocalToolPolicy
{
    private readonly IReadOnlyDictionary<ToolRiskClass, PolicyDecision> Decisions;

    public LocalToolPolicy(bool AllowStudioLocalWrite = false)
    {
        Decisions = new Dictionary<ToolRiskClass, PolicyDecision>
        {
            [ToolRiskClass.Read] = PolicyDecision.Allow,
            [ToolRiskClass.StudioLocalWrite] = AllowStudioLocalWrite ? PolicyDecision.Allow : PolicyDecision.RequireApproval,
            [ToolRiskClass.ProjectWrite] = PolicyDecision.RequireApproval,
            [ToolRiskClass.DestructiveWrite] = PolicyDecision.Deny,
            [ToolRiskClass.Execution] = PolicyDecision.Deny,
        };
    }

    public PolicyDecision Evaluate(ToolRiskClass RiskClass) => Decisions[RiskClass];
}
