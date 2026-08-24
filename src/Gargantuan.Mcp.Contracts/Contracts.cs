using System.Text.Json;

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
    public const int MaximumConcurrentWrites = 1;
    public const int MaximumContinuationTokenLength = 256;
    public const int MaximumInitialPropertyCount = 32;
    public const int MaximumPropertyStringBytes = 16 * 1024;
    public const int MaximumProjectWriteRequestBytes = 48 * 1024;
    public const int MaximumWriteDiagnostics = 8;
    public const int MaximumScriptSourceBytes = 64 * 1024;
    public const int MaximumScriptNameBytes = 16 * 1024;
    public const int MaximumScriptWriteRequestBytes = 512 * 1024;
    public const int MaximumScriptDiagnostics = 8;
    public const int MaximumScriptDiagnosticMessageCharacters = 256;
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
    ProjectWrite,
    ScriptInspection,
    ScriptWrite,
}

public enum ToolRiskClass
{
    Read,
    StudioLocalWrite,
    ProjectWrite,
    ScriptWrite,
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
    CommandUnavailable,
    ValidationFailed,
    ResourceLimit,
    Cancelled,
    InternalError,
}

public sealed record ScriptConflictDetails(
    int? CurrentSourceRevision,
    long? CurrentProjectRevision,
    bool LocalStudioEditsConflict,
    string Recommendation);

public sealed record ScriptCommitState(
    bool AuthoritativeCommitConfirmed,
    bool ProjectionUnavailable,
    string Recommendation);

