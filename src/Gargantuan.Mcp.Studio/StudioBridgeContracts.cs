namespace Gargantuan.Mcp.Studio;

public enum StudioBridgeCapability
{
    ProjectInspection,
    HierarchyInspection,
    SchemaInspection,
    SelectionInspection,
    SelectionWrite,
}

public enum StudioBridgeErrorCode
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

public sealed class StudioBridgeException(StudioBridgeErrorCode Code, string SafeMessage, Exception? InnerException = null)
    : Exception(SafeMessage, InnerException)
{
    public StudioBridgeErrorCode Code { get; } = Code;
    public string SafeMessage { get; } = SafeMessage;
}

public readonly record struct StudioObjectIdentity(uint Slot, uint Generation)
{
    public bool IsValid => Slot != 0 && Generation != 0;
}

public sealed record StudioSessionDescriptor(
    string SessionId,
    string Name,
    string Version,
    IReadOnlySet<StudioBridgeCapability> Capabilities);

public sealed record StudioProjectInfo(
    string ProjectId,
    string Name,
    StudioObjectIdentity RootId,
    string RootClassName,
    long Revision,
    string SchemaVersion);

public sealed record StudioInstanceSummary(
    StudioObjectIdentity Id,
    string Name,
    string ClassName,
    StudioObjectIdentity? ParentId,
    int Depth);

public sealed record StudioPropertyValue(string Type, string Value);

public sealed record StudioInstanceDetails(
    StudioObjectIdentity Id,
    string Name,
    string ClassName,
    StudioObjectIdentity? ParentId,
    IReadOnlyDictionary<string, StudioPropertyValue> Properties,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<string> Tags,
    string? CustomSchemaId);

public sealed record StudioPropertyMetadata(string Name, string Type, bool ReadOnly, string? EnumId);
public sealed record StudioClassSummary(string Id, string Name, string? BaseClassId, bool Constructible, string Provenance);
public sealed record StudioClassDetails(
    string Id,
    string Name,
    string? BaseClassId,
    bool Constructible,
    string Provenance,
    IReadOnlyList<string> Inheritance,
    IReadOnlyList<StudioPropertyMetadata> Properties);

public sealed record StudioPage<T>(IReadOnlyList<T> Items, ulong SnapshotVersion);

public sealed record StudioListInstancesRequest(
    StudioObjectIdentity? ParentId,
    int RecursiveDepth,
    string? ClassFilter,
    string? NameQuery,
    int Offset,
    int Limit,
    int MaximumCandidates,
    ulong? ExpectedSnapshotVersion);

public sealed record StudioGetChildrenRequest(
    StudioObjectIdentity ParentId,
    int Offset,
    int Limit,
    ulong? ExpectedSnapshotVersion);

public sealed record StudioListClassesRequest(
    int Offset,
    int Limit,
    ulong? ExpectedSnapshotVersion);

/// <summary>
/// A transport-free, Studio-owned view of one negotiated Studio session. Implementations
/// must route through Studio document, schema, selection, command, and capability services;
/// this boundary must never be implemented by direct EditorHost or DataModel access in MCP.
/// </summary>
public interface IStudioSessionClient
{
    Task<StudioSessionDescriptor> DescribeSessionAsync(CancellationToken CancellationToken);
    Task<StudioProjectInfo> GetProjectInfoAsync(CancellationToken CancellationToken);
    Task<StudioPage<StudioInstanceSummary>> ListInstancesAsync(StudioListInstancesRequest Request, CancellationToken CancellationToken);
    Task<StudioInstanceDetails> GetInstanceAsync(StudioObjectIdentity Id, CancellationToken CancellationToken);
    Task<StudioPage<StudioInstanceSummary>> GetChildrenAsync(StudioGetChildrenRequest Request, CancellationToken CancellationToken);
    Task<StudioPage<StudioClassSummary>> ListClassesAsync(StudioListClassesRequest Request, CancellationToken CancellationToken);
    Task<StudioClassDetails> GetClassAsync(string ClassId, CancellationToken CancellationToken);
    Task<IReadOnlyList<StudioObjectIdentity>> GetSelectionAsync(CancellationToken CancellationToken);
    Task<IReadOnlyList<StudioObjectIdentity>> SetSelectionAsync(IReadOnlyList<StudioObjectIdentity> Selection, CancellationToken CancellationToken);
}
