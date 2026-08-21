using Gargantuan.Mcp.Contracts;
using Gargantuan.Mcp.Server;
using Gargantuan.Mcp.Studio;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gargantuan.Mcp.Tests;

public sealed class StudioGargantuanAdapterTests
{
    [Fact]
    public async Task CapabilitiesComeOnlyFromNegotiatedStudioSession()
    {
        FakeStudioSessionClient Client = new(new HashSet<StudioBridgeCapability> { StudioBridgeCapability.ProjectInspection });
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);

        Assert.False(Adapter.Descriptor.IsMock);
        Assert.Equal([AdapterCapability.ProjectInspection], Adapter.Descriptor.Capabilities);
        GargantuanAdapterException Error = await Assert.ThrowsAsync<GargantuanAdapterException>(
            () => Adapter.GetSelectionAsync(default));
        Assert.Equal(GargantuanErrorCode.CapabilityUnavailable, Error.Code);
        Assert.Equal(0, Client.GetSelectionCalls);
    }

    [Fact]
    public async Task OpaqueIdentitiesRoundTripAndUnknownHandlesAreStale()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ProjectInfo Project = await Adapter.GetProjectInfoAsync(default);

        Assert.StartsWith("gtn_studio_", Project.RootId.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("1:7", Project.RootId.Value, StringComparison.Ordinal);
        InstanceDetails Root = await Adapter.GetInstanceAsync(Project.RootId, default);
        Assert.Equal(Project.RootId, Root.Id);
        Assert.Equal(new StudioObjectIdentity(1, 7), Client.LastInstanceId);

        GargantuanAdapterException Unknown = await Assert.ThrowsAsync<GargantuanAdapterException>(
            () => Adapter.GetInstanceAsync(new ObjectIdentity("gtn_studio_ffffffffffffffff"), default));
        Assert.Equal(GargantuanErrorCode.StaleIdentity, Unknown.Code);

        Client.GetInstanceFailure = new StudioBridgeException(StudioBridgeErrorCode.StaleIdentity, "The Studio object generation is stale.");
        GargantuanAdapterException Stale = await Assert.ThrowsAsync<GargantuanAdapterException>(
            () => Adapter.GetInstanceAsync(Project.RootId, default));
        Assert.Equal(GargantuanErrorCode.StaleIdentity, Stale.Code);
    }

    [Fact]
    public async Task PaginationIsBoundedSnapshotScopedAndDeterministic()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ProjectInfo Project = await Adapter.GetProjectInfoAsync(default);

        PagedResult<InstanceSummary> First = await Adapter.ListInstancesAsync(new(null, 2, null, null, null, 2), default);
        Assert.Equal(2, First.Items.Count);
        Assert.NotNull(First.NextPageToken);
        Assert.Equal(3, Client.LastListInstancesRequest?.Limit);
        Assert.Equal(McpLimits.MaximumSearchResults, Client.LastListInstancesRequest?.MaximumCandidates);
        Assert.Null(Client.LastListInstancesRequest?.ExpectedSnapshotVersion);

        PagedResult<InstanceSummary> Second = await Adapter.ListInstancesAsync(new(null, 2, null, null, First.NextPageToken, 2), default);
        Assert.Equal(2, Client.LastListInstancesRequest?.Offset);
        Assert.Equal((ulong)41, Client.LastListInstancesRequest?.ExpectedSnapshotVersion);
        Assert.Empty(First.Items.Select(Item => Item.Id).Intersect(Second.Items.Select(Item => Item.Id)));

        GargantuanAdapterException WrongScope = await Assert.ThrowsAsync<GargantuanAdapterException>(
            () => Adapter.GetChildrenAsync(new(Project.RootId, First.NextPageToken, 2), default));
        Assert.Equal(GargantuanErrorCode.InvalidArgument, WrongScope.Code);

        Client.SnapshotVersion = 42;
        GargantuanAdapterException Conflict = await Assert.ThrowsAsync<GargantuanAdapterException>(
            () => Adapter.ListInstancesAsync(new(null, 2, null, null, First.NextPageToken, 2), default));
        Assert.Equal(GargantuanErrorCode.Conflict, Conflict.Code);

        Client.ReturnOversizedPage = true;
        GargantuanAdapterException Oversized = await Assert.ThrowsAsync<GargantuanAdapterException>(
            () => Adapter.ListInstancesAsync(new(null, 1, null, null, null, 2), default));
        Assert.Equal(GargantuanErrorCode.ResourceLimit, Oversized.Code);
    }

    [Fact]
    public async Task InstanceSchemaAndSelectionConversionAreBounded()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ProjectInfo Project = await Adapter.GetProjectInfoAsync(default);
        InstanceDetails Root = await Adapter.GetInstanceAsync(Project.RootId, default);
        PagedResult<InstanceSummary> Children = await Adapter.GetChildrenAsync(new(Project.RootId, null, 2), default);
        PagedResult<ClassSummary> Classes = await Adapter.ListClassesAsync(new(null, 2), default);
        ClassDetails Class = await Adapter.GetClassAsync("DataModel", default);
        IReadOnlyList<ObjectIdentity> Selection = await Adapter.GetSelectionAsync(default);

        Assert.Equal("DataModel", Root.ClassName);
        Assert.NotEmpty(Children.Items);
        Assert.Equal(2, Classes.Items.Count);
        Assert.Equal("DataModel", Class.Id);
        Assert.Single(Selection);

        Client.Selection = Enumerable.Range(1, McpLimits.MaximumSelectionSize + 1)
            .Select(Index => new StudioObjectIdentity((uint)Index, 1)).ToArray();
        GargantuanAdapterException Oversized = await Assert.ThrowsAsync<GargantuanAdapterException>(
            () => Adapter.GetSelectionAsync(default));
        Assert.Equal(GargantuanErrorCode.ResourceLimit, Oversized.Code);
    }

    [Fact]
    public async Task CancellationFlowsIntoStudioBridge()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities)
        {
            GetProjectOperation = async CancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken);
                throw new InvalidOperationException("Unreachable");
            },
        };
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        using CancellationTokenSource Cancellation = new();
        Task<ProjectInfo> Pending = Adapter.GetProjectInfoAsync(Cancellation.Token);
        Cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pending);
    }

    [Theory]
    [InlineData(StudioBridgeErrorCode.Unavailable, GargantuanErrorCode.Unavailable)]
    [InlineData(StudioBridgeErrorCode.Conflict, GargantuanErrorCode.Conflict)]
    [InlineData(StudioBridgeErrorCode.CapabilityUnavailable, GargantuanErrorCode.CapabilityUnavailable)]
    public async Task KnownBridgeFailuresMapToStableErrors(StudioBridgeErrorCode BridgeCode, GargantuanErrorCode AdapterCode)
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities)
        {
            GetProjectFailure = new StudioBridgeException(BridgeCode, "Safe Studio failure."),
        };
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);

        GargantuanAdapterException Error = await Assert.ThrowsAsync<GargantuanAdapterException>(
            () => Adapter.GetProjectInfoAsync(default));
        Assert.Equal(AdapterCode, Error.Code);
        Assert.Equal("Safe Studio failure.", Error.SafeMessage);
    }

    [Fact]
    public async Task UnexpectedAndInternalBridgeFailuresDoNotLeakDetails()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities)
        {
            GetProjectFailure = new StudioBridgeException(StudioBridgeErrorCode.InternalError, "C:\\private\\session-token.txt"),
        };
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        GargantuanAdapterException Internal = await Assert.ThrowsAsync<GargantuanAdapterException>(
            () => Adapter.GetProjectInfoAsync(default));
        Assert.Equal(GargantuanErrorCode.InternalError, Internal.Code);
        Assert.DoesNotContain("private", Internal.SafeMessage, StringComparison.OrdinalIgnoreCase);

        Client.GetProjectFailure = new InvalidOperationException("C:\\private\\other-secret.txt");
        GargantuanAdapterException Unexpected = await Assert.ThrowsAsync<GargantuanAdapterException>(
            () => Adapter.GetProjectInfoAsync(default));
        Assert.Equal(GargantuanErrorCode.InternalError, Unexpected.Code);
        Assert.DoesNotContain("secret", Unexpected.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectionPolicyDeniesBeforeCapableBridgeIsCalled()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ProjectInfo Project = await Adapter.GetProjectInfoAsync(default);
        ToolExecutor Executor = new(NullLogger<ToolExecutor>.Instance);

        StudioTools DeniedTools = new(Adapter, Executor, new LocalToolPolicy());
        ToolResponse<IReadOnlyList<ObjectIdentity>> Denied = await DeniedTools.SetSelection([Project.RootId.Value]);
        Assert.Equal(nameof(GargantuanErrorCode.PermissionDenied), Denied.Error?.Code);
        Assert.Equal(0, Client.SetSelectionCalls);

        StudioTools AllowedTools = new(Adapter, Executor, new LocalToolPolicy(true));
        ToolResponse<IReadOnlyList<ObjectIdentity>> Allowed = await AllowedTools.SetSelection([Project.RootId.Value]);
        Assert.True(Allowed.Success);
        Assert.Equal(1, Client.SetSelectionCalls);
    }

    [Fact]
    public void ToolAdvertisementRequiresBothServerPolicyAndAdapterCapability()
    {
        AdapterDescriptor ReadOnly = new("Studio", "1", new HashSet<AdapterCapability>
        {
            AdapterCapability.ProjectInspection,
            AdapterCapability.HierarchyInspection,
            AdapterCapability.SchemaInspection,
            AdapterCapability.SelectionInspection,
        }, false);
        AdapterDescriptor Writable = ReadOnly with
        {
            Capabilities = new HashSet<AdapterCapability>(ReadOnly.Capabilities) { AdapterCapability.SelectionWrite },
        };

        Assert.True(ToolRegistrationPolicy.CanAdvertiseReadTools(ReadOnly, new LocalToolPolicy()));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseSelectionWrite(ReadOnly, new LocalToolPolicy(true)));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseSelectionWrite(Writable, new LocalToolPolicy()));
        Assert.True(ToolRegistrationPolicy.CanAdvertiseSelectionWrite(Writable, new LocalToolPolicy(true)));
    }

    private sealed class FakeStudioSessionClient : IStudioSessionClient
    {
        private static readonly StudioObjectIdentity RootId = new(1, 7);
        private readonly StudioSessionDescriptor Descriptor;
        private readonly StudioInstanceDetails[] Instances =
        [
            Instance(RootId, "DataModel", "DataModel", null),
            Instance(new(2, 3), "Workspace", "Workspace", RootId),
            Instance(new(3, 4), "PartA", "Part", new(2, 3)),
            Instance(new(4, 2), "PartB", "Part", new(2, 3)),
            Instance(new(5, 9), "Folder", "Folder", RootId),
        ];

        public static IReadOnlySet<StudioBridgeCapability> AllCapabilities { get; } =
            new HashSet<StudioBridgeCapability>(Enum.GetValues<StudioBridgeCapability>());

        public FakeStudioSessionClient(IReadOnlySet<StudioBridgeCapability> Capabilities)
        {
            Descriptor = new("fake-session-1", "DeterministicFakeStudioBridge", "2A-test", Capabilities);
        }

        public Exception? GetProjectFailure { get; set; }
        public Exception? GetInstanceFailure { get; set; }
        public Func<CancellationToken, Task<StudioProjectInfo>>? GetProjectOperation { get; set; }
        public StudioListInstancesRequest? LastListInstancesRequest { get; private set; }
        public StudioObjectIdentity? LastInstanceId { get; private set; }
        public bool ReturnOversizedPage { get; set; }
        public ulong SnapshotVersion { get; set; } = 41;
        public int GetSelectionCalls { get; private set; }
        public int SetSelectionCalls { get; private set; }
        public IReadOnlyList<StudioObjectIdentity> Selection { get; set; } = [RootId];

        public Task<StudioSessionDescriptor> DescribeSessionAsync(CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Descriptor);
        }

        public Task<StudioProjectInfo> GetProjectInfoAsync(CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (GetProjectFailure is not null) return Task.FromException<StudioProjectInfo>(GetProjectFailure);
            if (GetProjectOperation is not null) return GetProjectOperation(CancellationToken);
            return Task.FromResult(new StudioProjectInfo("fake-project", "Fake Project", RootId, "DataModel", 17, "schema-4"));
        }

        public Task<StudioPage<StudioInstanceSummary>> ListInstancesAsync(StudioListInstancesRequest Request, CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            LastListInstancesRequest = Request;
            StudioInstanceSummary[] Items = Instances.Skip(1)
                .Select((Item, Index) => Summary(Item, Index < 1 ? 1 : 2))
                .Skip(Request.Offset).Take(ReturnOversizedPage ? Request.Limit + 1 : Request.Limit).ToArray();
            if (ReturnOversizedPage)
            {
                Items = Enumerable.Range(0, Request.Limit + 1)
                    .Select(Index => new StudioInstanceSummary(new((uint)(100 + Index), 1), $"Item{Index}", "Folder", RootId, 1)).ToArray();
            }
            return Task.FromResult(new StudioPage<StudioInstanceSummary>(Items, SnapshotVersion));
        }

        public Task<StudioInstanceDetails> GetInstanceAsync(StudioObjectIdentity Id, CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            LastInstanceId = Id;
            if (GetInstanceFailure is not null) return Task.FromException<StudioInstanceDetails>(GetInstanceFailure);
            StudioInstanceDetails? Item = Instances.SingleOrDefault(Item => Item.Id == Id);
            return Item is null
                ? Task.FromException<StudioInstanceDetails>(new StudioBridgeException(StudioBridgeErrorCode.StaleIdentity, "The object is stale."))
                : Task.FromResult(Item);
        }

        public Task<StudioPage<StudioInstanceSummary>> GetChildrenAsync(StudioGetChildrenRequest Request, CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            StudioInstanceSummary[] Items = Instances.Where(Item => Item.ParentId == Request.ParentId)
                .Select(Item => Summary(Item, 1)).Skip(Request.Offset).Take(Request.Limit).ToArray();
            return Task.FromResult(new StudioPage<StudioInstanceSummary>(Items, SnapshotVersion));
        }

        public Task<StudioPage<StudioClassSummary>> ListClassesAsync(StudioListClassesRequest Request, CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            StudioClassSummary[] Items = new[]
            {
                new StudioClassSummary("DataModel", "DataModel", "Instance", false, "Native"),
                new StudioClassSummary("Folder", "Folder", "Instance", true, "Native"),
                new StudioClassSummary("Instance", "Instance", null, false, "Native"),
                new StudioClassSummary("Part", "Part", "Instance", true, "Native"),
            }.Skip(Request.Offset).Take(Request.Limit).ToArray();
            return Task.FromResult(new StudioPage<StudioClassSummary>(Items, 9));
        }

        public Task<StudioClassDetails> GetClassAsync(string ClassId, CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new StudioClassDetails(ClassId, ClassId, ClassId == "Instance" ? null : "Instance", false, "Native", ["Instance"], [new("Name", "String", false, null)]));
        }

        public Task<IReadOnlyList<StudioObjectIdentity>> GetSelectionAsync(CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            GetSelectionCalls++;
            return Task.FromResult(Selection);
        }

        public Task<IReadOnlyList<StudioObjectIdentity>> SetSelectionAsync(IReadOnlyList<StudioObjectIdentity> NewSelection, CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            SetSelectionCalls++;
            Selection = NewSelection.ToArray();
            return Task.FromResult(Selection);
        }

        private static StudioInstanceDetails Instance(StudioObjectIdentity Id, string Name, string ClassName, StudioObjectIdentity? ParentId) =>
            new(Id, Name, ClassName, ParentId,
                new Dictionary<string, StudioPropertyValue> { ["Name"] = new("String", Name) },
                new Dictionary<string, string>(), [], null);

        private static StudioInstanceSummary Summary(StudioInstanceDetails Item, int Depth) =>
            new(Item.Id, Item.Name, Item.ClassName, Item.ParentId, Depth);
    }
}
