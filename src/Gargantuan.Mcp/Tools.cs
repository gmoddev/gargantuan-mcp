using System.ComponentModel;
using Gargantuan.Mcp.Contracts;
using ModelContextProtocol.Server;

namespace Gargantuan.Mcp.Server;

public sealed class ReadTools(IGargantuanAdapter Adapter, ToolExecutor Executor)
{
    [McpServerTool(Name = "project.get_info", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns bounded project, root, schema, server-adapter capability, and mock-status metadata.")]
    public Task<ToolResponse<ProjectInfo>> GetProjectInfo(CancellationToken CancellationToken)
        => Executor.ExecuteAsync(Adapter.GetProjectInfoAsync, CancellationToken);

    [McpServerTool(Name = "project.list_instances", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists or searches hierarchy descendants with deterministic ordering, bounded depth, and opaque pagination.")]
    public Task<ToolResponse<PagedResult<InstanceSummary>>> ListInstances(string? ParentId = null, int RecursiveDepth = 1, string? ClassFilter = null, string? NameQuery = null, string? PageToken = null, int PageSize = McpLimits.DefaultPageSize, CancellationToken CancellationToken = default)
        => Executor.ExecuteAsync(Token => Adapter.ListInstancesAsync(new(ParentId is null ? null : new(ParentId), RecursiveDepth, ClassFilter, NameQuery, PageToken, PageSize), Token), CancellationToken);

    [McpServerTool(Name = "instance.get", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns bounded details for one opaque object identifier.")]
    public Task<ToolResponse<InstanceDetails>> GetInstance(string Id, CancellationToken CancellationToken = default)
        => Executor.ExecuteAsync(Token => Adapter.GetInstanceAsync(new(Id), Token), CancellationToken);

    [McpServerTool(Name = "instance.get_children", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns direct children in deterministic order with opaque pagination.")]
    public Task<ToolResponse<PagedResult<InstanceSummary>>> GetChildren(string ParentId, string? PageToken = null, int PageSize = McpLimits.DefaultPageSize, CancellationToken CancellationToken = default)
        => Executor.ExecuteAsync(Token => Adapter.GetChildrenAsync(new(new(ParentId), PageToken, PageSize), Token), CancellationToken);

    [McpServerTool(Name = "schema.list_classes", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists bounded Gargantuan semantic class summaries with opaque pagination.")]
    public Task<ToolResponse<PagedResult<ClassSummary>>> ListClasses(string? PageToken = null, int PageSize = McpLimits.DefaultPageSize, CancellationToken CancellationToken = default)
        => Executor.ExecuteAsync(Token => Adapter.ListClassesAsync(new(PageToken, PageSize), Token), CancellationToken);

    [McpServerTool(Name = "schema.get_class", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns semantic class inheritance, constructibility, provenance, and property metadata.")]
    public Task<ToolResponse<ClassDetails>> GetClass(string ClassId, CancellationToken CancellationToken = default)
        => Executor.ExecuteAsync(Token => Adapter.GetClassAsync(ClassId, Token), CancellationToken);

    [McpServerTool(Name = "studio.get_selection", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns the current bounded Studio-local selection as opaque object identifiers.")]
    public Task<ToolResponse<IReadOnlyList<ObjectIdentity>>> GetSelection(CancellationToken CancellationToken)
        => Executor.ExecuteAsync(Adapter.GetSelectionAsync, CancellationToken);
}

public sealed class StudioTools(IGargantuanAdapter Adapter, ToolExecutor Executor, LocalToolPolicy Policy)
{
    [McpServerTool(Name = "studio.set_selection", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Changes only mock/Studio-local selection. This does not mutate the project DataModel.")]
    public Task<ToolResponse<IReadOnlyList<ObjectIdentity>>> SetSelection(string[] ObjectIds, CancellationToken CancellationToken = default)
    {
        if (Policy.Evaluate(ToolRiskClass.StudioLocalWrite) != PolicyDecision.Allow)
            return Task.FromResult(ToolResponse<IReadOnlyList<ObjectIdentity>>.Fail(GargantuanErrorCode.PermissionDenied, "Studio-local writes are not allowed by server policy."));
        return Executor.ExecuteAsync(Token => Adapter.SetSelectionAsync(ObjectIds.Select(Id => new ObjectIdentity(Id)).ToArray(), Token), CancellationToken);
    }
}
