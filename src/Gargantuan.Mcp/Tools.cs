using System.ComponentModel;
using Gargantuan.Mcp.Contracts;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace Gargantuan.Mcp.Server;

public static class ToolRegistrationPolicy
{
    private static readonly AdapterCapability[] ReadCapabilities =
    [
        AdapterCapability.ProjectInspection,
        AdapterCapability.HierarchyInspection,
        AdapterCapability.SchemaInspection,
        AdapterCapability.SelectionInspection,
    ];

    public static bool CanAdvertiseReadTools(AdapterDescriptor Descriptor, LocalToolPolicy Policy) =>
        Policy.Evaluate(ToolRiskClass.Read) == PolicyDecision.Allow &&
        ReadCapabilities.All(Descriptor.Capabilities.Contains);

    public static bool CanAdvertiseSelectionWrite(AdapterDescriptor Descriptor, LocalToolPolicy Policy) =>
        Policy.Evaluate(ToolRiskClass.StudioLocalWrite) == PolicyDecision.Allow &&
        Descriptor.Capabilities.Contains(AdapterCapability.SelectionWrite);

    public static bool CanAdvertiseProjectWrite(AdapterDescriptor Descriptor, LocalToolPolicy Policy) =>
        Policy.Evaluate(ToolRiskClass.ProjectWrite) == PolicyDecision.Allow &&
        Descriptor.Capabilities.Contains(AdapterCapability.ProjectWrite);

    public static bool CanAdvertiseDestructiveWrite(AdapterDescriptor Descriptor, LocalToolPolicy Policy) =>
        CanAdvertiseProjectWrite(Descriptor, Policy) &&
        Policy.Evaluate(ToolRiskClass.DestructiveWrite) == PolicyDecision.Allow;
}

public sealed class ProjectWriteTools(IGargantuanAdapter Adapter, ToolExecutor Executor, LocalToolPolicy Policy)
{
    private readonly SemaphoreSlim Concurrency = new(McpLimits.MaximumConcurrentWrites, McpLimits.MaximumConcurrentWrites);

    public Task<ToolResponse<ProjectWriteResult>> Create(
        string ClassId,
        string ParentId,
        InitialPropertyWrite[]? InitialProperties = null,
        long? ExpectedRevision = null,
        CancellationToken CancellationToken = default) =>
        ExecuteAsync(Token => Adapter.CreateInstanceAsync(new(
            ClassId, new(ParentId), InitialProperties ?? [], ExpectedRevision), Token), CancellationToken);

    public Task<ToolResponse<ProjectWriteResult>> Delete(
        string ObjectId,
        bool DeleteSubtree,
        long? ExpectedRevision = null,
        CancellationToken CancellationToken = default) =>
        ExecuteAsync(Token => Adapter.DeleteInstanceAsync(new(new(ObjectId), DeleteSubtree, ExpectedRevision), Token),
            CancellationToken, ToolRiskClass.DestructiveWrite);

    public Task<ToolResponse<ProjectWriteResult>> Duplicate(
        string ObjectId,
        long? ExpectedRevision = null,
        CancellationToken CancellationToken = default) =>
        ExecuteAsync(Token => Adapter.DuplicateInstanceAsync(new(new(ObjectId), ExpectedRevision), Token), CancellationToken);

    public Task<ToolResponse<ProjectWriteResult>> Reparent(
        string ObjectId,
        string ParentId,
        long? ExpectedRevision = null,
        CancellationToken CancellationToken = default) =>
        ExecuteAsync(Token => Adapter.ReparentInstanceAsync(new(new(ObjectId), new(ParentId), ExpectedRevision), Token), CancellationToken);

    public Task<ToolResponse<ProjectWriteResult>> SetProperty(
        string ObjectId,
        ProjectPropertyTarget Property,
        ProjectPropertyValue Value,
        long? ExpectedRevision = null,
        CancellationToken CancellationToken = default) =>
        ExecuteAsync(Token => Adapter.SetPropertyAsync(new(new(ObjectId), Property, Value, ExpectedRevision), Token), CancellationToken);

