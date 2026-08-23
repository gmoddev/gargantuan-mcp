# Studio Bridge Contract

## Purpose and ownership

ProjectWrite Foundation extends `IStudioSessionClient` and Studio's committed protocol version 1 with bounded semantic project commands. Studio implements the bridge using Studio-owned session, document, schema, selection, command, and capability services.

```text
StudioGargantuanAdapter
  -> IStudioSessionClient
       -> StudioSession / StudioDocument / StudioSchemaCache / SelectionService
       -> Studio-owned capability and command routing
       -> EditorHost only where Studio already owns that route
```

The implementation must not give MCP an `EditorHostClient`, DataModel access, engine native API, authority context, capability constructor, IPC stream, process handle, filesystem watcher, or reflection path into Studio private state. Studio remains the sole owner of its EditorHost connection and local selection.

## Exact semantic method set

One bridge instance represents one already-negotiated Studio session and implements only these methods:

| Method | Studio-side source and required behavior |
| --- | --- |
| `DescribeSessionAsync` | Return a bounded immutable session ID, bridge name/version, and capabilities derived from trusted Studio/session state. Project data, Luau, UI state, and MCP input cannot add capabilities. |
| `GetProjectInfoAsync` | Read the active Studio session/document projection: project identity/name, generation-safe root ID, root class, authoritative project revision, and active schema version/generation. |
| `ListInstancesAsync` | Read `StudioDocument` only. Apply ancestor, depth, exact class, and substring-name filters; cap candidates; sort deterministically by depth then native ObjectId; validate expected snapshot version; return only the requested offset/limit window. |
| `GetInstanceAsync` | Resolve one live generation-safe ID in `StudioDocument`; return bounded name/class/parent, reflected property values, attributes, tags, and optional custom schema identity. A dead or replaced generation is `StaleIdentity`. |
| `GetChildrenAsync` | Read direct children from `StudioDocument`, sort by native ObjectId, validate expected snapshot version, and return only the requested offset/limit window. |
| `ListClassesAsync` | Read the active immutable `StudioSchemaCache`, sort by stable schema ID, validate expected registry generation, and return only the requested offset/limit window. |
| `GetClassAsync` | Resolve one active class by stable schema ID and return bounded inheritance, provenance, constructibility, and reflected property metadata. |
| `GetSelectionAsync` | Read bounded, live identities from Studio's local `SelectionService`. Prune/deny dead identities according to the existing Studio selection lifecycle. |
| `SetSelectionAsync` | Require the live Studio selection-write/`SelectionAccess` capability, validate every identity against the current document, atomically call Studio's local selection service, and return the accepted selection. This must not write the project journal. |
| `CreateInstanceAsync` | Require ProjectWrite, validate constructible class/parent and at most 32 initial native/custom properties, then execute one atomic create transaction and return the new identity/revision. |
| `DeleteInstanceAsync` | Require explicit subtree acknowledgement, reject root/stale targets, execute ordinary authoritative destruction, and return no now-stale target identity. |
| `DuplicateInstanceAsync` | Invoke ordinary EditorHost subtree duplicate semantics and return the fresh duplicate-root identity. |
| `ReparentInstanceAsync` | Validate live same-session identities, parent rules, and cycles, then execute one authoritative reparent transaction. |
| `SetPropertyAsync` | Validate one native/custom/extension target and deterministic typed value through active schema metadata, then execute one authoritative property transaction. |
| `SaveProjectAsync` | Save only to the current project location through the existing command; accept no path or Save As input. |
| `UndoAsync` / `RedoAsync` | Use the current session's shared Studio command stack and return the reconciled authoritative revision/state. |

There is no generic execute/reflection/journal method and no script-source mutation, Play, viewport, arbitrary filesystem, Save As, shell, process, network, secret, trust-state, or native-memory operation.

## Identity and session lifecycle

