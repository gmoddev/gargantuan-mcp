# MCP Architecture — ProjectWrite Foundation

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
            -> Studio-owned services/capability boundary and shared command runner
            -> EditorHost transaction -> MutationGateway
            -> authoritative engine state -> ordered journal
            -> StudioDocument reconciliation
```

MCP protocol types do not appear in `IGargantuanAdapter`, `StudioGargantuanAdapter`, or `IStudioSessionClient`. The adapters expose project, hierarchy, schema, and selection concepts. Studio mode is selected only by local startup argument `--studio-bridge-descriptor <absolute path>`. Without it the composition root constructs `MockGargantuanAdapter`. With it the root validates the one descriptor, constructs `StudioSessionClient`, negotiates `StudioGargantuanAdapter`, and registers tools from the capability/policy intersection. Project data and MCP requests cannot select or replace adapters.

The live protocol is Studio-owned version 1: a descriptor under `LocalApplicationData` contains exact transport, random pipe name, immutable session ID, process ID, and 256-bit token. MCP validates a 16 KiB descriptor bound, exact fields, transport/version, pipe/session lengths, positive process ID, and exact token size. It rejects a symbolic-link or Windows-reparse component at the root, every existing parent, and the leaf before reading the explicit file once with delete-compatible sharing. It does not retain the descriptor handle, re-read it, enumerate directories, or inspect the named process.

Each semantic call opens one Windows `CurrentUserOnly` byte-mode named-pipe connection, sends one four-byte little-endian length-prefixed UTF-8 JSON request, and reads one response. Every request repeats the session ID and token. Requests are capped at 64 KiB, responses at 1 MiB, JSON depth at 32, client concurrency at four, connection establishment at five seconds, and the complete request at 30 seconds. The token is never logged. Caller cancellation propagates into Studio's command runner. Cancellation before the authoritative commit point cancels normally; after commit the bridge returns the authoritative result rather than claiming rollback. Timeout, closure, disconnect, or Studio exit before completion maps to bounded `Unavailable`.

## Transport and lifecycle

The default and only transport is stdio through official C# SDK 2.0.0, pinned to MCP `2026-07-28`. Peers use discovery-first per-request metadata. stdin EOF drives prompt shutdown. Cancellation tokens flow from MCP handlers through the concurrency gate into adapters. Console logging is configured entirely to stderr; stdout belongs exclusively to protocol framing. No listener, port, or background HTTP host exists.

## Identity

`ObjectIdentity` is an opaque adapter-owned string. Names, paths, and pointers are never identities. The mock `gtn_*` values exist solely for deterministic tests. The Studio adapter assigns session-local `gtn_studio_*` handles to validated Studio `(Slot, Generation)` identities and maintains a bidirectional map. Unknown well-formed handles are `StaleIdentity`; malformed handles are `InvalidArgument`; a stale native generation reported by Studio is also `StaleIdentity`. Native slot/generation values never enter MCP results or continuation tokens.

## Tools, capabilities, and policy

Tools are grouped as Read, StudioLocalWrite, ProjectWrite, DestructiveWrite, and Execution. The local server policy returns Allow, Deny, or RequireApproval. Reads default to Allow; StudioLocalWrite and ProjectWrite default to RequireApproval; destructive writes and execution default to Deny. There is no approval UI, so RequireApproval tools are not advertised unless explicit local startup configuration permits them. `--allow-studio-local-write`, `--allow-project-write`, and `--allow-destructive-write` are separate grants; none implies another.

Discovery is the intersection of adapter capability and server policy. `ToolRegistrationPolicy` is the common registration decision. The mock advertises inspection plus selection-write capability but never ProjectWrite. The Studio adapter maps only capabilities returned by `IStudioSessionClient.DescribeSessionAsync`; it never infers support from target tools or request metadata. All read tools remain active in default mock mode; `studio.set_selection` is registered only when the adapter reports `SelectionWrite` and local startup policy allows it. The seven non-destructive durable tools are registered only when Studio reports live `ProjectWrite` and local startup supplied `--allow-project-write`. `instance.delete` is registered separately only when that intersection also includes local `--allow-destructive-write`. Direct invocation applies the same two-policy check before calling the adapter. MCP request metadata, project data, and the client cannot elevate either side.

The durable method set is exactly `instance.create`, `instance.delete`, `instance.duplicate`, `instance.reparent`, `instance.set_property`, `project.save`, `studio.undo`, and `studio.redo`. Delete is the only current destructive operation: duplicate always creates fresh identities and no other command overwrites or removes a project object. There is no generic command, reflection, journal, source, filesystem, Play, shell, process, network, credential, or policy mutation route. Writes carry semantic DTOs; clients never supply journal revisions or raw engine identities.

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
| Concurrent ProjectWrite operations | 1 |
| ProjectWrite starts | 20 per rolling second per Studio session |
| Continuation token | 256 characters |
| Serialized ProjectWrite request | 48 KiB |
| Initial create properties | 32 |
| Property string payload | 16 KiB UTF-8 |
| Write diagnostics returned | 8 |

Collections are deterministically ordered by Studio before its bounded offset/limit window. The adapter requests at most `PageSize + 1` records and converts at most `PageSize`. Instance search also passes the 1,000-candidate ceiling. Continuation tokens are adapter-owned, bounded opaque Base64 cursors scoped by session, operation, and arguments using a one-way scope digest. The digest input is a versioned, length-prefixed tuple that preserves null versus empty values and includes the full session and operation scope; caller-controlled separators cannot alias another scope. Tokens carry only snapshot version and offset. Studio must reject an `ExpectedSnapshotVersion` mismatch; the adapter independently rejects a returned version change as `Conflict`. Invalid and cross-scope tokens fail safely. Tokens contain no native identity, pointer, path, authority, or secret.

## Error semantics

Stable semantic codes are `InvalidArgument`, `NotFound`, `StaleIdentity`, `Conflict`, `PermissionDenied`, `CapabilityUnavailable`, `CommandUnavailable`, `ValidationFailed`, `ResourceLimit`, `Cancelled`, `Unavailable`, and `InternalError`. The Studio bridge has an isomorphic closed error set. Known safe failures map explicitly; internal exceptions and unexpected failures receive generic bounded messages. Active tools return a `ToolResponse<T>` envelope with either result or bounded error. The outer tool boundary remains a second confinement layer and never returns exception types, stack traces, filesystem paths, environment values, pipe details, or tokens. Detailed exceptions stay in Studio Output under the bounded `MCP.ProjectWrite` category.

## ProjectWrite transaction and conflict contract

Every durable tool call follows `MCP tool -> MCP validation/policy -> StudioGargantuanAdapter -> authenticated Studio bridge -> StudioCommandRunner -> EditorHost transaction -> MutationGateway -> authoritative state -> ordered journal -> StudioDocument reconciliation`. No layer mutates `StudioDocument` directly. Create attaches a fully validated candidate once; delete destroys the target subtree and invalidates every descendant identity; duplicate uses EditorHost's ordinary recursive clone semantics and returns the fresh root identity; reparent uses the existing cycle/parent validation; and set-property performs exactly one schema-writable native/custom/extension write. Save has no path argument. Undo and redo use the same current-document command stack as manual Studio commands.

Each request optionally carries `expected_revision`. If it differs from the authoritative project revision when EditorHost begins the operation, the command returns `Conflict` and commits nothing. If omitted, the one-at-a-time Studio writer applies the command to the authoritative state current at execution time. The adapter may know an earlier read revision but cannot manufacture or advance one. Object-level revisions are not invented for this foundation.

Property values are strictly typed and schema-validated in Studio/EditorHost, not parsed with MCP-local property-editor heuristics. Supported encodings include null, booleans, 32-bit integers, finite floats/doubles, bounded strings, fixed-length Vector2/Vector3/Color3/UDim/UDim2/CFrame components, stable native and schema enum identities, and opaque object references. Non-finite numbers, path/name object references, runtime/read-only fields, source writes, and malformed or extra fields are rejected.

## Implementations and verification

The deterministic mock project remains unchanged for standalone development and protocol contract tests. The deterministic fake semantic bridge exists only in `Gargantuan.Mcp.Tests` and proves capability intersection, typed validation, errors, conflicts, stale identities, concurrency, and cancellation without Studio. Descriptor/client tests cover explicit path validation and cancellation. `Gargantuan.Mcp.LiveStudioSmoke` is the production process proof: official MCP client -> MCP stdio executable -> `StudioSessionClient` -> Studio's production named-pipe host -> the live EditorHost-backed Studio projection. It covers all inspection and ProjectWrite tools, stale expected-revision rejection with no mutation, wrong-token startup, Studio shutdown/replacement isolation, save, reopen, persisted semantic state, and absence of descriptor/session/token metadata from project files.

## Authority invariant

The MCP server cannot directly acquire or manufacture `MutationAuthorityContext`, `ScriptSecurityContext`, engine capabilities, raw DataModel pointers, schema authority, or networking authority. ProjectWrite success means authoritative engine acceptance observed through Studio's ordered journal, never a local MCP cache update. Studio/project replacement closes the old endpoint, invalidates all old identities and history access, and publishes fresh credentials; the stale MCP process cannot attach to or undo the replacement project.
