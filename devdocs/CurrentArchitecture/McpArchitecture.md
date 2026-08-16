# MCP Architecture — Foundation 1

## Ownership and boundary

`Gargantuan.Mcp` owns MCP protocol hosting and translation. `Gargantuan.Mcp.Contracts` owns protocol-independent Gargantuan development semantics. `Gargantuan.Mcp.Mock` owns deterministic mock state. There is no dependency on the C++ engine or Gargantuan Studio.

```text
MCP client
  <-> stdio / MCP 2026-07-28
Gargantuan.Mcp (validation, policy, limits, error confinement)
  -> semantic requests
IGargantuanAdapter
  -> MockGargantuanAdapter now
  -> StudioGargantuanAdapter later
```

MCP protocol types do not appear in `IGargantuanAdapter`. The adapter exposes project, hierarchy, schema, and selection concepts. Replacing the mock with a future Studio adapter does not change tool semantics.

## Transport and lifecycle

The default and only transport is stdio through official C# SDK 2.0.0, pinned to MCP `2026-07-28`. Peers use discovery-first per-request metadata. stdin EOF drives prompt shutdown. Cancellation tokens flow from MCP handlers through the concurrency gate into adapters. Console logging is configured entirely to stderr; stdout belongs exclusively to protocol framing. No listener, port, or background HTTP host exists.

## Identity

`ObjectIdentity` is an opaque adapter-owned string. Names, paths, and pointers are never identities. The mock `gtn_*` values exist solely for deterministic tests and reveal no engine ObjectId representation. Real integration must resolve stable, generation-safe Gargantuan ObjectIds and return `StaleIdentity` when appropriate.

## Tools, capabilities, and policy

Tools are grouped as Read, StudioLocalWrite, ProjectWrite, DestructiveWrite, and Execution. The local server policy returns Allow, Deny, or RequireApproval. Reads default to Allow; StudioLocalWrite and ProjectWrite default to RequireApproval; destructive writes and execution default to Deny. Foundation 1 has no approval UI, so RequireApproval tools are not advertised unless explicit local startup configuration permits them.

Discovery is the intersection of adapter capability and server policy. The mock advertises inspection plus selection-write capability. All read tools are active; `studio.set_selection` is registered only with `--allow-studio-local-write`. Neither MCP arguments nor project data can change policy. A future adapter must advertise actual Studio/EditorHost capability state.

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

Collections are deterministically ordered before pagination. Continuation tokens are bounded opaque Base64 cursors scoped to the operation. Invalid, cross-scope, or stale tokens fail safely. They contain no pointers or secrets.

## Error semantics

Stable semantic codes are `InvalidArgument`, `NotFound`, `Unavailable`, `PermissionDenied`, `Conflict`, `StaleIdentity`, `CapabilityUnavailable`, `ResourceLimit`, and `InternalError`. Active tools return a `ToolResponse<T>` envelope with either result or bounded error. Adapter exceptions are caught at the tool boundary. Unexpected exceptions are logged to stderr with `[MCP:ToolBoundary]`, while clients receive only a generic bounded error—never exception types, stack traces, filesystem paths, environment values, or tokens.

## Mock state

The deterministic project contains DataModel, Workspace, two Parts, a nested Folder, a custom `GameSpawnMarker`, and small Shared/Server folders. Schema covers native/custom provenance, inheritance, constructibility, properties, and a `Material` enum reference. Instances include bounded properties, attributes, and tags. Selection is the only active optional mutation and is Studio-local state; it does not mutate hierarchy or claim engine behavior. Mock project revision `1` is descriptive and is not a final engine revision contract.

## Authority invariant

The MCP server cannot directly acquire or manufacture `MutationAuthorityContext`, `ScriptSecurityContext`, engine capabilities, raw DataModel pointers, schema authority, or networking authority. MCP success in a future authoring operation must mean authoritative engine acceptance, not a local MCP cache update.
