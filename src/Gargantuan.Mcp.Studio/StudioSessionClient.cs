using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Gargantuan.Mcp.Contracts;

namespace Gargantuan.Mcp.Studio;

public sealed class StudioSessionClient : IStudioSessionClient, IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    public const int MaximumRequestBytes = 64 * 1024;
    public const int MaximumResponseBytes = 1024 * 1024;
    public const int MaximumConcurrentOperations = 4;
    public static readonly TimeSpan ConnectionDeadline = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);

    private const int MaximumDescriptorBytes = 16 * 1024;
    private const int MaximumPipeNameLength = 256;
    private const int MaximumSessionIdLength = 128;
    private const int MaximumTokenLength = 128;
    private const int MaximumRequestIdLength = 128;
    private const int MaximumMethodLength = 64;
    private const int MaximumSafeMessageLength = 512;
    private const int MaximumWireDepth = 32;
    private const int MaximumPaginationItems = 201;
    private const int MaximumSchemaInheritance = 128;

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        MaxDepth = MaximumWireDepth,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string PipeName;
    private readonly string SessionId;
    private readonly byte[] TokenBytes;
    private readonly SemaphoreSlim Concurrency = new(MaximumConcurrentOperations, MaximumConcurrentOperations);
    private readonly CancellationTokenSource Lifetime = new();
    private readonly ConcurrentDictionary<int, NamedPipeClientStream> ActivePipes = new();
    private int NextPipeId;
    private int Disposed;

    private StudioSessionClient(StudioBridgeDescriptor Descriptor)
    {
        PipeName = Descriptor.PipeName;
        SessionId = Descriptor.SessionId;
        TokenBytes = Descriptor.TokenBytes;
    }

    public static async Task<StudioSessionClient> CreateAsync(string DescriptorPath, CancellationToken CancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new StudioBridgeException(StudioBridgeErrorCode.CapabilityUnavailable, "The Studio named-pipe bridge requires Windows.");
        StudioBridgeDescriptor Descriptor = await ReadDescriptorAsync(DescriptorPath, CancellationToken).ConfigureAwait(false);
        return new StudioSessionClient(Descriptor);
    }

    public async Task<StudioSessionDescriptor> DescribeSessionAsync(CancellationToken CancellationToken)
    {
        WireSessionDescriptor Wire = await InvokeAsync<WireSessionDescriptor>("DescribeSession", null, CancellationToken).ConfigureAwait(false);
        RequireWireString(Wire.SessionId, nameof(Wire.SessionId), MaximumSessionIdLength);
        if (!CryptographicEquals(Wire.SessionId, SessionId))
            throw ContractError("Studio returned a different session identity.");
        string Name = RequireWireString(Wire.Name, nameof(Wire.Name), McpLimits.MaximumStringLength);
        string Version = RequireWireString(Wire.Version, nameof(Wire.Version), McpLimits.MaximumStringLength);
        if (Version != ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw ContractError("Studio returned an unsupported bridge semantic version.");
        if (Wire.Capabilities is null || Wire.Capabilities.Length > Enum.GetValues<StudioBridgeCapability>().Length)
            throw ContractError("Studio returned an invalid capability set.");
        HashSet<StudioBridgeCapability> Capabilities = [];
        foreach (string Value in Wire.Capabilities)
        {
            if (!Enum.TryParse(Value, false, out StudioBridgeCapability Capability) || !Capabilities.Add(Capability))
                throw ContractError("Studio returned an invalid capability set.");
        }
        return new StudioSessionDescriptor(SessionId, Name, Version, Capabilities);
    }

    public Task<StudioProjectInfo> GetProjectInfoAsync(CancellationToken CancellationToken) =>
        InvokeAsync<StudioProjectInfo>("GetProjectInfo", null, CancellationToken);

    public Task<StudioPage<StudioInstanceSummary>> ListInstancesAsync(StudioListInstancesRequest Request, CancellationToken CancellationToken) =>
        InvokePageAsync<StudioInstanceSummary>("ListInstances", new JsonObject
        {
            ["ParentId"] = Request.ParentId is { } ParentId ? Identity(ParentId) : null,
            ["RecursiveDepth"] = Request.RecursiveDepth,
            ["ClassFilter"] = Request.ClassFilter,
            ["NameQuery"] = Request.NameQuery,
            ["Offset"] = Request.Offset,
            ["Limit"] = Request.Limit,
            ["MaximumCandidates"] = Request.MaximumCandidates,
            ["ExpectedSnapshotVersion"] = Request.ExpectedSnapshotVersion,
        }, CancellationToken);

    public Task<StudioInstanceDetails> GetInstanceAsync(StudioObjectIdentity Id, CancellationToken CancellationToken) =>
        InvokeAsync<StudioInstanceDetails>("GetInstance", new JsonObject { ["Id"] = Identity(Id) }, CancellationToken);

    public Task<StudioPage<StudioInstanceSummary>> GetChildrenAsync(StudioGetChildrenRequest Request, CancellationToken CancellationToken) =>
        InvokePageAsync<StudioInstanceSummary>("GetChildren", new JsonObject
        {
            ["ParentId"] = Identity(Request.ParentId),
            ["Offset"] = Request.Offset,
            ["Limit"] = Request.Limit,
            ["ExpectedSnapshotVersion"] = Request.ExpectedSnapshotVersion,
        }, CancellationToken);

    public Task<StudioPage<StudioClassSummary>> ListClassesAsync(StudioListClassesRequest Request, CancellationToken CancellationToken) =>
        InvokePageAsync<StudioClassSummary>("ListClasses", new JsonObject
        {
            ["Offset"] = Request.Offset,
            ["Limit"] = Request.Limit,
            ["ExpectedSnapshotVersion"] = Request.ExpectedSnapshotVersion,
        }, CancellationToken);

    public Task<StudioClassDetails> GetClassAsync(string ClassId, CancellationToken CancellationToken) =>
        InvokeAsync<StudioClassDetails>("GetClass", new JsonObject { ["ClassId"] = ClassId }, CancellationToken);

    public async Task<IReadOnlyList<StudioObjectIdentity>> GetSelectionAsync(CancellationToken CancellationToken) =>
        await InvokeAsync<StudioObjectIdentity[]>("GetSelection", null, CancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<StudioObjectIdentity>> SetSelectionAsync(IReadOnlyList<StudioObjectIdentity> Selection, CancellationToken CancellationToken)
    {
        JsonArray Encoded = new(Selection.Select(Id => (JsonNode?)Identity(Id)).ToArray());
        return await InvokeAsync<StudioObjectIdentity[]>("SetSelection", new JsonObject { ["Selection"] = Encoded }, CancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref Disposed, 1) != 0) return;
        Lifetime.Cancel();
        foreach (NamedPipeClientStream Pipe in ActivePipes.Values)
        {
            try { Pipe.Dispose(); } catch { }
        }
        for (int Index = 0; Index < MaximumConcurrentOperations; Index++)
            await Concurrency.WaitAsync().ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(TokenBytes);
        Concurrency.Dispose();
        Lifetime.Dispose();
    }

    private async Task<StudioPage<T>> InvokePageAsync<T>(string Method, JsonObject Parameters, CancellationToken CancellationToken)
    {
        StudioPage<T> Page = await InvokeAsync<StudioPage<T>>(Method, Parameters, CancellationToken).ConfigureAwait(false);
        if (Page.Items is null || Page.Items.Count > MaximumPaginationItems || Page.SnapshotVersion == 0)
            throw ContractError("Studio returned an invalid bounded page.");
        return Page;
    }

    private async Task<T> InvokeAsync<T>(string Method, JsonObject? Parameters, CancellationToken CancellationToken)
    {
        if (Volatile.Read(ref Disposed) != 0)
            throw new StudioBridgeException(StudioBridgeErrorCode.Unavailable, "The Studio bridge client is closed.");
        if (string.IsNullOrWhiteSpace(Method) || Method.Length > MaximumMethodLength)
            throw new ArgumentException("The Studio bridge method is invalid.", nameof(Method));

        using CancellationTokenSource RequestLifetime = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, Lifetime.Token);
        RequestLifetime.CancelAfter(RequestDeadline);
        bool Acquired = false;
        try
        {
            await Concurrency.WaitAsync(RequestLifetime.Token).ConfigureAwait(false);
            Acquired = true;
            string RequestId = Guid.NewGuid().ToString("N");
            JsonObject Request = new()
            {
                ["Version"] = ProtocolVersion,
                ["RequestId"] = RequestId,
                ["SessionId"] = SessionId,
                ["Token"] = Convert.ToBase64String(TokenBytes),
                ["Method"] = Method,
                ["Parameters"] = Parameters,
            };
            byte[] Payload = JsonSerializer.SerializeToUtf8Bytes(Request, StrictJson);
            if (Payload.Length == 0 || Payload.Length > MaximumRequestBytes)
                throw new StudioBridgeException(StudioBridgeErrorCode.ResourceLimit, "The Studio bridge request exceeds its size bound.");

            await using NamedPipeClientStream Pipe = new(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
            int PipeId = Interlocked.Increment(ref NextPipeId);
            ActivePipes[PipeId] = Pipe;
            try
            {
                using (CancellationTokenSource ConnectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(RequestLifetime.Token))
                {
                    ConnectionLifetime.CancelAfter(ConnectionDeadline);
                    await Pipe.ConnectAsync(ConnectionLifetime.Token).ConfigureAwait(false);
                }
                await WriteFrameAsync(Pipe, Payload, RequestLifetime.Token).ConfigureAwait(false);
                byte[] ResponsePayload = await ReadFrameAsync(Pipe, RequestLifetime.Token).ConfigureAwait(false);
                return ParseResponse<T>(ResponsePayload, RequestId);
            }
            finally
            {
                ActivePipes.TryRemove(PipeId, out _);
            }
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException Exception) when (Lifetime.IsCancellationRequested)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.Unavailable, "The Studio bridge client is closed.", Exception);
        }
        catch (OperationCanceledException Exception)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.Unavailable, "The Studio bridge request timed out.", Exception);
        }
        catch (StudioBridgeException)
        {
            throw;
        }
        catch (UnauthorizedAccessException Exception)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.PermissionDenied, "The Studio named pipe rejected the current user.", Exception);
        }
        catch (Exception Exception) when (Exception is IOException or EndOfStreamException or ObjectDisposedException or InvalidOperationException)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.Unavailable, "The Studio bridge is unavailable.", Exception);
        }
        catch (JsonException Exception)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.InternalError, "The Studio bridge response was invalid.", Exception);
        }
        catch (Exception Exception)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.InternalError, "The Studio bridge request failed.", Exception);
        }
        finally
        {
            if (Acquired) Concurrency.Release();
        }
    }

    private static T ParseResponse<T>(byte[] Payload, string RequestId)
    {
        WireResponse Response;
        try
        {
            Response = JsonSerializer.Deserialize<WireResponse>(Payload, StrictJson)
                ?? throw new JsonException("The Studio bridge response is empty.");
        }
        catch (JsonException Exception)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.InternalError, "The Studio bridge response was invalid.", Exception);
        }
        if (Response.Version != ProtocolVersion || Response.RequestId is null ||
            Response.RequestId.Length > MaximumRequestIdLength || !CryptographicEquals(Response.RequestId, RequestId))
            throw ContractError("Studio returned an invalid response envelope.");

        if (!Response.Ok)
        {
            if (Response.Result is not null || Response.Error is null ||
                !Enum.TryParse(Response.Error.Code, false, out StudioBridgeErrorCode Code))
                throw ContractError("Studio returned an invalid error envelope.");
            string Message = Code == StudioBridgeErrorCode.InternalError
                ? "The Studio bridge request failed."
                : BoundSafeMessage(Response.Error.Message);
            throw new StudioBridgeException(Code, Message);
        }
        if (Response.Error is not null || Response.Result is null)
            throw ContractError("Studio returned an invalid success envelope.");
        try
        {
            return Response.Result.Value.Deserialize<T>(StrictJson)
                ?? throw new JsonException("The Studio bridge result is empty.");
        }
        catch (JsonException Exception)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.InternalError, "The Studio bridge result was invalid.", Exception);
        }
    }

    private static async Task WriteFrameAsync(Stream Stream, byte[] Payload, CancellationToken CancellationToken)
    {
        byte[] Header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(Header, Payload.Length);
        await Stream.WriteAsync(Header.AsMemory(), CancellationToken).ConfigureAwait(false);
        await Stream.WriteAsync(Payload.AsMemory(), CancellationToken).ConfigureAwait(false);
        await Stream.FlushAsync(CancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream Stream, CancellationToken CancellationToken)
    {
        byte[] Header = new byte[4];
        await ReadExactlyAsync(Stream, Header, CancellationToken).ConfigureAwait(false);
        int Length = BinaryPrimitives.ReadInt32LittleEndian(Header);
        if (Length <= 0 || Length > MaximumResponseBytes)
            throw ContractError("Studio returned an invalid response frame.");
        byte[] Payload = new byte[Length];
        await ReadExactlyAsync(Stream, Payload, CancellationToken).ConfigureAwait(false);
        return Payload;
    }

    private static async Task ReadExactlyAsync(Stream Stream, byte[] Buffer, CancellationToken CancellationToken)
    {
        int Offset = 0;
        while (Offset != Buffer.Length)
        {
            int Read = await Stream.ReadAsync(Buffer.AsMemory(Offset), CancellationToken).ConfigureAwait(false);
            if (Read == 0) throw new EndOfStreamException("The Studio bridge closed during a response frame.");
            Offset += Read;
        }
    }

    private static async Task<StudioBridgeDescriptor> ReadDescriptorAsync(string DescriptorPath, CancellationToken CancellationToken)
    {
        if (string.IsNullOrWhiteSpace(DescriptorPath) || !Path.IsPathFullyQualified(DescriptorPath))
            throw new StudioBridgeException(StudioBridgeErrorCode.InvalidArgument, "An absolute Studio bridge descriptor path is required.");
        string FullPath;
        try { FullPath = Path.GetFullPath(DescriptorPath); }
        catch (Exception Exception) when (Exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.InvalidArgument, "The Studio bridge descriptor path is invalid.", Exception);
        }
        string LocalRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!FullPath.StartsWith(LocalRoot, StringComparison.OrdinalIgnoreCase))
            throw new StudioBridgeException(StudioBridgeErrorCode.InvalidArgument, "The Studio bridge descriptor must be inside LocalApplicationData.");

        byte[] Payload;
        try
        {
            await using FileStream Stream = new(FullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (Stream.Length is <= 0 or > MaximumDescriptorBytes)
                throw new StudioBridgeException(StudioBridgeErrorCode.InvalidArgument, "The Studio bridge descriptor exceeds its size bound.");
            Payload = new byte[checked((int)Stream.Length)];
            await ReadExactlyAsync(Stream, Payload, CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StudioBridgeException)
        {
            throw;
        }
        catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.Unavailable, "The Studio bridge descriptor is unavailable.", Exception);
        }

        WireDescriptor Wire;
        try
        {
            Wire = JsonSerializer.Deserialize<WireDescriptor>(Payload, StrictJson)
                ?? throw new JsonException("The descriptor is empty.");
        }
        catch (JsonException Exception)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.InvalidArgument, "The Studio bridge descriptor is malformed.", Exception);
        }
        if (Wire.Version != ProtocolVersion || Wire.Transport != "windows-named-pipe" || Wire.ProcessId <= 0)
            throw new StudioBridgeException(StudioBridgeErrorCode.InvalidArgument, "The Studio bridge descriptor is unsupported.");
        string PipeName = RequireDescriptorString(Wire.PipeName, nameof(Wire.PipeName), MaximumPipeNameLength);
        if (PipeName.Any(Character => char.IsControl(Character) || Character is '\\' or '/'))
            throw new StudioBridgeException(StudioBridgeErrorCode.InvalidArgument, "The Studio bridge pipe name is invalid.");
        string SessionId = RequireDescriptorString(Wire.SessionId, nameof(Wire.SessionId), MaximumSessionIdLength);
        byte[] TokenBytes;
        try
        {
            string Token = RequireDescriptorString(Wire.Token, nameof(Wire.Token), MaximumTokenLength);
            TokenBytes = Convert.FromBase64String(Token);
        }
        catch (FormatException Exception)
        {
            throw new StudioBridgeException(StudioBridgeErrorCode.InvalidArgument, "The Studio bridge credential is malformed.", Exception);
        }
        if (TokenBytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(TokenBytes);
            throw new StudioBridgeException(StudioBridgeErrorCode.InvalidArgument, "The Studio bridge credential is malformed.");
        }
        return new StudioBridgeDescriptor(PipeName, SessionId, TokenBytes);
    }

    private static string RequireDescriptorString(string? Value, string Name, int MaximumLength)
    {
        if (string.IsNullOrWhiteSpace(Value) || Value.Length > MaximumLength ||
            Encoding.UTF8.GetByteCount(Value) > MaximumLength * 4)
            throw new StudioBridgeException(StudioBridgeErrorCode.InvalidArgument, $"The Studio bridge descriptor {Name} is invalid.");
        return Value;
    }

    private static string RequireWireString(string? Value, string Name, int MaximumLength)
    {
        if (string.IsNullOrWhiteSpace(Value) || Value.Length > MaximumLength ||
            Encoding.UTF8.GetByteCount(Value) > MaximumLength * 4)
            throw ContractError($"Studio returned an invalid {Name}.");
        return Value;
    }

    private static JsonObject Identity(StudioObjectIdentity Id) => new()
    {
        ["Slot"] = Id.Slot,
        ["Generation"] = Id.Generation,
    };

    private static string BoundSafeMessage(string? Message)
    {
        if (string.IsNullOrWhiteSpace(Message)) return "The Studio bridge rejected the request.";
        return Message.Length <= MaximumSafeMessageLength ? Message : Message[..MaximumSafeMessageLength];
    }

    private static bool CryptographicEquals(string Left, string Right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Left), Encoding.UTF8.GetBytes(Right));

    private static StudioBridgeException ContractError(string Message) =>
        new(StudioBridgeErrorCode.InternalError, Message);

    private sealed record StudioBridgeDescriptor(string PipeName, string SessionId, byte[] TokenBytes);

    private sealed class WireDescriptor
    {
        public required int Version { get; init; }
        public required string Transport { get; init; }
        public required string PipeName { get; init; }
        public required string SessionId { get; init; }
        public required string Token { get; init; }
        public required int ProcessId { get; init; }
    }

    private sealed class WireSessionDescriptor
    {
        public required string SessionId { get; init; }
        public required string Name { get; init; }
        public required string Version { get; init; }
        public required string[] Capabilities { get; init; }
    }

    private sealed class WireResponse
    {
        public required int Version { get; init; }
        public string? RequestId { get; init; }
        public required bool Ok { get; init; }
        public JsonElement? Result { get; init; }
        public WireError? Error { get; init; }
    }

    private sealed class WireError
    {
        public required string Code { get; init; }
        public required string Message { get; init; }
    }
}
