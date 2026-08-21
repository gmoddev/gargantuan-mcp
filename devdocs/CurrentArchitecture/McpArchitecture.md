# MCP Architecture — Foundation 2

## Ownership and boundary

`Gargantuan.Mcp` owns MCP protocol hosting, startup composition, policy, and translation. `Gargantuan.Mcp.Contracts` owns protocol-independent Gargantuan development semantics. `Gargantuan.Mcp.Mock` owns deterministic mock state. `Gargantuan.Mcp.Studio` owns the real adapter plus the bounded client for Studio's committed named-pipe bridge. There is no dependency on the C++ engine, EditorHost, or Gargantuan Studio implementation assembly.

```text
MCP client
  <-> stdio / MCP 2026-07-28
Gargantuan.Mcp (validation, policy, limits, error confinement)
  -> semantic requests
IGargantuanAdapter
  -> MockGargantuanAdapter (default standalone mode)
  -> StudioGargantuanAdapter (implemented semantics)
       -> StudioSessionClient / IStudioSessionClient
            -> explicit descriptor + authenticated current-user named pipe
            -> Studio-owned services/capability boundary
            -> EditorHost
            -> authoritative engine operation
```

MCP protocol types do not appear in `IGargantuanAdapter`, `StudioGargantuanAdapter`, or `IStudioSessionClient`. The adapters expose project, hierarchy, schema, and selection concepts. Studio mode is selected only by local startup argument `--studio-bridge-descriptor <absolute path>`. Without it the composition root constructs `MockGargantuanAdapter`. With it the root validates the one descriptor, constructs `StudioSessionClient`, negotiates `StudioGargantuanAdapter`, and registers tools from the capability/policy intersection. Project data and MCP requests cannot select or replace adapters.

The live protocol is Studio-owned version 1: a descriptor under `LocalApplicationData` contains exact transport, random pipe name, immutable session ID, process ID, and 256-bit token. MCP validates a 16 KiB descriptor bound, exact fields, transport/version, pipe/session lengths, positive process ID, and exact token size. It reads the explicit file once with delete-compatible sharing. It does not retain the descriptor handle, re-read it, enumerate directories, or inspect the named process.

Each semantic call opens one Windows `CurrentUserOnly` byte-mode named-pipe connection, sends one four-byte little-endian length-prefixed UTF-8 JSON request, and reads one response. Every request repeats the session ID and token. Requests are capped at 64 KiB, responses at 1 MiB, JSON depth at 32, client concurrency at four, connection establishment at five seconds, and the complete request at 30 seconds. The token is never logged. Caller cancellation propagates; timeout, closure, disconnect, or Studio exit maps to bounded `Unavailable`.

## Transport and lifecycle

The default and only transport is stdio through official C# SDK 2.0.0, pinned to MCP `2026-07-28`. Peers use discovery-first per-request metadata. stdin EOF drives prompt shutdown. Cancellation tokens flow from MCP handlers through the concurrency gate into adapters. Console logging is configured entirely to stderr; stdout belongs exclusively to protocol framing. No listener, port, or background HTTP host exists.

## Identity

`ObjectIdentity` is an opaque adapter-owned string. Names, paths, and pointers are never identities. The mock `gtn_*` values exist solely for deterministic tests. The Studio adapter assigns session-local `gtn_studio_*` handles to validated Studio `(Slot, Generation)` identities and maintains a bidirectional map. Unknown well-formed handles are `StaleIdentity`; malformed handles are `InvalidArgument`; a stale native generation reported by Studio is also `StaleIdentity`. Native slot/generation values never enter MCP results or continuation tokens.

## Tools, capabilities, and policy

Tools are grouped as Read, StudioLocalWrite, ProjectWrite, DestructiveWrite, and Execution. The local server policy returns Allow, Deny, or RequireApproval. Reads default to Allow; StudioLocalWrite and ProjectWrite default to RequireApproval; destructive writes and execution default to Deny. Foundation 1 has no approval UI, so RequireApproval tools are not advertised unless explicit local startup configuration permits them.

Discovery is the intersection of adapter capability and server policy. `ToolRegistrationPolicy` is the common registration decision. The mock advertises inspection plus selection-write capability. The Studio adapter maps only capabilities returned by `IStudioSessionClient.DescribeSessionAsync`; it never infers support from target tools or request metadata. All read tools remain active in default mock mode; `studio.set_selection` is registered only when the adapter reports `SelectionWrite` and local startup policy allows it. Neither MCP arguments, request metadata, project data, nor the bridge can elevate local policy.

Each `StudioSessionClient` represents one negotiated Studio session. Its pipe, session identity, token, capability set, and opaque identity scope are immutable for that adapter lifetime. Studio replacement closes the old endpoint and publishes fresh credentials. The old MCP process continues using only the old in-memory credentials and therefore returns `Unavailable`; only a newly started MCP process given the replacement descriptor can attach.

## Bounds

| Boundary | Limit |
| --- | ---: |
| Default / maximum page | 50 / 200 |
| Search candidates | 1,000 |
| Recursive depth | 8 |
| Serialized semantic response | 512 KiB |
| Properties per instance | 128 |
| General string | 4,096 characters |
| Search query | 256 characters |
| Selection | 128 objects |
| Concurrent tool operations | 8 |
| Continuation token | 256 characters |

Collections are deterministically ordered by Studio before its bounded offset/limit window. The adapter requests at most `PageSize + 1` records and converts at most `PageSize`. Instance search also passes the 1,000-candidate ceiling. Continuation tokens are adapter-owned, bounded opaque Base64 cursors scoped by session, operation, and arguments using a one-way scope digest. They carry only snapshot version and offset. Studio must reject an `ExpectedSnapshotVersion` mismatch; the adapter independently rejects a returned version change as `Conflict`. Invalid and cross-scope tokens fail safely. Tokens contain no native identity, pointer, path, authority, or secret.

## Error semantics

Stable semantic codes are `InvalidArgument`, `NotFound`, `Unavailable`, `PermissionDenied`, `Conflict`, `StaleIdentity`, `CapabilityUnavailable`, `ResourceLimit`, and `InternalError`. The Studio bridge has an isomorphic closed error set. Known safe failures map explicitly; bridge `InternalError`, unexpected exceptions, and non-request cancellation receive generic bounded messages. Request cancellation propagates unchanged. Active tools return a `ToolResponse<T>` envelope with either result or bounded error. The outer tool boundary remains a second confinement layer and never returns exception types, stack traces, filesystem paths, environment values, or tokens.

## Implementations and verification

The deterministic mock project remains unchanged for standalone development and protocol contract tests. The deterministic fake semantic bridge exists only in `Gargantuan.Mcp.Tests` and proves adapter behavior without Studio. Descriptor/client tests cover explicit path validation and cancellation. `Gargantuan.Mcp.LiveStudioSmoke` is the production process proof: official MCP client -> MCP stdio executable -> `StudioSessionClient` -> Studio's production named-pipe host -> the live EditorHost-backed Studio projection. It covers all Foundation 2 tools, wrong-token startup, Studio shutdown, replacement isolation, and fresh replacement attachment.

## Authority invariant

The MCP server cannot directly acquire or manufacture `MutationAuthorityContext`, `ScriptSecurityContext`, engine capabilities, raw DataModel pointers, schema authority, or networking authority. Foundation 2's only write is Studio-local selection through the bridge after both local MCP policy and the negotiated live capability allow it. Future project-authoring success must mean authoritative engine acceptance observed through Studio, not a local MCP cache update.