public sealed class GargantuanAdapterException(
    GargantuanErrorCode Code,
    string SafeMessage,
    Exception? InnerException = null,
    ScriptConflictDetails? ConflictDetails = null,
    ScriptCommitState? CommitState = null)
    : Exception(SafeMessage, InnerException)
{
    public GargantuanErrorCode Code { get; } = Code;
    public string SafeMessage { get; } = SafeMessage;
    public ScriptConflictDetails? ConflictDetails { get; } = ConflictDetails;
    public ScriptCommitState? CommitState { get; } = CommitState;
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

/// <summary>
/// Closed MCP representation of a schema-typed property value. Type is one of Null,
/// Bool, Int, Float, Double, String, Vector2, Vector3, Color3, UDim, UDim2,
/// CFrame, EnumItem, SchemaEnum, or ObjectReference. Value carries the scalar,
/// component array, or stable enum item value. Object is used only for an opaque
/// ObjectReference; Enum and SchemaId/DefinitionVersion carry stable enum identity.
/// </summary>
public sealed record ProjectPropertyValue(
    string Type,
    JsonElement? Value = null,
    string? Enum = null,
    string? SchemaId = null,
    uint? DefinitionVersion = null,
    ObjectIdentity? Object = null);

public enum ProjectPropertyKind
{
    Native,
    Custom,
    Extension,
}

public sealed record ProjectPropertyTarget(
    ProjectPropertyKind Kind,
    string Name,
    string? DeclaringSchemaId = null);

public sealed record InitialPropertyWrite(
    ProjectPropertyTarget Property,
    ProjectPropertyValue Value);

public sealed record CreateInstanceRequest(
    string ClassId,
    ObjectIdentity ParentId,
    IReadOnlyList<InitialPropertyWrite> InitialProperties,
    long? ExpectedRevision);

public sealed record DeleteInstanceRequest(
    ObjectIdentity ObjectId,
    bool DeleteSubtree,
    long? ExpectedRevision);

public sealed record DuplicateInstanceRequest(ObjectIdentity ObjectId, long? ExpectedRevision);
public sealed record ReparentInstanceRequest(ObjectIdentity ObjectId, ObjectIdentity ParentId, long? ExpectedRevision);
public sealed record SetPropertyRequest(
    ObjectIdentity ObjectId,
    ProjectPropertyTarget Property,
    ProjectPropertyValue Value,
    long? ExpectedRevision);
public sealed record ProjectRevisionRequest(long? ExpectedRevision);

public sealed record ProjectWriteDiagnostic(string Code, string Message);
public sealed record ProjectWriteResult(
    ObjectIdentity? ObjectId,
    long Revision,
    long PersistedRevision,
    bool Dirty,
    string? HistoryLabel,
    IReadOnlyList<ProjectWriteDiagnostic> Diagnostics);

public sealed record ScriptSourceResult(
    ObjectIdentity ObjectId,
    string ClassName,
    string Source,
    int SourceRevision,
    long ProjectRevision);

public sealed record CreateScriptRequest(
    string ClassId,
    ObjectIdentity ParentId,
    string Name,
    string Source,
    long? ExpectedRevision);

public sealed record SetScriptSourceRequest(
    ObjectIdentity ObjectId,
    string Source,
    int ExpectedSourceRevision,
    long? ExpectedRevision);

public sealed record ScriptDiagnostic(string Code, string Message, int Line, int Column);
public sealed record ScriptWriteResult(
    ObjectIdentity ObjectId,
    string ClassName,
    int SourceRevision,
    long ProjectRevision,
    bool AuthoritativeCommitConfirmed,
    bool LocalEditsConflict,
    IReadOnlyList<ScriptDiagnostic> Diagnostics);

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
    Task<ProjectWriteResult> CreateInstanceAsync(CreateInstanceRequest Request, CancellationToken CancellationToken);
    Task<ProjectWriteResult> DeleteInstanceAsync(DeleteInstanceRequest Request, CancellationToken CancellationToken);
    Task<ProjectWriteResult> DuplicateInstanceAsync(DuplicateInstanceRequest Request, CancellationToken CancellationToken);
    Task<ProjectWriteResult> ReparentInstanceAsync(ReparentInstanceRequest Request, CancellationToken CancellationToken);
    Task<ProjectWriteResult> SetPropertyAsync(SetPropertyRequest Request, CancellationToken CancellationToken);
    Task<ProjectWriteResult> SaveProjectAsync(ProjectRevisionRequest Request, CancellationToken CancellationToken);
    Task<ProjectWriteResult> UndoAsync(ProjectRevisionRequest Request, CancellationToken CancellationToken);
    Task<ProjectWriteResult> RedoAsync(ProjectRevisionRequest Request, CancellationToken CancellationToken);
    Task<ScriptSourceResult> GetScriptSourceAsync(ObjectIdentity ObjectId, CancellationToken CancellationToken);
    Task<ScriptWriteResult> CreateScriptAsync(CreateScriptRequest Request, CancellationToken CancellationToken);
    Task<ScriptWriteResult> SetScriptSourceAsync(SetScriptSourceRequest Request, CancellationToken CancellationToken);
}

public sealed class LocalToolPolicy
{
    private readonly IReadOnlyDictionary<ToolRiskClass, PolicyDecision> Decisions;

    public LocalToolPolicy(bool AllowStudioLocalWrite = false, bool AllowProjectWrite = false,
        bool AllowDestructiveWrite = false, bool AllowScriptWrite = false)
    {
        Decisions = new Dictionary<ToolRiskClass, PolicyDecision>
        {
            [ToolRiskClass.Read] = PolicyDecision.Allow,
            [ToolRiskClass.StudioLocalWrite] = AllowStudioLocalWrite ? PolicyDecision.Allow : PolicyDecision.RequireApproval,
            [ToolRiskClass.ProjectWrite] = AllowProjectWrite ? PolicyDecision.Allow : PolicyDecision.RequireApproval,
            [ToolRiskClass.ScriptWrite] = AllowScriptWrite ? PolicyDecision.Allow : PolicyDecision.Deny,
            [ToolRiskClass.DestructiveWrite] = AllowDestructiveWrite ? PolicyDecision.Allow : PolicyDecision.Deny,
            [ToolRiskClass.Execution] = PolicyDecision.Deny,
        };
    }

    public PolicyDecision Evaluate(ToolRiskClass RiskClass) => Decisions[RiskClass];
}
