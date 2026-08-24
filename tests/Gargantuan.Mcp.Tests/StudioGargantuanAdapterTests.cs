using Gargantuan.Mcp.Contracts;
using Gargantuan.Mcp.Server;
using Gargantuan.Mcp.Studio;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

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
    public async Task PaginationScopeEncodingIsInjectiveForCallerControlledText()
    {
        const char Delimiter = '\u001f';
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);

        PagedResult<InstanceSummary> Delimited = await Adapter.ListInstancesAsync(
            new(null, 2, $"left{Delimiter}right", "", null, 2), default);
        Assert.NotNull(Delimited.NextPageToken);
        GargantuanAdapterException DelimiterCollision = await Assert.ThrowsAsync<GargantuanAdapterException>(() =>
            Adapter.ListInstancesAsync(new(null, 2, "left", $"right{Delimiter}", Delimited.NextPageToken, 2), default));
        Assert.Equal(GargantuanErrorCode.InvalidArgument, DelimiterCollision.Code);

        PagedResult<InstanceSummary> NullScoped = await Adapter.ListInstancesAsync(
            new(null, 2, null, null, null, 2), default);
        GargantuanAdapterException NullEmptyCollision = await Assert.ThrowsAsync<GargantuanAdapterException>(() =>
            Adapter.ListInstancesAsync(new(null, 2, "", null, NullScoped.NextPageToken, 2), default));
        Assert.Equal(GargantuanErrorCode.InvalidArgument, NullEmptyCollision.Code);

        FakeStudioSessionClient OtherClient = new(FakeStudioSessionClient.AllCapabilities, "fake-session-2");
        StudioGargantuanAdapter OtherAdapter = await StudioGargantuanAdapter.CreateAsync(OtherClient);
        GargantuanAdapterException SessionCollision = await Assert.ThrowsAsync<GargantuanAdapterException>(() =>
            OtherAdapter.ListInstancesAsync(new(null, 2, null, null, NullScoped.NextPageToken, 2), default));
        Assert.Equal(GargantuanErrorCode.InvalidArgument, SessionCollision.Code);
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
    [InlineData(StudioBridgeErrorCode.CommandUnavailable, GargantuanErrorCode.CommandUnavailable)]
    [InlineData(StudioBridgeErrorCode.ValidationFailed, GargantuanErrorCode.ValidationFailed)]
    [InlineData(StudioBridgeErrorCode.Cancelled, GargantuanErrorCode.Cancelled)]
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
    public async Task ProjectWriteUsesTypedBridgeRequestAndReturnsOpaqueAuthoritativeIdentity()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ProjectInfo Project = await Adapter.GetProjectInfoAsync(default);
        InitialPropertyWrite[] InitialProperties =
        [
            new(new(ProjectPropertyKind.Native, "Name"),
                new("String", JsonSerializer.SerializeToElement("MCP Folder"))),
        ];

        ProjectWriteResult Result = await Adapter.CreateInstanceAsync(
            new("Folder", Project.RootId, InitialProperties, 17), default);

        Assert.NotNull(Result.ObjectId);
        Assert.StartsWith("gtn_studio_", Result.ObjectId.Value.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("10:2", Result.ObjectId.Value.Value, StringComparison.Ordinal);
        Assert.Equal(18, Result.Revision);
        Assert.Equal(17, Client.LastCreateRequest?.ExpectedRevision);
        Assert.Equal(new StudioObjectIdentity(1, 7), Client.LastCreateRequest?.ParentId);
        Assert.Equal("Name", Client.LastCreateRequest?.InitialProperties.Single().Property.Name);
        Assert.Equal("MCP Folder", Client.LastCreateRequest?.InitialProperties.Single().Value.Value?.GetString());
        Assert.Equal(1, Client.ProjectWriteCalls);
    }

    [Fact]
    public async Task ProjectWriteValidationRejectsBeforeBridgeInvocation()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ProjectInfo Project = await Adapter.GetProjectInfoAsync(default);

        GargantuanAdapterException Revision = await Assert.ThrowsAsync<GargantuanAdapterException>(() =>
            Adapter.SaveProjectAsync(new(0), default));
        Assert.Equal(GargantuanErrorCode.InvalidArgument, Revision.Code);

        GargantuanAdapterException DeleteAcknowledgement = await Assert.ThrowsAsync<GargantuanAdapterException>(() =>
            Adapter.DeleteInstanceAsync(new(Project.RootId, false, null), default));
        Assert.Equal(GargantuanErrorCode.InvalidArgument, DeleteAcknowledgement.Code);

        ProjectPropertyValue NonFinite = new("Double", JsonDocument.Parse("1e9999").RootElement.Clone());
        GargantuanAdapterException Value = await Assert.ThrowsAsync<GargantuanAdapterException>(() =>
            Adapter.SetPropertyAsync(new(Project.RootId, new(ProjectPropertyKind.Native, "Name"), NonFinite, null), default));
        Assert.Equal(GargantuanErrorCode.InvalidArgument, Value.Code);

        Assert.Equal(0, Client.ProjectWriteCalls);
    }

    [Fact]
    public async Task ProjectWritePolicyDeniesBeforeBridgeAndBoundsConcurrency()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ToolExecutor Executor = new(NullLogger<ToolExecutor>.Instance);

        ProjectWriteTools DeniedTools = new(Adapter, Executor, new LocalToolPolicy());
        ToolResponse<ProjectWriteResult> Denied = await DeniedTools.Save();
        Assert.Equal(nameof(GargantuanErrorCode.PermissionDenied), Denied.Error?.Code);
        Assert.Equal(0, Client.ProjectWriteCalls);

        TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Client.SaveOperation = async CancellationToken =>
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(CancellationToken);
            return FakeStudioSessionClient.SuccessfulWrite;
        };
        ProjectWriteTools AllowedTools = new(Adapter, Executor, new LocalToolPolicy(AllowProjectWrite: true));
        Task<ToolResponse<ProjectWriteResult>> First = AllowedTools.Save();
        await Entered.Task;
        ToolResponse<ProjectWriteResult> Concurrent = await AllowedTools.Undo();
        Assert.Equal(nameof(GargantuanErrorCode.ResourceLimit), Concurrent.Error?.Code);
        Release.SetResult();
        Assert.True((await First).Success);
        Assert.Equal(1, Client.ProjectWriteCalls);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task DestructiveWriteRequiresProjectAndDestructiveAuthority(
        bool AllowProjectWrite, bool AllowDestructiveWrite, bool ExpectedSuccess)
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ProjectInfo Project = await Adapter.GetProjectInfoAsync(default);
        ToolExecutor Executor = new(NullLogger<ToolExecutor>.Instance);
        ProjectWriteTools Tools = new(Adapter, Executor, new LocalToolPolicy(
            AllowProjectWrite: AllowProjectWrite, AllowDestructiveWrite: AllowDestructiveWrite));

        ToolResponse<ProjectWriteResult> Result = await Tools.Delete(Project.RootId.Value, true);

        Assert.Equal(ExpectedSuccess, Result.Success);
        Assert.Equal(ExpectedSuccess ? null : nameof(GargantuanErrorCode.PermissionDenied), Result.Error?.Code);
        Assert.Equal(ExpectedSuccess ? 1 : 0, Client.ProjectWriteCalls);
    }

    [Fact]
    public async Task ProjectWriteCancellationAndStaleIdentityArePreserved()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities)
        {
            SaveOperation = async CancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken);
                throw new InvalidOperationException("Unreachable");
            },
        };
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        using CancellationTokenSource Cancellation = new();
        Task<ProjectWriteResult> Pending = Adapter.SaveProjectAsync(new(null), Cancellation.Token);
        Cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pending);

        Client.SaveOperation = null;
        Client.ProjectWriteFailure = new StudioBridgeException(
            StudioBridgeErrorCode.StaleIdentity, "The object generation is stale.");
        GargantuanAdapterException Stale = await Assert.ThrowsAsync<GargantuanAdapterException>(() =>
            Adapter.SaveProjectAsync(new(null), default));
        Assert.Equal(GargantuanErrorCode.StaleIdentity, Stale.Code);
    }

    [Fact]
    public async Task ScriptSourceAndWritesPreserveExactTextIdentityAndRevisions()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ProjectInfo Project = await Adapter.GetProjectInfoAsync(default);

        ScriptSourceResult Source = await Adapter.GetScriptSourceAsync(Project.RootId, default);
        Assert.Equal(Project.RootId, Source.ObjectId);
        Assert.Equal("return 1\n", Source.Source);
        Assert.Equal(4, Source.SourceRevision);
        Assert.Equal(17, Source.ProjectRevision);

        ScriptWriteResult Created = await Adapter.CreateScriptAsync(new(
            "ModuleScript", Project.RootId, "MCP Module", "return { value = 1 }\n", 17), default);
        Assert.True(Created.AuthoritativeCommitConfirmed);
        Assert.Equal(18, Created.ProjectRevision);
        Assert.Equal("return { value = 1 }\n", Client.LastCreateScriptRequest?.Source);
        Assert.Equal(new StudioObjectIdentity(1, 7), Client.LastCreateScriptRequest?.ParentId);

        ScriptWriteResult Set = await Adapter.SetScriptSourceAsync(new(
            Project.RootId, "local =\n", 4, 17), default);
        Assert.Equal(5, Set.SourceRevision);
        Assert.Single(Set.Diagnostics);
        Assert.Equal("LuauSyntax", Set.Diagnostics[0].Code);
        Assert.Equal("local =\n", Client.LastSetScriptRequest?.Source);
        Assert.Equal(4, Client.LastSetScriptRequest?.ExpectedSourceRevision);
    }

    [Fact]
    public async Task ScriptWriteRequiresBothAuthoritiesSharesConcurrencyAndPreservesConflictDetails()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ProjectInfo Project = await Adapter.GetProjectInfoAsync(default);
        ToolExecutor Executor = new(NullLogger<ToolExecutor>.Instance);

        ProjectWriteTools NoScriptAuthority = new(Adapter, Executor,
            new LocalToolPolicy(AllowProjectWrite: true));
        ToolResponse<ScriptWriteResult> Denied = await NoScriptAuthority.SetScriptSource(
            Project.RootId.Value, "return 2\n", 4);
        Assert.Equal(nameof(GargantuanErrorCode.PermissionDenied), Denied.Error?.Code);
        Assert.Equal(0, Client.ScriptWriteCalls);

        TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Client.SetScriptOperation = async CancellationToken =>
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(CancellationToken);
            return FakeStudioSessionClient.SuccessfulSetScript;
        };
        ProjectWriteTools Allowed = new(Adapter, Executor, new LocalToolPolicy(
            AllowProjectWrite: true, AllowScriptWrite: true));
        Task<ToolResponse<ScriptWriteResult>> First = Allowed.SetScriptSource(
            Project.RootId.Value, "return 2\n", 4);
        await Entered.Task;
        ToolResponse<ProjectWriteResult> Concurrent = await Allowed.Save();
        Assert.Equal(nameof(GargantuanErrorCode.ResourceLimit), Concurrent.Error?.Code);
        Release.SetResult();
        Assert.True((await First).Success);

        Client.SetScriptOperation = null;
        Client.ScriptWriteFailure = new StudioBridgeException(
            StudioBridgeErrorCode.Conflict,
            "The authoritative script source changed before commit.",
            ConflictDetails: new StudioScriptConflictDetails(
                7, 21, true, "Reread script.get_source before retrying."));
        ToolResponse<ScriptWriteResult> Conflict = await Allowed.SetScriptSource(
            Project.RootId.Value, "return 3\n", 5);
        Assert.Equal(nameof(GargantuanErrorCode.Conflict), Conflict.Error?.Code);
        Assert.Equal(7, Conflict.Error?.Details?.CurrentSourceRevision);
        Assert.Equal(21, Conflict.Error?.Details?.CurrentProjectRevision);
        Assert.True(Conflict.Error?.Details?.LocalStudioEditsConflict);

        Client.ScriptWriteFailure = new StudioBridgeException(
            StudioBridgeErrorCode.Unavailable,
            "EditorHost committed the command, but Studio projection recovery failed.",
            CommitState: new StudioScriptCommitState(
                true, true, "Reopen Studio and reread authoritative state."));
        ToolResponse<ScriptWriteResult> CommittedUnavailable = await Allowed.SetScriptSource(
            Project.RootId.Value, "return 4\n", 7);
        Assert.Equal(nameof(GargantuanErrorCode.Unavailable), CommittedUnavailable.Error?.Code);
        Assert.True(CommittedUnavailable.Error?.CommitState?.AuthoritativeCommitConfirmed);
        Assert.True(CommittedUnavailable.Error?.CommitState?.ProjectionUnavailable);
    }

    [Fact]
    public async Task ScriptWriteBoundsRejectBeforeBridgeInvocation()
    {
        FakeStudioSessionClient Client = new(FakeStudioSessionClient.AllCapabilities);
        StudioGargantuanAdapter Adapter = await StudioGargantuanAdapter.CreateAsync(Client);
        ProjectInfo Project = await Adapter.GetProjectInfoAsync(default);

        GargantuanAdapterException Oversized = await Assert.ThrowsAsync<GargantuanAdapterException>(() =>
            Adapter.CreateScriptAsync(new("ModuleScript", Project.RootId, "Module",
                new string('x', McpLimits.MaximumScriptSourceBytes + 1), null), default));
        Assert.Equal(GargantuanErrorCode.ResourceLimit, Oversized.Code);

        GargantuanAdapterException InvalidRevision = await Assert.ThrowsAsync<GargantuanAdapterException>(() =>
            Adapter.SetScriptSourceAsync(new(Project.RootId, "return 1\n", 0, null), default));
        Assert.Equal(GargantuanErrorCode.InvalidArgument, InvalidRevision.Code);

        GargantuanAdapterException EmbeddedNul = await Assert.ThrowsAsync<GargantuanAdapterException>(() =>
            Adapter.CreateScriptAsync(new("ModuleScript", Project.RootId, "Module", "return\0 1", null), default));
        Assert.Equal(GargantuanErrorCode.InvalidArgument, EmbeddedNul.Code);
        Assert.Equal(0, Client.ScriptWriteCalls);

        ScriptWriteResult ExactUtf8Limit = await Adapter.CreateScriptAsync(new(
            "ModuleScript", Project.RootId, "Module", new string('\u00e9', 32 * 1024), null), default);
        Assert.True(ExactUtf8Limit.AuthoritativeCommitConfirmed);
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
        AdapterDescriptor ProjectWritable = ReadOnly with
        {
            Capabilities = new HashSet<AdapterCapability>(ReadOnly.Capabilities) { AdapterCapability.ProjectWrite },
        };
        AdapterDescriptor ScriptReadable = ReadOnly with
        {
            Capabilities = new HashSet<AdapterCapability>(ReadOnly.Capabilities) { AdapterCapability.ScriptInspection },
        };
        AdapterDescriptor ScriptWritable = ReadOnly with
        {
            Capabilities = new HashSet<AdapterCapability>(ReadOnly.Capabilities)
            {
                AdapterCapability.ProjectWrite,
                AdapterCapability.ScriptWrite,
            },
        };

        Assert.True(ToolRegistrationPolicy.CanAdvertiseReadTools(ReadOnly, new LocalToolPolicy()));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseSelectionWrite(ReadOnly, new LocalToolPolicy(true)));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseSelectionWrite(Writable, new LocalToolPolicy()));
        Assert.True(ToolRegistrationPolicy.CanAdvertiseSelectionWrite(Writable, new LocalToolPolicy(true)));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseProjectWrite(ReadOnly, new LocalToolPolicy(AllowProjectWrite: true)));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseProjectWrite(ProjectWritable, new LocalToolPolicy()));
        Assert.True(ToolRegistrationPolicy.CanAdvertiseProjectWrite(ProjectWritable, new LocalToolPolicy(AllowProjectWrite: true)));
        Assert.True(ToolRegistrationPolicy.CanAdvertiseScriptRead(ScriptReadable, new LocalToolPolicy()));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseScriptRead(ReadOnly, new LocalToolPolicy()));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseScriptWrite(ReadOnly, new LocalToolPolicy()));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseScriptWrite(ReadOnly,
            new LocalToolPolicy(AllowProjectWrite: true, AllowScriptWrite: true)));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseScriptWrite(ScriptWritable,
            new LocalToolPolicy(AllowProjectWrite: true)));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseScriptWrite(ScriptWritable,
            new LocalToolPolicy(AllowScriptWrite: true)));
        Assert.True(ToolRegistrationPolicy.CanAdvertiseScriptWrite(ScriptWritable,
            new LocalToolPolicy(AllowProjectWrite: true, AllowScriptWrite: true)));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseDestructiveWrite(ProjectWritable,
            new LocalToolPolicy(AllowProjectWrite: false, AllowDestructiveWrite: false)));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseDestructiveWrite(ProjectWritable,
            new LocalToolPolicy(AllowProjectWrite: true, AllowDestructiveWrite: false)));
        Assert.False(ToolRegistrationPolicy.CanAdvertiseDestructiveWrite(ProjectWritable,
            new LocalToolPolicy(AllowProjectWrite: false, AllowDestructiveWrite: true)));
        Assert.True(ToolRegistrationPolicy.CanAdvertiseDestructiveWrite(ProjectWritable,
            new LocalToolPolicy(AllowProjectWrite: true, AllowDestructiveWrite: true)));
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
        public static StudioProjectWriteResult SuccessfulWrite { get; } =
            new(new StudioObjectIdentity(10, 2), 18, 16, true, "MCP write", []);
        public static StudioScriptWriteResult SuccessfulSetScript { get; } =
            new(RootId, "ModuleScript", 5, 18, true, false,
                [new("LuauSyntax", "Expected expression.", 1, 7)]);

        public FakeStudioSessionClient(IReadOnlySet<StudioBridgeCapability> Capabilities, string SessionId = "fake-session-1")
        {
            Descriptor = new(SessionId, "DeterministicFakeStudioBridge", "2A-test", Capabilities);
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
        public int ProjectWriteCalls { get; private set; }
        public IReadOnlyList<StudioObjectIdentity> Selection { get; set; } = [RootId];
        public StudioCreateInstanceRequest? LastCreateRequest { get; private set; }
        public Exception? ProjectWriteFailure { get; set; }
        public Func<CancellationToken, Task<StudioProjectWriteResult>>? SaveOperation { get; set; }
        public int ScriptReadCalls { get; private set; }
        public int ScriptWriteCalls { get; private set; }
        public StudioCreateScriptRequest? LastCreateScriptRequest { get; private set; }
        public StudioSetScriptSourceRequest? LastSetScriptRequest { get; private set; }
        public Exception? ScriptWriteFailure { get; set; }
        public Func<CancellationToken, Task<StudioScriptWriteResult>>? SetScriptOperation { get; set; }

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

        public Task<StudioProjectWriteResult> CreateInstanceAsync(StudioCreateInstanceRequest Request, CancellationToken CancellationToken)
        {
            LastCreateRequest = Request;
            return CompleteWriteAsync(CancellationToken);
        }

        public Task<StudioProjectWriteResult> DeleteInstanceAsync(StudioDeleteInstanceRequest Request, CancellationToken CancellationToken) =>
            CompleteWriteAsync(CancellationToken);

        public Task<StudioProjectWriteResult> DuplicateInstanceAsync(StudioDuplicateInstanceRequest Request, CancellationToken CancellationToken) =>
            CompleteWriteAsync(CancellationToken);

        public Task<StudioProjectWriteResult> ReparentInstanceAsync(StudioReparentInstanceRequest Request, CancellationToken CancellationToken) =>
            CompleteWriteAsync(CancellationToken);

        public Task<StudioProjectWriteResult> SetPropertyAsync(StudioSetPropertyRequest Request, CancellationToken CancellationToken) =>
            CompleteWriteAsync(CancellationToken);

        public Task<StudioProjectWriteResult> SaveProjectAsync(StudioProjectRevisionRequest Request, CancellationToken CancellationToken)
        {
            ProjectWriteCalls++;
            CancellationToken.ThrowIfCancellationRequested();
            if (ProjectWriteFailure is not null)
                return Task.FromException<StudioProjectWriteResult>(ProjectWriteFailure);
            return SaveOperation?.Invoke(CancellationToken) ?? Task.FromResult(SuccessfulWrite);
        }

        public Task<StudioProjectWriteResult> UndoAsync(StudioProjectRevisionRequest Request, CancellationToken CancellationToken) =>
            CompleteWriteAsync(CancellationToken);

        public Task<StudioProjectWriteResult> RedoAsync(StudioProjectRevisionRequest Request, CancellationToken CancellationToken) =>
            CompleteWriteAsync(CancellationToken);

        public Task<StudioScriptSourceResult> GetScriptSourceAsync(
            StudioObjectIdentity ObjectId,
            CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            ScriptReadCalls++;
            return Task.FromResult(new StudioScriptSourceResult(
                ObjectId, "ModuleScript", "return 1\n", 4, 17));
        }

        public Task<StudioScriptWriteResult> CreateScriptAsync(
            StudioCreateScriptRequest Request,
            CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            ProjectWriteCalls++;
            ScriptWriteCalls++;
            LastCreateScriptRequest = Request;
            if (ScriptWriteFailure is not null)
                return Task.FromException<StudioScriptWriteResult>(ScriptWriteFailure);
            return Task.FromResult(new StudioScriptWriteResult(
                new StudioObjectIdentity(10, 2), "ModuleScript", 2, 18, true, false, []));
        }

        public Task<StudioScriptWriteResult> SetScriptSourceAsync(
            StudioSetScriptSourceRequest Request,
            CancellationToken CancellationToken)
        {
            CancellationToken.ThrowIfCancellationRequested();
            ProjectWriteCalls++;
            ScriptWriteCalls++;
            LastSetScriptRequest = Request;
            if (ScriptWriteFailure is not null)
                return Task.FromException<StudioScriptWriteResult>(ScriptWriteFailure);
            return SetScriptOperation?.Invoke(CancellationToken) ?? Task.FromResult(SuccessfulSetScript);
        }

        private Task<StudioProjectWriteResult> CompleteWriteAsync(CancellationToken CancellationToken)
        {
            ProjectWriteCalls++;
            CancellationToken.ThrowIfCancellationRequested();
            return ProjectWriteFailure is null
                ? Task.FromResult(SuccessfulWrite)
                : Task.FromException<StudioProjectWriteResult>(ProjectWriteFailure);
        }

        private static StudioInstanceDetails Instance(StudioObjectIdentity Id, string Name, string ClassName, StudioObjectIdentity? ParentId) =>
            new(Id, Name, ClassName, ParentId,
                new Dictionary<string, StudioPropertyValue> { ["Name"] = new("String", Name) },
                new Dictionary<string, string>(), [], null);

        private static StudioInstanceSummary Summary(StudioInstanceDetails Item, int Depth) =>
            new(Item.Id, Item.Name, Item.ClassName, Item.ParentId, Depth);
    }
}
