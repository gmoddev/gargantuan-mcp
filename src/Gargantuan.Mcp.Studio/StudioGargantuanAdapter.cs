using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gargantuan.Mcp.Contracts;

namespace Gargantuan.Mcp.Studio;

public sealed class StudioGargantuanAdapter : IGargantuanAdapter
{
    private const int MaximumIdentityLength = 128;
    private const int MaximumSafeMessageLength = 512;
    private const int MaximumInheritanceDepth = 128;
    private const int MaximumPaginationOffset = 1_000_000;
    private const string IdentityPrefix = "gtn_studio_";

    private readonly IStudioSessionClient Client;
    private readonly StudioIdentityMap Identities = new();
    private readonly string SessionId;

    private StudioGargantuanAdapter(IStudioSessionClient Client, StudioSessionDescriptor Session)
    {
        this.Client = Client;
        SessionId = RequireBridgeString(Session.SessionId, nameof(Session.SessionId));
        string Version = RequireBridgeString(Session.Version, nameof(Session.Version));
        _ = RequireBridgeString(Session.Name, nameof(Session.Name));

        HashSet<AdapterCapability> Capabilities = [];
        foreach (StudioBridgeCapability Capability in Session.Capabilities)
        {
            Capabilities.Add(Capability switch
            {
                StudioBridgeCapability.ProjectInspection => AdapterCapability.ProjectInspection,
                StudioBridgeCapability.HierarchyInspection => AdapterCapability.HierarchyInspection,
                StudioBridgeCapability.SchemaInspection => AdapterCapability.SchemaInspection,
                StudioBridgeCapability.SelectionInspection => AdapterCapability.SelectionInspection,
                StudioBridgeCapability.SelectionWrite => AdapterCapability.SelectionWrite,
                StudioBridgeCapability.ProjectWrite => AdapterCapability.ProjectWrite,
                _ => throw new GargantuanAdapterException(GargantuanErrorCode.InternalError, "Studio advertised an unsupported capability."),
            });
        }

        Descriptor = new AdapterDescriptor("StudioGargantuanAdapter", Version, Capabilities, false);
    }

    public AdapterDescriptor Descriptor { get; }