    public Task<ToolResponse<ProjectWriteResult>> Save(long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ExecuteAsync(Token => Adapter.SaveProjectAsync(new(ExpectedRevision), Token), CancellationToken);

    public Task<ToolResponse<ProjectWriteResult>> Undo(long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ExecuteAsync(Token => Adapter.UndoAsync(new(ExpectedRevision), Token), CancellationToken);

    public Task<ToolResponse<ProjectWriteResult>> Redo(long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ExecuteAsync(Token => Adapter.RedoAsync(new(ExpectedRevision), Token), CancellationToken);

    private async Task<ToolResponse<ProjectWriteResult>> ExecuteAsync(
        Func<CancellationToken, Task<ProjectWriteResult>> Operation,
        CancellationToken CancellationToken,
        ToolRiskClass RiskClass = ToolRiskClass.ProjectWrite)
    {
        if (Policy.Evaluate(ToolRiskClass.ProjectWrite) != PolicyDecision.Allow)
            return ToolResponse<ProjectWriteResult>.Fail(
                GargantuanErrorCode.PermissionDenied,
                "Durable project writes are not allowed by server policy.");
        if (RiskClass != ToolRiskClass.ProjectWrite &&
            Policy.Evaluate(RiskClass) != PolicyDecision.Allow)
            return ToolResponse<ProjectWriteResult>.Fail(
                GargantuanErrorCode.PermissionDenied,
                "Destructive project writes are not allowed by server policy.");
        if (!await Concurrency.WaitAsync(0, CancellationToken).ConfigureAwait(false))
            return ToolResponse<ProjectWriteResult>.Fail(
                GargantuanErrorCode.ResourceLimit,
                "Another MCP ProjectWrite operation is already in progress.");
        try
        {
            return await Executor.ExecuteAsync(Operation, CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Concurrency.Release();
        }
    }
}

public sealed class ReadTools(IGargantuanAdapter Adapter, ToolExecutor Executor)
{
    public Task<ToolResponse<ProjectInfo>> GetProjectInfo(CancellationToken CancellationToken)
        => Executor.ExecuteAsync(Adapter.GetProjectInfoAsync, CancellationToken);

    public Task<ToolResponse<PagedResult<InstanceSummary>>> ListInstances(string? ParentId = null, int RecursiveDepth = 1, string? ClassFilter = null, string? NameQuery = null, string? PageToken = null, int PageSize = McpLimits.DefaultPageSize, CancellationToken CancellationToken = default)
        => Executor.ExecuteAsync(Token => Adapter.ListInstancesAsync(new(ParentId is null ? null : new(ParentId), RecursiveDepth, ClassFilter, NameQuery, PageToken, PageSize), Token), CancellationToken);

    public Task<ToolResponse<InstanceDetails>> GetInstance(string Id, CancellationToken CancellationToken = default)
        => Executor.ExecuteAsync(Token => Adapter.GetInstanceAsync(new(Id), Token), CancellationToken);

    public Task<ToolResponse<PagedResult<InstanceSummary>>> GetChildren(string ParentId, string? PageToken = null, int PageSize = McpLimits.DefaultPageSize, CancellationToken CancellationToken = default)
        => Executor.ExecuteAsync(Token => Adapter.GetChildrenAsync(new(new(ParentId), PageToken, PageSize), Token), CancellationToken);

    public Task<ToolResponse<PagedResult<ClassSummary>>> ListClasses(string? PageToken = null, int PageSize = McpLimits.DefaultPageSize, CancellationToken CancellationToken = default)
        => Executor.ExecuteAsync(Token => Adapter.ListClassesAsync(new(PageToken, PageSize), Token), CancellationToken);

    public Task<ToolResponse<ClassDetails>> GetClass(string ClassId, CancellationToken CancellationToken = default)
        => Executor.ExecuteAsync(Token => Adapter.GetClassAsync(ClassId, Token), CancellationToken);

    public Task<ToolResponse<IReadOnlyList<ObjectIdentity>>> GetSelection(CancellationToken CancellationToken)
        => Executor.ExecuteAsync(Adapter.GetSelectionAsync, CancellationToken);
}

public sealed class StudioTools(IGargantuanAdapter Adapter, ToolExecutor Executor, LocalToolPolicy Policy)
{
    public Task<ToolResponse<IReadOnlyList<ObjectIdentity>>> SetSelection(string[] ObjectIds, CancellationToken CancellationToken = default)
    {
        if (Policy.Evaluate(ToolRiskClass.StudioLocalWrite) != PolicyDecision.Allow)
            return Task.FromResult(ToolResponse<IReadOnlyList<ObjectIdentity>>.Fail(GargantuanErrorCode.PermissionDenied, "Studio-local writes are not allowed by server policy."));
        return Executor.ExecuteAsync(Token => Adapter.SetSelectionAsync(ObjectIds.Select(Id => new ObjectIdentity(Id)).ToArray(), Token), CancellationToken);
    }
}

public sealed class McpReadTools(ReadTools Tools)
{
    [McpServerTool(Name = "project.get_info", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ProjectInfo>))]
    [Description("Returns bounded project, root, schema, server-adapter capability, and mock-status metadata.")]
    public async Task<CallToolResult> GetProjectInfo(CancellationToken CancellationToken)
        => ToolExecutor.ToMcpResult(await Tools.GetProjectInfo(CancellationToken));

    [McpServerTool(Name = "project.list_instances", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<PagedResult<InstanceSummary>>))]
    [Description("Lists or searches hierarchy descendants with deterministic ordering, bounded depth, and opaque pagination.")]
    public async Task<CallToolResult> ListInstances(string? ParentId = null, int RecursiveDepth = 1, string? ClassFilter = null, string? NameQuery = null, string? PageToken = null, int PageSize = McpLimits.DefaultPageSize, CancellationToken CancellationToken = default)
        => ToolExecutor.ToMcpResult(await Tools.ListInstances(ParentId, RecursiveDepth, ClassFilter, NameQuery, PageToken, PageSize, CancellationToken));

    [McpServerTool(Name = "instance.get", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<InstanceDetails>))]
    [Description("Returns bounded details for one opaque object identifier.")]
    public async Task<CallToolResult> GetInstance(string Id, CancellationToken CancellationToken = default)
        => ToolExecutor.ToMcpResult(await Tools.GetInstance(Id, CancellationToken));

    [McpServerTool(Name = "instance.get_children", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<PagedResult<InstanceSummary>>))]
    [Description("Returns direct children in deterministic order with opaque pagination.")]
    public async Task<CallToolResult> GetChildren(string ParentId, string? PageToken = null, int PageSize = McpLimits.DefaultPageSize, CancellationToken CancellationToken = default)
        => ToolExecutor.ToMcpResult(await Tools.GetChildren(ParentId, PageToken, PageSize, CancellationToken));