`StudioObjectIdentity` is exactly the already-established nonzero `(uint Slot, uint Generation)` value. The bridge never accepts or returns MCP handles. `StudioGargantuanAdapter` owns the bidirectional opaque-handle map.

`StudioSessionDescriptor.SessionId` identifies the one immutable client lifetime. Replacing or closing the Studio document must close/invalidate that bridge instance. It must not silently bind the same instance to a replacement document, because old MCP handles would otherwise acquire meaning in a new identity scope.

## Pagination and bounds

The adapter supplies `Offset`, `Limit`, and optional `ExpectedSnapshotVersion`. `Limit` is at most MCP `MaximumPageSize + 1` (201); the extra record proves whether another page exists. Instance search also supplies `MaximumCandidates = 1,000`.

The bridge must:

1. capture one coherent Studio document revision or schema registry generation;
2. reject a mismatched expected version with `Conflict`;
3. filter and sort the coherent view before applying offset/limit;
4. return no more than `Limit` items and the captured non-authoritative pagination snapshot version; and
5. observe cancellation before and during potentially expensive enumeration.

The snapshot version is a conflict token for pagination only. It is not mutation authority, an EditorHost request ID, a journal cursor, or permission to edit.

## Error contract

The implementation returns only the closed `StudioBridgeErrorCode` set:

- `InvalidArgument` for malformed bridge input;
- `NotFound` for a valid non-identity lookup that does not exist;
- `Unavailable` for no live/connected Studio session;
- `PermissionDenied` for Studio policy/security denial;
- `Conflict` for changed document/schema snapshot or other optimistic conflict;
- `StaleIdentity` for a dead generation, old session identity, or replaced object;
- `CapabilityUnavailable` when the live Studio session lacks the operation;
- `ResourceLimit` for a bounded request or result overflow;
- `CommandUnavailable` when the shared current-session Studio command cannot run;
- `ValidationFailed` for safe schema/class/parent/property/value validation failures;
- `Cancelled` for a request cancelled before authoritative commit; and
- `InternalError` for all other failures.

Only explicitly safe, bounded messages may accompany non-internal errors. Internal exception text, paths, tokens, IPC details, stack traces, and Studio private state must remain on the Studio side. Request cancellation propagates through the command runner. Once an EditorHost transaction commits, the response reports that result even if the client cancels afterward; disconnect before a result is available is `Unavailable` and never asserts rollback.

## Implemented transport and startup

Studio is explicitly launched with `--mcp-bridge-descriptor <absolute LocalApplicationData path>`. MCP is separately launched with `--studio-bridge-descriptor <the same path>`. Normal launches expose no Studio bridge, and normal MCP launches remain mock-backed.

The descriptor is protocol version 1 with exact `Transport`, `PipeName`, `SessionId`, `Token`, and `ProcessId` fields. Transport is `windows-named-pipe`; token is 256 random bits. The descriptor is read once and is never a discovery directory or reattachment mechanism.

Each request uses one `CurrentUserOnly` Windows byte-mode named-pipe connection and one four-byte little-endian length-prefixed UTF-8 JSON request/response. The authenticated envelope contains exact `Version`, `RequestId`, `SessionId`, `Token`, `Method`, and `Parameters` fields. The implemented bounds are 64 KiB request, 1 MiB response, JSON depth 32, four client operations, one concurrent ProjectWrite, 20 ProjectWrite starts per rolling second per Studio session, 48 KiB serialized ProjectWrite DTO, 32 create-initialization properties, 16 KiB UTF-8 property strings, five-second connection establishment, and 30-second request completion. Write results return at most eight safe diagnostics.

The client validates version, transport, all descriptor fields, token size, frame sizes, response version/request ID, exact response shape, semantic DTOs, and closed errors. It never logs the token. Both Studio and MCP must explicitly opt into ProjectWrite (`--allow-mcp-project-write` and `--allow-project-write` respectively); live bridge existence alone is insufficient. Cancellation closes the connection; disconnect, timeout, Studio exit, and replacement fail as bounded `Unavailable`.