    public static async Task<StudioGargantuanAdapter> CreateAsync(IStudioSessionClient Client, CancellationToken CancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Client);
        try
        {
            StudioSessionDescriptor Session = await Client.DescribeSessionAsync(CancellationToken).ConfigureAwait(false);
            return new StudioGargantuanAdapter(Client, Session);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StudioBridgeException Exception)
        {
            throw MapBridgeException(Exception);
        }
        catch (GargantuanAdapterException)
        {
            throw;
        }
        catch (Exception Exception)
        {
            throw new GargantuanAdapterException(GargantuanErrorCode.InternalError, "Studio session negotiation failed.", Exception);
        }
    }

    public Task<ProjectInfo> GetProjectInfoAsync(CancellationToken CancellationToken) => InvokeAsync(
        AdapterCapability.ProjectInspection,
        async () =>
        {
            StudioProjectInfo Project = await Client.GetProjectInfoAsync(CancellationToken).ConfigureAwait(false);
            ValidateNativeIdentity(Project.RootId);
            if (Project.Revision < 0)
                throw BridgeContractError("Studio returned an invalid project revision.");
            return new ProjectInfo(
                RequireBridgeString(Project.ProjectId, nameof(Project.ProjectId)),
                RequireBridgeString(Project.Name, nameof(Project.Name)),
                Identities.GetMcpIdentity(Project.RootId),
                RequireBridgeString(Project.RootClassName, nameof(Project.RootClassName)),
                Project.Revision,
                RequireBridgeString(Project.SchemaVersion, nameof(Project.SchemaVersion)),
                Descriptor);
        }, CancellationToken);

    public Task<PagedResult<InstanceSummary>> ListInstancesAsync(ListInstancesRequest Request, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.HierarchyInspection);
        ValidatePage(Request.PageSize, Request.PageToken);
        if (Request.RecursiveDepth is < 0 or > McpLimits.MaximumRecursionDepth)
            throw Error(GargantuanErrorCode.ResourceLimit, $"RecursiveDepth must be between 0 and {McpLimits.MaximumRecursionDepth}.");
        ValidateOptionalQuery(Request.ClassFilter, nameof(Request.ClassFilter));
        ValidateOptionalQuery(Request.NameQuery, nameof(Request.NameQuery));

        StudioObjectIdentity? ParentId = Request.ParentId is { } McpParent ? Identities.GetStudioIdentity(McpParent) : null;
        string Scope = GetScope("instances", Request.ParentId?.Value,
            Request.RecursiveDepth.ToString(CultureInfo.InvariantCulture), Request.ClassFilter, Request.NameQuery);
        PageCursor Cursor = DecodePageToken(Request.PageToken, Scope);
        StudioListInstancesRequest BridgeRequest = new(
            ParentId, Request.RecursiveDepth, Request.ClassFilter, Request.NameQuery,
            Cursor.Offset, checked(Request.PageSize + 1), McpLimits.MaximumSearchResults, Cursor.SnapshotVersion);

        return InvokeAsync(
            AdapterCapability.HierarchyInspection,
            async () =>
            {
                StudioPage<StudioInstanceSummary> Page = await Client.ListInstancesAsync(BridgeRequest, CancellationToken).ConfigureAwait(false);
                return ConvertPage(Page, Request.PageSize, Scope, Cursor, ConvertSummary);
            }, CancellationToken);
    }

    public Task<InstanceDetails> GetInstanceAsync(ObjectIdentity Id, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.HierarchyInspection);
        StudioObjectIdentity StudioId = Identities.GetStudioIdentity(Id);
        return InvokeAsync(
            AdapterCapability.HierarchyInspection,
            async () =>
            {
                StudioInstanceDetails Item = await Client.GetInstanceAsync(StudioId, CancellationToken).ConfigureAwait(false);
                if (Item.Id != StudioId)
                    throw BridgeContractError("Studio returned details for a different object.");
                return ConvertDetails(Item);
            }, CancellationToken);
    }

    public Task<PagedResult<InstanceSummary>> GetChildrenAsync(GetChildrenRequest Request, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.HierarchyInspection);
        ValidatePage(Request.PageSize, Request.PageToken);
        StudioObjectIdentity ParentId = Identities.GetStudioIdentity(Request.ParentId);
        string Scope = GetScope("children", Request.ParentId.Value);
        PageCursor Cursor = DecodePageToken(Request.PageToken, Scope);
        StudioGetChildrenRequest BridgeRequest = new(ParentId, Cursor.Offset, checked(Request.PageSize + 1), Cursor.SnapshotVersion);
        return InvokeAsync(
            AdapterCapability.HierarchyInspection,
            async () =>
            {
                StudioPage<StudioInstanceSummary> Page = await Client.GetChildrenAsync(BridgeRequest, CancellationToken).ConfigureAwait(false);
                return ConvertPage(Page, Request.PageSize, Scope, Cursor, ConvertSummary);
            }, CancellationToken);
    }

    public Task<PagedResult<ClassSummary>> ListClassesAsync(ListClassesRequest Request, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.SchemaInspection);
        ValidatePage(Request.PageSize, Request.PageToken);
        string Scope = GetScope("classes");
        PageCursor Cursor = DecodePageToken(Request.PageToken, Scope);
        StudioListClassesRequest BridgeRequest = new(Cursor.Offset, checked(Request.PageSize + 1), Cursor.SnapshotVersion);
        return InvokeAsync(
            AdapterCapability.SchemaInspection,
            async () =>
            {
                StudioPage<StudioClassSummary> Page = await Client.ListClassesAsync(BridgeRequest, CancellationToken).ConfigureAwait(false);
                return ConvertPage(Page, Request.PageSize, Scope, Cursor, ConvertClassSummary);
            }, CancellationToken);
    }

    public Task<ClassDetails> GetClassAsync(string ClassId, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.SchemaInspection);
        ValidateClassId(ClassId);
        return InvokeAsync(
            AdapterCapability.SchemaInspection,
            async () =>
            {
                StudioClassDetails Item = await Client.GetClassAsync(ClassId, CancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(Item.Id, ClassId))
                    throw BridgeContractError("Studio returned a different schema class.");
                if (Item.Inheritance.Count > MaximumInheritanceDepth || Item.Properties.Count > McpLimits.MaximumPropertyCount)
                    throw Error(GargantuanErrorCode.ResourceLimit, "Studio schema metadata exceeds the MCP bounds.");
                return new ClassDetails(
                    RequireBridgeString(Item.Id, nameof(Item.Id)),
                    RequireBridgeString(Item.Name, nameof(Item.Name)),
                    OptionalBridgeString(Item.BaseClassId, nameof(Item.BaseClassId)),
                    Item.Constructible,
                    RequireBridgeString(Item.Provenance, nameof(Item.Provenance)),
                    Item.Inheritance.Select(Value => RequireBridgeString(Value, "Inheritance")).ToArray(),
                    Item.Properties.Select(ConvertPropertyMetadata).ToArray());
            }, CancellationToken);
    }

    public Task<IReadOnlyList<ObjectIdentity>> GetSelectionAsync(CancellationToken CancellationToken) => InvokeAsync(
        AdapterCapability.SelectionInspection,
        async () => ConvertSelection(await Client.GetSelectionAsync(CancellationToken).ConfigureAwait(false)),
        CancellationToken);

    public Task<IReadOnlyList<ObjectIdentity>> SetSelectionAsync(IReadOnlyList<ObjectIdentity> Selection, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.SelectionWrite);
        ValidateSelectionCount(Selection.Count);
        if (Selection.Distinct().Count() != Selection.Count)
            throw Error(GargantuanErrorCode.InvalidArgument, "Selection contains duplicate identifiers.");
        StudioObjectIdentity[] StudioSelection = Selection.Select(Identities.GetStudioIdentity).ToArray();
        return InvokeAsync(
            AdapterCapability.SelectionWrite,
            async () => ConvertSelection(await Client.SetSelectionAsync(StudioSelection, CancellationToken).ConfigureAwait(false)),
            CancellationToken);
    }

    public Task<ProjectWriteResult> CreateInstanceAsync(CreateInstanceRequest Request, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.ProjectWrite);
        ValidateExpectedRevision(Request.ExpectedRevision);
        ValidateClassId(Request.ClassId);
        if (Request.InitialProperties.Count > McpLimits.MaximumInitialPropertyCount)
            throw Error(GargantuanErrorCode.ResourceLimit,
                $"Create initialization exceeds {McpLimits.MaximumInitialPropertyCount} properties.");
        StudioInitialPropertyWrite[] InitialProperties = Request.InitialProperties
            .Select(ConvertInitialProperty).ToArray();
        if (InitialProperties.Select(Item => Item.Property).Distinct().Count() != InitialProperties.Length)
            throw Error(GargantuanErrorCode.InvalidArgument, "Create initialization contains duplicate properties.");
        StudioCreateInstanceRequest BridgeRequest = new(
            Request.ClassId,
            Identities.GetStudioIdentity(Request.ParentId),
            InitialProperties,
            Request.ExpectedRevision);
        ValidateWriteRequestSize(BridgeRequest);
        return InvokeWriteAsync(
            () => Client.CreateInstanceAsync(BridgeRequest, CancellationToken), CancellationToken);
    }

    public Task<ProjectWriteResult> DeleteInstanceAsync(DeleteInstanceRequest Request, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.ProjectWrite);
        ValidateExpectedRevision(Request.ExpectedRevision);
        if (!Request.DeleteSubtree)
            throw Error(GargantuanErrorCode.InvalidArgument,
                "DeleteSubtree must be true to acknowledge that the target and all descendants will be deleted.");
        StudioDeleteInstanceRequest BridgeRequest = new(
            Identities.GetStudioIdentity(Request.ObjectId), true, Request.ExpectedRevision);
        return InvokeWriteAsync(
            () => Client.DeleteInstanceAsync(BridgeRequest, CancellationToken), CancellationToken);
    }

    public Task<ProjectWriteResult> DuplicateInstanceAsync(DuplicateInstanceRequest Request, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.ProjectWrite);
        ValidateExpectedRevision(Request.ExpectedRevision);
        StudioDuplicateInstanceRequest BridgeRequest = new(
            Identities.GetStudioIdentity(Request.ObjectId), Request.ExpectedRevision);
        return InvokeWriteAsync(
            () => Client.DuplicateInstanceAsync(BridgeRequest, CancellationToken), CancellationToken);
    }

    public Task<ProjectWriteResult> ReparentInstanceAsync(ReparentInstanceRequest Request, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.ProjectWrite);
        ValidateExpectedRevision(Request.ExpectedRevision);
        StudioReparentInstanceRequest BridgeRequest = new(
            Identities.GetStudioIdentity(Request.ObjectId),
            Identities.GetStudioIdentity(Request.ParentId),
            Request.ExpectedRevision);
        return InvokeWriteAsync(
            () => Client.ReparentInstanceAsync(BridgeRequest, CancellationToken), CancellationToken);
    }

    public Task<ProjectWriteResult> SetPropertyAsync(SetPropertyRequest Request, CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.ProjectWrite);
        ValidateExpectedRevision(Request.ExpectedRevision);
        StudioSetPropertyRequest BridgeRequest = new(
            Identities.GetStudioIdentity(Request.ObjectId),
            ConvertPropertyTarget(Request.Property),
            ConvertPropertyValue(Request.Value),
            Request.ExpectedRevision);
        ValidateWriteRequestSize(BridgeRequest);
        return InvokeWriteAsync(
            () => Client.SetPropertyAsync(BridgeRequest, CancellationToken), CancellationToken);
    }

    public Task<ProjectWriteResult> SaveProjectAsync(ProjectRevisionRequest Request, CancellationToken CancellationToken) =>
        InvokeRevisionWriteAsync(Request, Client.SaveProjectAsync, CancellationToken);

    public Task<ProjectWriteResult> UndoAsync(ProjectRevisionRequest Request, CancellationToken CancellationToken) =>
        InvokeRevisionWriteAsync(Request, Client.UndoAsync, CancellationToken);

    public Task<ProjectWriteResult> RedoAsync(ProjectRevisionRequest Request, CancellationToken CancellationToken) =>
        InvokeRevisionWriteAsync(Request, Client.RedoAsync, CancellationToken);

    private Task<ProjectWriteResult> InvokeRevisionWriteAsync(
        ProjectRevisionRequest Request,
        Func<StudioProjectRevisionRequest, CancellationToken, Task<StudioProjectWriteResult>> Operation,
        CancellationToken CancellationToken)
    {
        RequireCapability(AdapterCapability.ProjectWrite);
        ValidateExpectedRevision(Request.ExpectedRevision);
        StudioProjectRevisionRequest BridgeRequest = new(Request.ExpectedRevision);
        return InvokeWriteAsync(() => Operation(BridgeRequest, CancellationToken), CancellationToken);
    }

    private Task<ProjectWriteResult> InvokeWriteAsync(
        Func<Task<StudioProjectWriteResult>> Operation,
        CancellationToken CancellationToken) => InvokeAsync(
        AdapterCapability.ProjectWrite,
        async () => ConvertWriteResult(await Operation().ConfigureAwait(false)),
        CancellationToken);

    private ProjectWriteResult ConvertWriteResult(StudioProjectWriteResult Result)
    {
        if (Result.Revision < 0 || Result.PersistedRevision < 0 || Result.Diagnostics is null ||
            Result.Diagnostics.Count > McpLimits.MaximumWriteDiagnostics)
            throw BridgeContractError("Studio returned an invalid ProjectWrite result.");
        if (Result.ObjectId is { } Object) ValidateNativeIdentity(Object);
        ProjectWriteDiagnostic[] Diagnostics = Result.Diagnostics.Select(Item => new ProjectWriteDiagnostic(
            RequireBridgeString(Item.Code, nameof(Item.Code)),
            RequireBridgeString(Item.Message, nameof(Item.Message)))).ToArray();
        return new ProjectWriteResult(
            Result.ObjectId is { } ObjectId ? Identities.GetMcpIdentity(ObjectId) : null,
            Result.Revision,
            Result.PersistedRevision,
            Result.Dirty,
            OptionalBridgeString(Result.HistoryLabel, nameof(Result.HistoryLabel)),
            Diagnostics);
    }

    private StudioInitialPropertyWrite ConvertInitialProperty(InitialPropertyWrite Item) => new(
        ConvertPropertyTarget(Item.Property), ConvertPropertyValue(Item.Value));

    private static StudioProjectPropertyTarget ConvertPropertyTarget(ProjectPropertyTarget Property)
    {
        if (Property is null)
            throw Error(GargantuanErrorCode.InvalidArgument, "Property target is required.");
        string Name = RequireInputString(Property.Name, "Property name", 256);
        string? DeclaringSchemaId = Property.DeclaringSchemaId is null
            ? null
            : RequireInputString(Property.DeclaringSchemaId, "Declaring schema identity", McpLimits.MaximumStringLength);
        if (Property.Kind == ProjectPropertyKind.Native && DeclaringSchemaId is not null ||
            Property.Kind is ProjectPropertyKind.Custom or ProjectPropertyKind.Extension && DeclaringSchemaId is null)
            throw Error(GargantuanErrorCode.InvalidArgument,
                "Native properties omit DeclaringSchemaId; custom and extension properties require it.");
        return new StudioProjectPropertyTarget(
            Property.Kind switch
            {
                ProjectPropertyKind.Native => StudioProjectPropertyKind.Native,
                ProjectPropertyKind.Custom => StudioProjectPropertyKind.Custom,
                ProjectPropertyKind.Extension => StudioProjectPropertyKind.Extension,
                _ => throw Error(GargantuanErrorCode.InvalidArgument, "Property kind is invalid."),
            },
            Name,
            DeclaringSchemaId);
    }

    private StudioProjectPropertyValue ConvertPropertyValue(ProjectPropertyValue Input)
    {
        if (Input is null)
            throw Error(GargantuanErrorCode.InvalidArgument, "Property value is required.");
        string Type = RequireInputString(Input.Type, "Property value type", 32);
        JsonElement? Value = Input.Value;
        string? Enum = Input.Enum;
        string? SchemaId = Input.SchemaId;
        uint? DefinitionVersion = Input.DefinitionVersion;
        StudioObjectIdentity? Object = Input.Object is { } ObjectId ? Identities.GetStudioIdentity(ObjectId) : null;

        switch (Type)
        {
            case "Null":
                RequireValueShape(Value is null, Enum is null && SchemaId is null && DefinitionVersion is null && Object is null);
                break;
            case "Bool":
                RequireValueShape(Value?.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    Enum is null && SchemaId is null && DefinitionVersion is null && Object is null);
                break;
            case "Int":
                RequireValueShape(Value is { } Integer && Integer.TryGetInt32(out _),
                    Enum is null && SchemaId is null && DefinitionVersion is null && Object is null);
                break;
            case "Float":
            case "Double":
                RequireValueShape(Value is { ValueKind: JsonValueKind.Number } Number &&
                    Number.TryGetDouble(out double Scalar) && double.IsFinite(Scalar),
                    Enum is null && SchemaId is null && DefinitionVersion is null && Object is null);
                break;
            case "String":
                RequireValueShape(Value is { ValueKind: JsonValueKind.String } Text &&
                    Encoding.UTF8.GetByteCount(Text.GetString() ?? string.Empty) <= McpLimits.MaximumPropertyStringBytes,
                    Enum is null && SchemaId is null && DefinitionVersion is null && Object is null);
                break;
            case "Vector2": ValidateComponents(Value, 2, false); RequireNoValueIdentity(Enum, SchemaId, DefinitionVersion, Object); break;
            case "Vector3": ValidateComponents(Value, 3, false); RequireNoValueIdentity(Enum, SchemaId, DefinitionVersion, Object); break;
            case "Color3": ValidateComponents(Value, 3, false); RequireNoValueIdentity(Enum, SchemaId, DefinitionVersion, Object); break;
            case "UDim": ValidateComponents(Value, 2, true); RequireNoValueIdentity(Enum, SchemaId, DefinitionVersion, Object); break;
            case "UDim2": ValidateComponents(Value, 4, true); RequireNoValueIdentity(Enum, SchemaId, DefinitionVersion, Object); break;
            case "CFrame": ValidateComponents(Value, 12, false); RequireNoValueIdentity(Enum, SchemaId, DefinitionVersion, Object); break;
            case "EnumItem":
                RequireValueShape(Value is { ValueKind: JsonValueKind.String } &&
                    !string.IsNullOrWhiteSpace(Enum), SchemaId is null && DefinitionVersion is null && Object is null);
                Enum = RequireInputString(Enum!, "Native enum identity", McpLimits.MaximumStringLength);
                break;
            case "SchemaEnum":
                RequireValueShape(Value is { } EnumValue && EnumValue.TryGetInt32(out _) &&
                    !string.IsNullOrWhiteSpace(SchemaId) && DefinitionVersion is > 0,
                    Enum is null && Object is null);
                SchemaId = RequireInputString(SchemaId!, "Schema enum identity", McpLimits.MaximumStringLength);
                break;
            case "ObjectReference":
                RequireValueShape(Value is null && Object is not null,
                    Enum is null && SchemaId is null && DefinitionVersion is null);
                break;
            default:
                throw Error(GargantuanErrorCode.InvalidArgument, "Property value Type is unsupported.");
        }

        return new StudioProjectPropertyValue(Type, Value, Enum, SchemaId, DefinitionVersion, Object);
    }

    private static void ValidateComponents(JsonElement? Value, int Count, bool IntegralOffsets)
    {
        if (Value is not { ValueKind: JsonValueKind.Array } Components || Components.GetArrayLength() != Count)
            throw Error(GargantuanErrorCode.InvalidArgument, "Property component array has the wrong size.");
        int Index = 0;
        foreach (JsonElement Component in Components.EnumerateArray())
        {
            bool Valid = IntegralOffsets && Index % 2 == 1
                ? Component.TryGetInt32(out _)
                : Component.ValueKind == JsonValueKind.Number && Component.TryGetDouble(out double Number) && double.IsFinite(Number);
            if (!Valid)
                throw Error(GargantuanErrorCode.InvalidArgument, "Property components must be finite numbers with integer UDim offsets.");
            Index++;
        }
    }

    private static void RequireNoValueIdentity(
        string? Enum, string? SchemaId, uint? DefinitionVersion, StudioObjectIdentity? Object) =>
        RequireValueShape(true, Enum is null && SchemaId is null && DefinitionVersion is null && Object is null);

    private static void RequireValueShape(bool ValueValid, bool IdentityValid)
    {
        if (!ValueValid || !IdentityValid)
            throw Error(GargantuanErrorCode.InvalidArgument, "Property value fields do not match its Type.");
    }

    private static string RequireInputString(string Value, string Name, int MaximumBytes)
    {
        if (string.IsNullOrWhiteSpace(Value) || Encoding.UTF8.GetByteCount(Value) > MaximumBytes)
            throw Error(GargantuanErrorCode.InvalidArgument, $"{Name} is invalid or exceeds its UTF-8 byte bound.");
        return Value;
    }

    private static void ValidateExpectedRevision(long? ExpectedRevision)
    {
        if (ExpectedRevision is <= 0)
            throw Error(GargantuanErrorCode.InvalidArgument, "ExpectedRevision must be a positive project revision.");
    }

    private static void ValidateWriteRequestSize<T>(T Request)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(Request).Length > McpLimits.MaximumProjectWriteRequestBytes)
            throw Error(GargantuanErrorCode.ResourceLimit, "ProjectWrite request exceeds its encoded byte bound.");
    }

    private async Task<T> InvokeAsync<T>(AdapterCapability Capability, Func<Task<T>> Operation, CancellationToken CancellationToken)
    {
        RequireCapability(Capability);
        try
        {
            return await Operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException Exception)
        {
            throw new GargantuanAdapterException(GargantuanErrorCode.Unavailable, "The Studio operation ended before completion.", Exception);
        }
        catch (StudioBridgeException Exception)
        {
            throw MapBridgeException(Exception);
        }
        catch (GargantuanAdapterException)
        {
            throw;
        }
        catch (Exception Exception)
        {
            throw new GargantuanAdapterException(GargantuanErrorCode.InternalError, "The Studio bridge operation failed.", Exception);
        }
    }

    private PagedResult<TOutput> ConvertPage<TInput, TOutput>(StudioPage<TInput> Page, int PageSize, string Scope, PageCursor Cursor, Func<TInput, TOutput> Convert)
    {
        if (Cursor.SnapshotVersion is { } ExpectedVersion && Page.SnapshotVersion != ExpectedVersion)
            throw Error(GargantuanErrorCode.Conflict, "The Studio snapshot changed during pagination.");
        if (Page.Items.Count > PageSize + 1)
            throw Error(GargantuanErrorCode.ResourceLimit, "Studio returned more items than the bounded request allowed.");
        TOutput[] Converted = Page.Items.Take(PageSize).Select(Convert).ToArray();
        bool HasMore = Page.Items.Count > PageSize;
        string? NextPageToken = HasMore ? EncodePageToken(Scope, new PageCursor(Page.SnapshotVersion, checked(Cursor.Offset + PageSize))) : null;
        return new PagedResult<TOutput>(Converted, NextPageToken);
    }

    private InstanceSummary ConvertSummary(StudioInstanceSummary Item)
    {
        ValidateNativeIdentity(Item.Id);
        if (Item.ParentId is { } Parent) ValidateNativeIdentity(Parent);
        if (Item.Depth is < 0 or > McpLimits.MaximumRecursionDepth)
            throw BridgeContractError("Studio returned an invalid hierarchy depth.");
        return new InstanceSummary(
            Identities.GetMcpIdentity(Item.Id),
            RequireBridgeString(Item.Name, nameof(Item.Name)),
            RequireBridgeString(Item.ClassName, nameof(Item.ClassName)),
            Item.ParentId is { } ParentId ? Identities.GetMcpIdentity(ParentId) : null,
            Item.Depth);
    }

    private InstanceDetails ConvertDetails(StudioInstanceDetails Item)
    {
        ValidateNativeIdentity(Item.Id);
        if (Item.ParentId is { } Parent) ValidateNativeIdentity(Parent);
        if (Item.Properties.Count > McpLimits.MaximumPropertyCount || Item.Attributes.Count > McpLimits.MaximumPropertyCount || Item.Tags.Count > McpLimits.MaximumPropertyCount)
            throw Error(GargantuanErrorCode.ResourceLimit, "Studio instance data exceeds the MCP collection bounds.");
        Dictionary<string, PropertyValue> Properties = Item.Properties
            .OrderBy(Pair => Pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                Pair => RequireBridgeString(Pair.Key, "PropertyName"),
                Pair => new PropertyValue(RequireBridgeString(Pair.Value.Type, "PropertyType"), RequireBridgeString(Pair.Value.Value, "PropertyValue")),
                StringComparer.Ordinal);
        Dictionary<string, string> Attributes = Item.Attributes
            .OrderBy(Pair => Pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                Pair => RequireBridgeString(Pair.Key, "AttributeName"),
                Pair => RequireBridgeString(Pair.Value, "AttributeValue"),
                StringComparer.Ordinal);
        string[] Tags = Item.Tags.Select(Value => RequireBridgeString(Value, "Tag")).Order(StringComparer.Ordinal).ToArray();
        if (Tags.Distinct(StringComparer.Ordinal).Count() != Tags.Length)
            throw BridgeContractError("Studio returned duplicate tags.");
        return new InstanceDetails(
            Identities.GetMcpIdentity(Item.Id),
            RequireBridgeString(Item.Name, nameof(Item.Name)),
            RequireBridgeString(Item.ClassName, nameof(Item.ClassName)),
            Item.ParentId is { } ParentId ? Identities.GetMcpIdentity(ParentId) : null,
            Properties,
            Attributes,
            Tags,
            OptionalBridgeString(Item.CustomSchemaId, nameof(Item.CustomSchemaId)));
    }

    private static ClassSummary ConvertClassSummary(StudioClassSummary Item) => new(
        RequireBridgeString(Item.Id, nameof(Item.Id)),
        RequireBridgeString(Item.Name, nameof(Item.Name)),
        OptionalBridgeString(Item.BaseClassId, nameof(Item.BaseClassId)),
        Item.Constructible,
        RequireBridgeString(Item.Provenance, nameof(Item.Provenance)));

    private static PropertyMetadata ConvertPropertyMetadata(StudioPropertyMetadata Item) => new(
        RequireBridgeString(Item.Name, nameof(Item.Name)),
        RequireBridgeString(Item.Type, nameof(Item.Type)),
        Item.ReadOnly,
        OptionalBridgeString(Item.EnumId, nameof(Item.EnumId)));

    private IReadOnlyList<ObjectIdentity> ConvertSelection(IReadOnlyList<StudioObjectIdentity> Selection)
    {
        ValidateSelectionCount(Selection.Count);
        if (Selection.Distinct().Count() != Selection.Count)
            throw BridgeContractError("Studio returned duplicate selection identifiers.");
        foreach (StudioObjectIdentity Id in Selection) ValidateNativeIdentity(Id);
        return Selection.Select(Identities.GetMcpIdentity).ToArray();
    }

    private void RequireCapability(AdapterCapability Capability)
    {
        if (!Descriptor.Capabilities.Contains(Capability))
            throw Error(GargantuanErrorCode.CapabilityUnavailable, $"The Studio session does not provide {Capability}.");
    }

    private static void ValidatePage(int PageSize, string? PageToken)
    {
        if (PageSize is < 1 or > McpLimits.MaximumPageSize)
            throw Error(GargantuanErrorCode.ResourceLimit, $"PageSize must be between 1 and {McpLimits.MaximumPageSize}.");
        if (PageToken?.Length > McpLimits.MaximumContinuationTokenLength)
            throw Error(GargantuanErrorCode.InvalidArgument, "Continuation token is invalid.");
    }

    private static void ValidateOptionalQuery(string? Value, string Name)
    {
        if (Value?.Length > McpLimits.MaximumQueryLength)
            throw Error(GargantuanErrorCode.ResourceLimit, $"{Name} exceeds {McpLimits.MaximumQueryLength} characters.");
    }

    private static void ValidateClassId(string ClassId)
    {
        if (string.IsNullOrWhiteSpace(ClassId) || ClassId.Length > McpLimits.MaximumStringLength)
            throw Error(GargantuanErrorCode.InvalidArgument, "ClassId is invalid.");
    }

    private static void ValidateSelectionCount(int Count)
    {
        if (Count > McpLimits.MaximumSelectionSize)
            throw Error(GargantuanErrorCode.ResourceLimit, $"Selection exceeds {McpLimits.MaximumSelectionSize} objects.");
    }

    private static void ValidateNativeIdentity(StudioObjectIdentity Id)
    {
        if (!Id.IsValid)
            throw BridgeContractError("Studio returned an invalid object identity.");
    }

    private string GetScope(params string?[] Values)
    {
        using MemoryStream Canonical = new();
        using (BinaryWriter Writer = new(Canonical, Encoding.UTF8, true))
        {
            Writer.Write(1);
            WriteScopeValue(Writer, "Gargantuan.Mcp.PageScope");
            Writer.Write(checked(Values.Length + 1));
            WriteScopeValue(Writer, SessionId);
            foreach (string? Value in Values) WriteScopeValue(Writer, Value);
        }
        return Convert.ToHexString(SHA256.HashData(Canonical.GetBuffer().AsSpan(0, checked((int)Canonical.Length))));
    }

    private static void WriteScopeValue(BinaryWriter Writer, string? Value)
    {
        if (Value is null)
        {
            Writer.Write(-1);
            return;
        }
        byte[] Bytes = Encoding.UTF8.GetBytes(Value);
        Writer.Write(Bytes.Length);
        Writer.Write(Bytes);
    }

    private PageCursor DecodePageToken(string? Token, string Scope)
    {
        if (Token is null)
            return new PageCursor(null, 0);
        try
        {
            string Value = Encoding.UTF8.GetString(Convert.FromBase64String(Token));
            string[] Parts = Value.Split(':');
            if (Parts.Length != 4 || Parts[0] != "2" || Parts[1] != Scope ||
                !ulong.TryParse(Parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong SnapshotVersion) ||
                !int.TryParse(Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int Offset) ||
                Offset is < 0 or > MaximumPaginationOffset)
            {
                throw new FormatException();
            }
            return new PageCursor(SnapshotVersion, Offset);
        }
        catch (Exception Exception) when (Exception is FormatException or ArgumentException)
        {
            throw Error(GargantuanErrorCode.InvalidArgument, "Continuation token is invalid or stale.");
        }
    }

    private static string EncodePageToken(string Scope, PageCursor Cursor)
    {
        if (Cursor.SnapshotVersion is not { } SnapshotVersion)
            throw BridgeContractError("Studio page did not provide a snapshot version.");
        string Value = $"2:{Scope}:{SnapshotVersion.ToString(CultureInfo.InvariantCulture)}:{Cursor.Offset.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(Value));
    }

    private static string RequireBridgeString(string Value, string Name)
    {
        if (string.IsNullOrWhiteSpace(Value))
            throw BridgeContractError($"Studio returned an invalid {Name}.");
        if (Value.Length > McpLimits.MaximumStringLength)
            throw Error(GargantuanErrorCode.ResourceLimit, $"Studio {Name} exceeds the MCP string bound.");
        return Value;
    }

    private static string? OptionalBridgeString(string? Value, string Name) =>
        Value is null ? null : RequireBridgeString(Value, Name);

    private static GargantuanAdapterException MapBridgeException(StudioBridgeException Exception)
    {
        GargantuanErrorCode Code = Exception.Code switch
        {
            StudioBridgeErrorCode.InvalidArgument => GargantuanErrorCode.InvalidArgument,
            StudioBridgeErrorCode.NotFound => GargantuanErrorCode.NotFound,
            StudioBridgeErrorCode.Unavailable => GargantuanErrorCode.Unavailable,
            StudioBridgeErrorCode.PermissionDenied => GargantuanErrorCode.PermissionDenied,
            StudioBridgeErrorCode.Conflict => GargantuanErrorCode.Conflict,
            StudioBridgeErrorCode.StaleIdentity => GargantuanErrorCode.StaleIdentity,
            StudioBridgeErrorCode.CapabilityUnavailable => GargantuanErrorCode.CapabilityUnavailable,
            StudioBridgeErrorCode.CommandUnavailable => GargantuanErrorCode.CommandUnavailable,
            StudioBridgeErrorCode.ValidationFailed => GargantuanErrorCode.ValidationFailed,
            StudioBridgeErrorCode.ResourceLimit => GargantuanErrorCode.ResourceLimit,
            StudioBridgeErrorCode.Cancelled => GargantuanErrorCode.Cancelled,
            StudioBridgeErrorCode.InternalError => GargantuanErrorCode.InternalError,
            _ => GargantuanErrorCode.InternalError,
        };
        string Message = Code == GargantuanErrorCode.InternalError
            ? "The Studio bridge operation failed."
            : BoundSafeMessage(Exception.SafeMessage);
        return new GargantuanAdapterException(Code, Message, Exception);
    }

    private static string BoundSafeMessage(string Message)
    {
        if (string.IsNullOrWhiteSpace(Message)) return "The Studio bridge rejected the operation.";
        return Message.Length <= MaximumSafeMessageLength ? Message : Message[..MaximumSafeMessageLength];
    }

    private static GargantuanAdapterException BridgeContractError(string Message) =>
        new(GargantuanErrorCode.InternalError, Message);

    private static GargantuanAdapterException Error(GargantuanErrorCode Code, string Message) => new(Code, Message);

    private readonly record struct PageCursor(ulong? SnapshotVersion, int Offset);

    private sealed class StudioIdentityMap
    {
        private readonly object Sync = new();
        private readonly Dictionary<StudioObjectIdentity, ObjectIdentity> ByStudio = [];
        private readonly Dictionary<string, StudioObjectIdentity> ByMcp = new(StringComparer.Ordinal);
        private ulong NextIdentity = 1;

        public ObjectIdentity GetMcpIdentity(StudioObjectIdentity StudioId)
        {
            ValidateNativeIdentity(StudioId);
            lock (Sync)
            {
                if (ByStudio.TryGetValue(StudioId, out ObjectIdentity Existing)) return Existing;
                ObjectIdentity McpId = new($"{IdentityPrefix}{NextIdentity++:x16}");
                ByStudio.Add(StudioId, McpId);
                ByMcp.Add(McpId.Value, StudioId);
                return McpId;
            }
        }

        public StudioObjectIdentity GetStudioIdentity(ObjectIdentity McpId)
        {
            if (!IsWellFormed(McpId.Value))
                throw Error(GargantuanErrorCode.InvalidArgument, "Object identifier is invalid.");
            lock (Sync)
            {
                return ByMcp.TryGetValue(McpId.Value, out StudioObjectIdentity StudioId)
                    ? StudioId
                    : throw Error(GargantuanErrorCode.StaleIdentity, "The object identifier is stale for this Studio session.");
            }
        }

        private static bool IsWellFormed(string Value)
        {
            if (string.IsNullOrWhiteSpace(Value) || Value.Length > MaximumIdentityLength || !Value.StartsWith(IdentityPrefix, StringComparison.Ordinal)) return false;
            ReadOnlySpan<char> Suffix = Value.AsSpan(IdentityPrefix.Length);
            return Suffix.Length == 16 && Suffix.ToString().All(Uri.IsHexDigit);
        }
    }
}
