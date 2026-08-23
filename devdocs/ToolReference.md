# Tool Reference

The C# tool attributes and generated MCP JSON schemas are canonical. This concise inventory records semantic behavior and is covered by protocol discovery tests. In Studio mode, each capability is negotiated from the Studio-owned session bridge; MCP never infers it from a requested tool.

| Name | Risk | Arguments | Result | Pagination | Capability |
| --- | --- | --- | --- | --- | --- |
| `project.get_info` | Read | none | `ProjectInfo` | no | ProjectInspection |
| `project.list_instances` | Read | parent ID?, depth=1, exact class?, substring name?, token?, page=50 | `PagedResult<InstanceSummary>` | opaque, max 200 | HierarchyInspection |
| `instance.get` | Read | ID | `InstanceDetails` | no; properties max 128 | HierarchyInspection |
| `instance.get_children` | Read | parent ID, token?, page=50 | `PagedResult<InstanceSummary>` | opaque, max 200 | HierarchyInspection |
| `schema.list_classes` | Read | token?, page=50 | `PagedResult<ClassSummary>` | opaque, max 200 | SchemaInspection |
| `schema.get_class` | Read | class ID | `ClassDetails` | no | SchemaInspection |
| `studio.get_selection` | Read | none | object ID list | no; max 128 | SelectionInspection |
| `studio.set_selection` | StudioLocalWrite | object ID list | accepted object ID list | no; max 128 | SelectionWrite + local Allow |
| `instance.create` | ProjectWrite | class ID, parent ID, initial properties (max 32), expected revision? | new object ID + authoritative revision/state | no | ProjectWrite + local Allow |
| `instance.delete` | ProjectWrite | object ID, `delete_subtree=true`, expected revision? | authoritative revision/state; no deleted ID | no | ProjectWrite + local Allow |
| `instance.duplicate` | ProjectWrite | source object ID, expected revision? | new duplicate ID + authoritative revision/state | no | ProjectWrite + local Allow |
| `instance.reparent` | ProjectWrite | object ID, new parent ID, expected revision? | object ID + authoritative revision/state | no | ProjectWrite + local Allow |
| `instance.set_property` | ProjectWrite | object ID, typed property target/value, expected revision? | object ID + authoritative revision/state | no | ProjectWrite + local Allow |
| `project.save` | ProjectWrite | expected revision? | saved authoritative revision/state | no | ProjectWrite + local Allow |
| `studio.undo` | ProjectWrite | expected revision? | authoritative revision/state | no | ProjectWrite + local Allow |
| `studio.redo` | ProjectWrite | expected revision? | authoritative revision/state | no | ProjectWrite + local Allow |

Every result uses `ToolResponse<T>`: `{ Success, Result, Error }`. On failure, `Error` contains a stable semantic `Code` and bounded safe `Message`. Write results contain the resulting/new object identity where one remains live, the authoritative document revision, persisted revision, dirty state, history label, and up to eight bounded diagnostics. `project.list_instances` uses case-insensitive substring name matching, exact case-insensitive class matching, an ancestor scope, deterministic depth-then-ID order, maximum depth 8, and at most 1,000 candidates.

The executable defaults to mock mode. `--studio-bridge-descriptor <absolute path>` selects one live Studio session at startup and supports exactly this tool set when negotiated capabilities and local policy permit it. `studio.set_selection` always requires both `SelectionWrite` from Studio and local `StudioLocalWrite = Allow`. Durable tools always require both live `ProjectWrite` and local `ProjectWrite = Allow`, set only by `--allow-project-write`; MCP request metadata cannot satisfy either condition. The client never changes adapters or reattaches during the MCP process lifetime.

Property targets are closed to `Native`, `Custom`, or `Extension`; custom/extension targets carry their declaring stable schema ID. Values use one deterministic object with `Type`, `Value`, `Enum`, `SchemaId`, `DefinitionVersion`, and `Object` fields. Supported `Type` values are `Null`, `Bool`, 32-bit `Int`, finite `Float`/`Double`, UTF-8-bounded `String`, finite component arrays for `Vector2` (2), `Vector3`/`Color3` (3), `UDim` (scale, integer offset), `UDim2` (two scale/integer-offset pairs), and `CFrame` (12), `EnumItem` with stable enum identity plus item value, `SchemaEnum` with stable schema ID/definition version plus integer item value, and `ObjectReference` with an opaque identity. Names and hierarchy paths are never accepted as write references.

`expected_revision` is optional and must be a positive authoritative project revision. A mismatch is `Conflict` and applies nothing. When omitted, Studio serializes the command and applies it against the authoritative state current at command execution (explicit last-writer behavior); identity and schema validation still run at execution time. Each tool call is one shared Studio command and one EditorHost transaction. Create validates and applies all initial native/custom properties before attachment, so failure exposes no partial object. Initial extension properties are excluded because the current atomic-create protocol does not support them; use a later one-property command.
