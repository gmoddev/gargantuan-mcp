using System.ComponentModel;
using Gargantuan.Mcp.Contracts;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace Gargantuan.Mcp.Server;

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
