# Tool Reference

The C# tool attributes and generated MCP JSON schemas are canonical. This concise inventory records semantic behavior and is covered by protocol discovery tests.

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

Every result uses `ToolResponse<T>`: `{ Success, Result, Error }`. On failure, `Error` contains a stable semantic `Code` and bounded safe `Message`. `project.list_instances` uses case-insensitive substring name matching, exact case-insensitive class matching, an ancestor scope, deterministic depth-then-ID order, maximum depth 8, and at most 1,000 candidates.
