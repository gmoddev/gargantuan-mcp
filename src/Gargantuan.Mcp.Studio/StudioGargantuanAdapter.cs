using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
        string Scope = GetScope("instances", Request.ParentId?.Value ?? string.Empty, Request.RecursiveDepth.ToString(CultureInfo.InvariantCulture), Request.ClassFilter ?? string.Empty, Request.NameQuery ?? string.Empty);
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

    private string GetScope(params string[] Values)
    {
        string Input = string.Join('\u001f', new[] { SessionId }.Concat(Values));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Input)))[..24];
    }

    private PageCursor DecodePageToken(string? Token, string Scope)
    {
        if (Token is null)
            return new PageCursor(null, 0);
        try
        {
            string Value = Encoding.UTF8.GetString(Convert.FromBase64String(Token));
            string[] Parts = Value.Split(':');
            if (Parts.Length != 4 || Parts[0] != "1" || Parts[1] != Scope ||
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
        string Value = $"1:{Scope}:{SnapshotVersion.ToString(CultureInfo.InvariantCulture)}:{Cursor.Offset.ToString(CultureInfo.InvariantCulture)}";
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
            StudioBridgeErrorCode.ResourceLimit => GargantuanErrorCode.ResourceLimit,
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