    [McpServerTool(Name = "schema.list_classes", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<PagedResult<ClassSummary>>))]
    [Description("Lists bounded Gargantuan semantic class summaries with opaque pagination.")]
    public async Task<CallToolResult> ListClasses(string? PageToken = null, int PageSize = McpLimits.DefaultPageSize, CancellationToken CancellationToken = default)
        => ToolExecutor.ToMcpResult(await Tools.ListClasses(PageToken, PageSize, CancellationToken));

    [McpServerTool(Name = "schema.get_class", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ClassDetails>))]
    [Description("Returns semantic class inheritance, constructibility, provenance, and property metadata.")]
    public async Task<CallToolResult> GetClass(string ClassId, CancellationToken CancellationToken = default)
        => ToolExecutor.ToMcpResult(await Tools.GetClass(ClassId, CancellationToken));

    [McpServerTool(Name = "studio.get_selection", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<IReadOnlyList<ObjectIdentity>>))]
    [Description("Returns the current bounded Studio-local selection as opaque object identifiers.")]
    public async Task<CallToolResult> GetSelection(CancellationToken CancellationToken)
        => ToolExecutor.ToMcpResult(await Tools.GetSelection(CancellationToken));
}

public sealed class McpStudioTools(StudioTools Tools)
{
    [McpServerTool(Name = "studio.set_selection", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<IReadOnlyList<ObjectIdentity>>))]
    [Description("Changes only mock/Studio-local selection. This does not mutate the project DataModel.")]
    public async Task<CallToolResult> SetSelection(string[] ObjectIds, CancellationToken CancellationToken = default)
        => ToolExecutor.ToMcpResult(await Tools.SetSelection(ObjectIds, CancellationToken));
}

