# Studio MCP Integration Status

EditorHost authoring, transactions, structural edits, persistence, schema
discovery, snapshot/journal projection, Studio-local selection, ProjectWrite, and
ScriptWrite now exist through Studio's authenticated named-pipe bridge.

```text
MCP request
  -> MCP validation and server-owned policy
  -> StudioGargantuanAdapter
  -> authenticated Studio bridge
  -> Studio command/capability/script-workspace model
  -> EditorHost -> MutationGateway
  -> authoritative journal and Studio reconciliation
```

It must never become `MCP -> raw DataModel`, `MCP -> text buffer`, or
`MCP -> project file`. Opaque MCP identities map to generation-safe Studio
identities, negotiated capabilities intersect server policy, and success means an
authoritative Engine result reconciled through Studio. A post-commit projection
failure explicitly preserves commit truth and retires the stale session.

## Implemented

- bounded project/hierarchy/instance/schema/selection reads;
- Studio-local selection mutation;
- eight ProjectWrite tools with independent DestructiveWrite for delete;
- exact bounded `script.get_source`, atomic `script.create`, and versioned
  `script.set_source`;
- independent two-sided ProjectWrite and ScriptWrite opt-ins;
- shared Studio commands, source versions, Undo/Redo, persistence, dirty-buffer
  conflict handling, and bounded post-commit recovery;
- protocol-v2 current-user named-pipe framing, descriptor confinement, opaque
  identities, closed errors, and strict resource limits; and
- deterministic/unit tests plus disposable real Studio/EditorHost/MCP workflow,
  reopen verification, and post-commit ScriptWrite fault mode.

## Remaining sequence

Play/Test control, viewport capture, asset tooling, streamable HTTP, remote/cloud
hosting, and collaboration remain separate future security designs. ScriptWrite
does not pre-authorize any of them. Any future Play tool must use a distinct
Studio-owned capability and must not launch runtime processes directly. Viewport
tooling must use explicit Studio APIs rather than duplicating the shared-memory
transport. SourceMount and arbitrary filesystem editing remain out of scope.
