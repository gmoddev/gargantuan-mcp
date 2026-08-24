using System.Text.Json;

namespace Gargantuan.Mcp.Studio;

public enum StudioBridgeCapability
{
    ProjectInspection,
    HierarchyInspection,
    SchemaInspection,
    SelectionInspection,
    SelectionWrite,
    ProjectWrite,
    ScriptInspection,
    ScriptWrite,
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
    CommandUnavailable,
    ValidationFailed,
    ResourceLimit,
    Cancelled,
    InternalError,
}

public sealed record StudioScriptConflictDetails(
    int? CurrentSourceRevision,
    long? CurrentProjectRevision,
    bool LocalStudioEditsConflict,
    string Recommendation);

public sealed record StudioScriptCommitState(
    bool AuthoritativeCommitConfirmed,
    bool ProjectionUnavailable,
    string Recommendation);

public sealed class StudioBridgeException(
    StudioBridgeErrorCode Code,
    string SafeMessage,
    Exception? InnerException = null,
    StudioScriptConflictDetails? ConflictDetails = null,
    StudioScriptCommitState? CommitState = null)
    : Exception(SafeMessage, InnerException)
{
    public StudioBridgeErrorCode Code { get; } = Code;
    public string SafeMessage { get; } = SafeMessage;
    public StudioScriptConflictDetails? ConflictDetails { get; } = ConflictDetails;
    public StudioScriptCommitState? CommitState { get; } = CommitState;
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

public enum StudioProjectPropertyKind
{
    Native,
    Custom,
    Extension,
}

public sealed record StudioProjectPropertyTarget(
    StudioProjectPropertyKind Kind,
    string Name,
    string? DeclaringSchemaId);

public sealed record StudioProjectPropertyValue(
    string Type,
    JsonElement? Value,
    string? Enum,
    string? SchemaId,
    uint? DefinitionVersion,
    StudioObjectIdentity? Object);

public sealed record StudioInitialPropertyWrite(
    StudioProjectPropertyTarget Property,
    StudioProjectPropertyValue Value);

public sealed record StudioCreateInstanceRequest(
    string ClassId,
    StudioObjectIdentity ParentId,
    IReadOnlyList<StudioInitialPropertyWrite> InitialProperties,
    long? ExpectedRevision);

public sealed record StudioDeleteInstanceRequest(
    StudioObjectIdentity ObjectId,
    bool DeleteSubtree,
    long? ExpectedRevision);

public sealed record StudioDuplicateInstanceRequest(StudioObjectIdentity ObjectId, long? ExpectedRevision);
public sealed record StudioReparentInstanceRequest(
    StudioObjectIdentity ObjectId,
    StudioObjectIdentity ParentId,
    long? ExpectedRevision);
public sealed record StudioSetPropertyRequest(
    StudioObjectIdentity ObjectId,
    StudioProjectPropertyTarget Property,
    StudioProjectPropertyValue Value,
    long? ExpectedRevision);
public sealed record StudioProjectRevisionRequest(long? ExpectedRevision);

public sealed record StudioProjectWriteDiagnostic(string Code, string Message);
public sealed record StudioProjectWriteResult(
    StudioObjectIdentity? ObjectId,
    long Revision,
    long PersistedRevision,
    bool Dirty,
    string? HistoryLabel,
    IReadOnlyList<StudioProjectWriteDiagnostic> Diagnostics);

public sealed record StudioScriptSourceResult(
    StudioObjectIdentity ObjectId,
    string ClassName,
    string Source,
    int SourceRevision,
    long ProjectRevision);

public sealed record StudioCreateScriptRequest(
    string ClassId,
    StudioObjectIdentity ParentId,
    string Name,
    string Source,
    long? ExpectedRevision);

public sealed record StudioSetScriptSourceRequest(
    StudioObjectIdentity ObjectId,
    string Source,
    int ExpectedSourceRevision,
    long? ExpectedRevision);

public sealed record StudioScriptDiagnostic(string Code, string Message, int Line, int Column);
public sealed record StudioScriptWriteResult(
    StudioObjectIdentity ObjectId,
    string ClassName,
    int SourceRevision,
    long ProjectRevision,
    bool AuthoritativeCommitConfirmed,
    bool LocalEditsConflict,
    IReadOnlyList<StudioScriptDiagnostic> Diagnostics);

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
    Task<StudioProjectWriteResult> CreateInstanceAsync(StudioCreateInstanceRequest Request, CancellationToken CancellationToken);
    Task<StudioProjectWriteResult> DeleteInstanceAsync(StudioDeleteInstanceRequest Request, CancellationToken CancellationToken);
    Task<StudioProjectWriteResult> DuplicateInstanceAsync(StudioDuplicateInstanceRequest Request, CancellationToken CancellationToken);
    Task<StudioProjectWriteResult> ReparentInstanceAsync(StudioReparentInstanceRequest Request, CancellationToken CancellationToken);
    Task<StudioProjectWriteResult> SetPropertyAsync(StudioSetPropertyRequest Request, CancellationToken CancellationToken);
    Task<StudioProjectWriteResult> SaveProjectAsync(StudioProjectRevisionRequest Request, CancellationToken CancellationToken);
    Task<StudioProjectWriteResult> UndoAsync(StudioProjectRevisionRequest Request, CancellationToken CancellationToken);
    Task<StudioProjectWriteResult> RedoAsync(StudioProjectRevisionRequest Request, CancellationToken CancellationToken);
    Task<StudioScriptSourceResult> GetScriptSourceAsync(StudioObjectIdentity ObjectId, CancellationToken CancellationToken);
    Task<StudioScriptWriteResult> CreateScriptAsync(StudioCreateScriptRequest Request, CancellationToken CancellationToken);
    Task<StudioScriptWriteResult> SetScriptSourceAsync(StudioSetScriptSourceRequest Request, CancellationToken CancellationToken);
}
