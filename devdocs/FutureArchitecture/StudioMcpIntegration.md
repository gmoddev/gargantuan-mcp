# Studio MCP Integration Status

EditorHost authoring, transactions, structural edits, persistence, schema discovery, snapshot/journal projection, and Studio-local selection exist in the referenced Gargantuan and Studio architecture. Foundation 2 completes live MCP reads and Studio-local selection through Studio's committed authenticated named-pipe bridge. Project authoring tools remain deliberately absent.

```text
MCP request
  -> MCP validation and server-owned policy
  -> StudioGargantuanAdapter
  -> Studio command and capability model
  -> EditorHost
  -> authoritative engine operation / MutationGateway
```

It must never become `MCP -> raw DataModel`. The Studio adapter maps opaque MCP identities to stable Gargantuan ObjectId semantics, reflects current EditorHost capabilities, submits commands, waits for authoritative acceptance, and maps authoritative failures to the stable MCP error model. MCP does not create fake transactions or treat cache mutation as success.

## Implemented in Foundation 2

- `StudioGargantuanAdapter` implements the existing semantic `IGargantuanAdapter` contract.
- `IStudioSessionClient` is the narrow Studio-owned boundary beneath it.
- Capability discovery comes from one negotiated Studio session.
- Session-local opaque MCP handles map bidirectionally to generation-safe Studio identities.
- Project, hierarchy, instance, schema, and selection conversion is bounded.
- Pagination is adapter-owned and snapshot-conflict aware.
- Studio-local selection write passes only after MCP policy and Studio capability checks.
- A deterministic fake bridge proves these semantics independently of Studio and EditorHost.
- `StudioSessionClient` consumes only an explicit bounded descriptor and the authenticated current-user Windows named pipe.
- The composition root keeps mock mode as default and adds explicit `--studio-bridge-descriptor` Studio mode.
- A real external process smoke proves the complete production chain, wrong-token denial, shutdown, replacement isolation, and fresh-session attachment.

## ProjectWrite prerequisites

ProjectWrite requires a separate accepted extension of the Studio-owned bridge. It must define closed operation-specific requests; route through `StudioCommandCatalog`, `StudioCommandRunner`, `StudioSession`, and existing EditorHost commands; carry Studio generation guards and engine-issued optimistic conflict tokens; wait for authoritative acceptance plus journal reconciliation; integrate engine-owned transactions and Undo/Redo; expose only negotiated live capabilities; add separate MCP policy classes and opt-ins; and prove bounded failure, cancellation, replacement, stale identity, conflict, and partial-success behavior. Raw `StudioDocument` mutation, generic command invocation, and direct EditorHost pass-through remain prohibited.

## Remaining sequence

1. Keep the current read foundation: project info, hierarchy, instance, schema, and selection.
2. After EditorHost authoring exists: create, delete, duplicate, reparent, set property, save, undo, and redo.
3. Later, behind distinct capabilities and policy: script read/write, Play/Test control, diagnostics, viewport capture, and asset tooling.

Planned authoring contracts are not in default tool discovery. Their intended shapes are:

- `instance.create`: parent ID, class ID, bounded name/properties, expected project revision; returns authoritative ID and revision.
- `instance.delete`: ID and expected revision; destructive, returns authoritative revision.
- `instance.duplicate`: source/parent IDs and expected revision; returns authoritative new ID and revision.
- `instance.reparent`: object/parent IDs and expected revision; returns authoritative revision.
- `instance.set_property`: ID, semantic property ID/value, expected revision; returns accepted normalized value and revision.
- `project.save`: explicit destination policy owned by Studio, not an MCP path; returns authoritative save status.

Script source requires separate `ReadScripts` and `WriteScripts` policy from general object editing. Play tools must depend on a future Studio Play-session capability and must never launch runtime processes directly. Viewport tooling must use explicit Studio APIs and must not duplicate the shared-memory viewport protocol. Streamable HTTP, authentication, and remote/cloud hosting remain separate future security designs.