public sealed class McpProjectWriteTools(ProjectWriteTools Tools)
{
    [McpServerTool(Name = "instance.create", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ProjectWriteResult>))]
    [Description("Creates one schema-constructible instance under an opaque parent identity. InitialProperties are validated and committed atomically with creation; script Source is never accepted.")]
    public async Task<CallToolResult> Create(string ClassId, string ParentId, InitialPropertyWrite[]? InitialProperties = null,
        long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ToolExecutor.ToMcpResult(await Tools.Create(ClassId, ParentId, InitialProperties, ExpectedRevision, CancellationToken));

    [McpServerTool(Name = "instance.duplicate", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ProjectWriteResult>))]
    [Description("Duplicates the opaque source and all descendants using Studio's ordinary authoritative duplicate semantics. The duplicate is placed beside its source and receives fresh identities.")]
    public async Task<CallToolResult> Duplicate(string ObjectId,
        long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ToolExecutor.ToMcpResult(await Tools.Duplicate(ObjectId, ExpectedRevision, CancellationToken));

    [McpServerTool(Name = "instance.reparent", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ProjectWriteResult>))]
    [Description("Moves one opaque target beneath one opaque parent in the same current project. Studio rejects cycles, protected targets, stale identities, and incompatible parents.")]
    public async Task<CallToolResult> Reparent(string ObjectId, string ParentId,
        long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ToolExecutor.ToMcpResult(await Tools.Reparent(ObjectId, ParentId, ExpectedRevision, CancellationToken));

    [McpServerTool(Name = "instance.set_property", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ProjectWriteResult>))]
    [Description("Sets one schema-writable native, custom-class, or extension property on an opaque target using the canonical typed value representation. Names, paths, reflection, and script Source are not mutation targets.")]
    public async Task<CallToolResult> SetProperty(string ObjectId, ProjectPropertyTarget Property, ProjectPropertyValue Value,
        long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ToolExecutor.ToMcpResult(await Tools.SetProperty(ObjectId, Property, Value, ExpectedRevision, CancellationToken));

    [McpServerTool(Name = "project.save", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ProjectWriteResult>))]
    [Description("Atomically saves the current authoritative project to its existing Studio destination. No path or Save As destination is accepted.")]
    public async Task<CallToolResult> Save(long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ToolExecutor.ToMcpResult(await Tools.Save(ExpectedRevision, CancellationToken));

    [McpServerTool(Name = "studio.undo", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ProjectWriteResult>))]
    [Description("Undoes the current document session's next authoritative history entry using the same Studio/EditorHost history used by manual commands.")]
    public async Task<CallToolResult> Undo(long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ToolExecutor.ToMcpResult(await Tools.Undo(ExpectedRevision, CancellationToken));

    [McpServerTool(Name = "studio.redo", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ProjectWriteResult>))]
    [Description("Redoes the current document session's next authoritative history entry using the same Studio/EditorHost history used by manual commands.")]
    public async Task<CallToolResult> Redo(long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ToolExecutor.ToMcpResult(await Tools.Redo(ExpectedRevision, CancellationToken));
}

public sealed class McpDestructiveWriteTools(ProjectWriteTools Tools)
{
    [McpServerTool(Name = "instance.delete", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<ProjectWriteResult>))]
    [Description("Deletes exactly the opaque target and its full descendant subtree through Studio history. DeleteSubtree must be true to acknowledge descendant deletion; root/protected/stale targets are rejected.")]
    public async Task<CallToolResult> Delete(string ObjectId, bool DeleteSubtree,
        long? ExpectedRevision = null, CancellationToken CancellationToken = default) =>
        ToolExecutor.ToMcpResult(await Tools.Delete(ObjectId, DeleteSubtree, ExpectedRevision, CancellationToken));
}
