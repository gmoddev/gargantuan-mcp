# Studio Bridge Contract

## Purpose and ownership

ScriptWrite Foundation extends `IStudioSessionClient` and Studio's committed protocol version 2 with bounded semantic project and script commands. Studio implements the bridge using Studio-owned session, document, schema, selection, script-workspace, command, and capability services.

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
| `GetScriptSourceAsync` | Require ScriptInspection, validate a current generation-safe script identity, and return exact bounded source, schema-derived class, Engine source revision, and project revision. |
| `CreateInstanceAsync` | Require ProjectWrite, validate constructible class/parent and at most 32 initial native/custom properties, then execute one atomic create transaction and return the new identity/revision. |
| `DeleteInstanceAsync` | Require explicit subtree acknowledgement, reject root/stale targets, execute ordinary authoritative destruction, and return no now-stale target identity. |
| `DuplicateInstanceAsync` | Invoke ordinary EditorHost subtree duplicate semantics and return the fresh duplicate-root identity. |
| `ReparentInstanceAsync` | Validate live same-session identities, parent rules, and cycles, then execute one authoritative reparent transaction. |
| `SetPropertyAsync` | Validate one native/custom/extension target and deterministic typed value through active schema metadata, then execute one authoritative property transaction. |
| `SaveProjectAsync` | Save only to the current project location through the existing command; accept no path or Save As input. |
| `UndoAsync` / `RedoAsync` | Use the current session's shared Studio command stack and return the reconciled authoritative revision/state. |
| `CreateScriptAsync` | Require ProjectWrite plus ScriptWrite; validate schema-derived script class, parent, name/source bounds, and optional project revision; atomically attach name, initial source, and parent in one authoritative transaction. |
| `SetScriptSourceAsync` | Require ProjectWrite plus ScriptWrite; reject dirty local buffers; replace exact source using mandatory Engine source revision and optional project revision; reconcile open tabs and return bounded diagnostics. |

There is no generic execute/reflection/journal method and no script execution, Play, viewport, arbitrary filesystem, Save As, shell, process, network, secret, trust-state, or native-memory operation. Script deletion remains ordinary `instance.delete` with DestructiveWrite.

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

Only explicitly safe, bounded messages may accompany non-internal errors. Internal exception text, paths, tokens, IPC details, stack traces, and Studio private state must remain on the Studio side. Script conflicts may include bounded current source/project revisions, local-edit conflict state, and a reread recommendation, but never source. Once EditorHost commits, post-commit failure is `Unavailable` with validated authoritative-confirmed/projection-unavailable state and never asserts rollback.

## Implemented transport and startup

Studio is explicitly launched with `--mcp-bridge-descriptor <absolute LocalApplicationData path>`. MCP is separately launched with `--studio-bridge-descriptor <the same path>`. Normal launches expose no Studio bridge, and normal MCP launches remain mock-backed.

The descriptor is protocol version 2 with exact `Transport`, `PipeName`, `SessionId`, `Token`, and `ProcessId` fields. Transport is `windows-named-pipe`; token is 256 random bits. Its absolute path must remain below `LocalApplicationData`, and the root, every existing parent component, and the leaf must not be a symbolic link or Windows reparse point. The descriptor is read once and is never a discovery directory or reattachment mechanism.

Each request uses one `CurrentUserOnly` Windows byte-mode named-pipe connection and one four-byte little-endian length-prefixed UTF-8 JSON request/response. The authenticated envelope contains exact `Version`, `RequestId`, `SessionId`, `Token`, `Method`, and `Parameters` fields. Bounds are 512 KiB request, 1 MiB response, JSON depth 32, four client operations, one shared concurrent ProjectWrite/ScriptWrite, 20 write starts per rolling second, 48 KiB ordinary ProjectWrite DTO, 512 KiB ScriptWrite DTO, 64 KiB UTF-8 source, 16 KiB UTF-8 script name/property string, 32 create properties, eight script diagnostics with 256-character messages, 250 ms syntax analysis, five-second connection establishment, and 30-second request completion.

The client validates version, transport, all descriptor fields, token size, frame sizes, response version/request ID, exact response shape, semantic DTOs, and closed errors. It never logs the token. Both sides must opt into ProjectWrite; both must separately opt into ScriptWrite (`--allow-mcp-script-write` and `--allow-script-write`). Cancellation closes the connection; disconnect, timeout, Studio exit, and replacement fail as bounded `Unavailable`.

## Deferred bridge conformance artifact

The real `Gargantuan.Mcp.LiveStudioSmoke` proves matching builds end to end, but
Studio and MCP still duplicate bridge method, error, and limit constants. A
separate contract-release milestone should have Studio publish
`contracts/studio-mcp-bridge-v2.json` containing the descriptor/envelope version,
method names, closed error codes, request/response schema hashes, capability
mapping, and every frame/operation/resource limit, together with canonical JSON
vectors. MCP should pin the artifact to its supported Studio release and compare
its DTO serialization and constants in CI. The artifact remains data-only and
does not authorize calls or introduce a Studio assembly dependency.
