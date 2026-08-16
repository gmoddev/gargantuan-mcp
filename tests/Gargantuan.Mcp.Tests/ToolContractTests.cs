using Gargantuan.Mcp.Contracts;
using Gargantuan.Mcp.Mock;
using Gargantuan.Mcp.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gargantuan.Mcp.Tests;

public sealed class ToolContractTests
{
    private static (MockGargantuanAdapter Adapter, ReadTools Tools) CreateTools()
    {
        MockGargantuanAdapter Adapter = new();
        ToolExecutor Executor = new(NullLogger<ToolExecutor>.Instance);
        return (Adapter, new ReadTools(Adapter, Executor));
    }

    [Fact]
    public async Task EveryReadToolReturnsAValidBoundedResult()
    {
        (MockGargantuanAdapter Adapter, ReadTools Tools) = CreateTools();
        Assert.True((await Tools.GetProjectInfo(default)).Success);
        Assert.True((await Tools.ListInstances(PageSize: 2)).Success);
        Assert.True((await Tools.GetInstance("gtn_workspace_part")).Success);
        Assert.True((await Tools.GetChildren("gtn_workspace", PageSize: 2)).Success);
        Assert.True((await Tools.ListClasses(PageSize: 2)).Success);
        Assert.True((await Tools.GetClass("Part")).Success);
        Assert.True((await Tools.GetSelection(default)).Success);
        Assert.True(Adapter.Descriptor.IsMock);
    }

    [Fact]
    public async Task PaginationIsOpaqueStableAndScoped()
    {
        (_, ReadTools Tools) = CreateTools();
        ToolResponse<PagedResult<InstanceSummary>> First = await Tools.ListInstances(RecursiveDepth: 3, PageSize: 2);
        Assert.NotNull(First.Result?.NextPageToken);
        ToolResponse<PagedResult<InstanceSummary>> Second = await Tools.ListInstances(RecursiveDepth: 3, PageToken: First.Result!.NextPageToken, PageSize: 2);
        Assert.Empty(First.Result.Items.Select(Item => Item.Id).Intersect(Second.Result!.Items.Select(Item => Item.Id)));
        ToolResponse<PagedResult<InstanceSummary>> Repeat = await Tools.ListInstances(RecursiveDepth: 3, PageSize: 2);
        Assert.Equal(First.Result.Items, Repeat.Result!.Items);
        Assert.Equal(First.Result.NextPageToken, Repeat.Result.NextPageToken);

        ToolResponse<PagedResult<ClassSummary>> WrongScope = await Tools.ListClasses(First.Result.NextPageToken, 2);
        Assert.False(WrongScope.Success);
        Assert.Equal(nameof(GargantuanErrorCode.InvalidArgument), WrongScope.Error?.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task PageBoundsAreEnforced(int PageSize)
    {
        (_, ReadTools Tools) = CreateTools();
        ToolResponse<PagedResult<ClassSummary>> Result = await Tools.ListClasses(PageSize: PageSize);
        Assert.False(Result.Success);
        Assert.Equal(nameof(GargantuanErrorCode.ResourceLimit), Result.Error?.Code);
    }

    [Fact]
    public async Task MaximumPageSizeIsAcceptedAndResponsesAreSizeBounded()
    {
        (_, ReadTools Tools) = CreateTools();
        Assert.True((await Tools.ListClasses(PageSize: McpLimits.MaximumPageSize)).Success);
        ToolExecutor Executor = new(NullLogger<ToolExecutor>.Instance);
        ToolResponse<string> Oversized = await Executor.ExecuteAsync(_ => Task.FromResult(new string('x', McpLimits.MaximumResponseBytes + 1)), default);
        Assert.Equal(nameof(GargantuanErrorCode.ResourceLimit), Oversized.Error?.Code);
    }

    [Fact]
    public async Task HostileArgumentsFailSafely()
    {
        (_, ReadTools Tools) = CreateTools();
        Assert.Equal(nameof(GargantuanErrorCode.ResourceLimit), (await Tools.ListInstances(RecursiveDepth: 9)).Error?.Code);
        Assert.Equal(nameof(GargantuanErrorCode.ResourceLimit), (await Tools.ListInstances(NameQuery: new string('x', 257))).Error?.Code);
        Assert.Equal(nameof(GargantuanErrorCode.InvalidArgument), (await Tools.GetInstance("../not-an-id")).Error?.Code);
        Assert.Equal(nameof(GargantuanErrorCode.NotFound), (await Tools.GetInstance("gtn_missing")).Error?.Code);
        Assert.Equal(nameof(GargantuanErrorCode.InvalidArgument), (await Tools.ListClasses("malformed", 2)).Error?.Code);
    }

    [Fact]
    public async Task SelectionWriteIsPolicyOwnedAndBounded()
    {
        MockGargantuanAdapter Adapter = new();
        ToolExecutor Executor = new(NullLogger<ToolExecutor>.Instance);
        StudioTools DeniedTools = new(Adapter, Executor, new LocalToolPolicy());
        ToolResponse<IReadOnlyList<ObjectIdentity>> Denied = await DeniedTools.SetSelection(["gtn_workspace"]);
        Assert.False(Denied.Success);
        Assert.Equal(nameof(GargantuanErrorCode.PermissionDenied), Denied.Error?.Code);

        StudioTools AllowedTools = new(Adapter, Executor, new LocalToolPolicy(true));
        Assert.True((await AllowedTools.SetSelection(["gtn_workspace", "gtn_workspace_part"])).Success);
        Assert.Equal(nameof(GargantuanErrorCode.ResourceLimit), (await AllowedTools.SetSelection(Enumerable.Repeat("gtn_workspace", 129).ToArray())).Error?.Code);
    }

    [Fact]
    public async Task AdapterFailuresAreConfinedAndNormalized()
    {
        ToolExecutor Executor = new(NullLogger<ToolExecutor>.Instance);
        ToolResponse<string> Unavailable = await Executor.ExecuteAsync<string>(_ => throw new GargantuanAdapterException(GargantuanErrorCode.Unavailable, "Adapter unavailable."), default);
        ToolResponse<string> Internal = await Executor.ExecuteAsync<string>(_ => throw new InvalidOperationException("C:\\secret\\token.txt"), default);
        Assert.Equal(nameof(GargantuanErrorCode.Unavailable), Unavailable.Error?.Code);
        Assert.Equal(nameof(GargantuanErrorCode.InternalError), Internal.Error?.Code);
        Assert.DoesNotContain("secret", Internal.Error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepeatedConcurrentReadsRemainBoundedAndDeterministic()
    {
        (_, ReadTools Tools) = CreateTools();
        Task<ToolResponse<ProjectInfo>>[] Requests = Enumerable.Range(0, 64).Select(_ => Tools.GetProjectInfo(default)).ToArray();
        ToolResponse<ProjectInfo>[] Results = await Task.WhenAll(Requests);
        Assert.All(Results, Result => Assert.True(Result.Success));
        Assert.Single(Results.Select(Result => Result.Result).Distinct());
    }

    [Fact]
    public async Task CancellationFlowsThroughToolBoundary()
    {
        ToolExecutor Executor = new(NullLogger<ToolExecutor>.Instance);
        using CancellationTokenSource Cancellation = new();
        Cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor.ExecuteAsync(_ => Task.FromResult("unreachable"), Cancellation.Token));
    }
}
