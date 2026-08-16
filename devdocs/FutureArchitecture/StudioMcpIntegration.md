# Future Studio MCP Integration

Live integration is deferred until **EDITORHOST AUTHORING FOUNDATION — TRANSACTIONS, STRUCTURAL EDITS, AND PERSISTENCE** (or its current equivalent) is complete.

```text
MCP request
  -> MCP validation and server-owned policy
  -> StudioGargantuanAdapter
  -> Studio command and capability model
  -> EditorHost
  -> authoritative engine operation / MutationGateway
```

It must never become `MCP -> raw DataModel`. The Studio adapter maps opaque MCP identities to stable Gargantuan ObjectId semantics, reflects current EditorHost capabilities, submits commands, waits for authoritative acceptance, and maps authoritative failures to the stable MCP error model. MCP does not create fake transactions or treat cache mutation as success.

## Proposed sequence

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
