# Gargantuan MCP

An independent, conservative Model Context Protocol server foundation for Gargantuan developer tooling.

> **ProjectWrite Foundation status:** live inspection, Studio-local selection, and eight bounded durable project commands are implemented through Studio's authenticated current-user Windows named-pipe bridge. The executable still defaults to deterministic mock data. ProjectWrite is separately disabled by default, and MCP never connects directly to EditorHost or the engine.

## Protocol target

- Runtime: .NET 8 / C#
- MCP specification: [`2026-07-28`](https://modelcontextprotocol.io/specification/2026-07-28)
- Official C# SDK: `ModelContextProtocol` `2.0.0` (exactly pinned)
- Transport: stdio only

The server is pinned to the modern discovery-first, per-request-metadata, stateless lifecycle. (SDK 2.0.0 can support down-level initialization when not pinned.) stdio is newline-delimited JSON-RPC: stdin is input, stdout is protocol output only, and diagnostics go to stderr.

## Build and run

```powershell
dotnet restore Gargantuan.Mcp.slnx --configfile NuGet.Config
dotnet build Gargantuan.Mcp.slnx -c Release --no-restore
dotnet test Gargantuan.Mcp.slnx -c Release --no-build --no-restore
dotnet run --project src/Gargantuan.Mcp/Gargantuan.Mcp.csproj --no-launch-profile
```

Example client configuration:

```json
{
  "mcpServers": {
    "gargantuan": {
      "command": "dotnet",
      "args": ["/absolute/path/to/Gargantuan.Mcp.dll"]
    }
  }
}
```

The default executable uses `MockGargantuanAdapter` and advertises seven read tools. To opt into Studio-local selection write in either mode, append `--allow-studio-local-write` to the server command. Durable ProjectWrite is live-Studio-only and requires the independent `--allow-project-write` MCP policy switch. MCP requests, metadata, adapter data, and project data cannot change either policy.

For an explicitly enabled live Studio session, start Studio with its descriptor option, wait for that exact file to be published, and pass the same absolute path to MCP:

```powershell
GargantuanStudio --engine <engine> --project <project> `
  --mcp-bridge-descriptor <absolute-LocalApplicationData-session.json> `
  --allow-mcp-project-write

dotnet Gargantuan.Mcp.dll `
  --studio-bridge-descriptor <absolute-LocalApplicationData-session.json> `
  --allow-studio-local-write `
  --allow-project-write `
  --allow-destructive-write
```

Omit `--allow-studio-local-write` to keep `studio.set_selection` out of discovery. Omit either ProjectWrite switch to keep all durable-write tools out of discovery. `instance.delete` additionally requires `--allow-destructive-write`; without it the seven non-destructive ProjectWrite tools remain available while deletion is absent. MCP reads only the supplied descriptor; it never searches LocalApplicationData or scans processes. One MCP process remains bound to the descriptor's immutable pipe, session ID, token, capability set, and object-identity scope and never reattaches to a replacement session.

## Current tools

| Tool | Risk | Default | Purpose |
| --- | --- | --- | --- |
| `project.get_info` | Read | enabled | Project, root, schema, adapter and mock-status metadata |
| `project.list_instances` | Read | enabled | Bounded descendant listing/search |
| `instance.get` | Read | enabled | Bounded instance details |
| `instance.get_children` | Read | enabled | Paginated direct children |
| `schema.list_classes` | Read | enabled | Paginated semantic class summaries |
| `schema.get_class` | Read | enabled | Class inheritance and property metadata |
| `studio.get_selection` | Read | enabled | Current Studio-local selection |
| `studio.set_selection` | StudioLocalWrite | opt-in | Mock/Studio-local selection only |
| `instance.create` | ProjectWrite | opt-in | Atomically create one schema-valid instance with up to 32 initial properties |
| `instance.delete` | ProjectWrite + DestructiveWrite | separately opt-in | Delete an explicitly acknowledged target subtree |
| `instance.duplicate` | ProjectWrite | opt-in | Use Studio's ordinary authoritative subtree duplicate semantics |
| `instance.reparent` | ProjectWrite | opt-in | Reparent one live identity after parent/cycle validation |
| `instance.set_property` | ProjectWrite | opt-in | Set one schema-writable native, custom, or extension property |
| `project.save` | ProjectWrite | opt-in | Save to the current project location; no path or Save As argument |
| `studio.undo` | ProjectWrite | opt-in | Undo the current session's shared Studio command history |
| `studio.redo` | ProjectWrite | opt-in | Redo the current session's shared Studio command history |

See [ToolReference.md](devdocs/ToolReference.md) for arguments, results, pagination, and adapter capabilities.

## Security model

MCP client input is untrusted. The server owns policy; client identity is not engine authority. Requests are bounded by page size, recursion depth, search count, response bytes, property/string/query/selection sizes, continuation-token length, serialized write size, initial-property count, and concurrency. Adapter exceptions are confined and converted to stable safe errors. ProjectWrite has no arbitrary filesystem path, shell/process execution, script-source mutation, Play/Test, network listener, HTTP, credential, trust-state, or generic command/reflection tool.

Opaque mock identifiers such as `gtn_workspace_part` are adapter-owned handles, not engine `ObjectId` internals and not path identities. The Studio adapter maps Studio's generation-safe `(Slot, Generation)` identities to session-local opaque `gtn_studio_*` handles without exposing the native representation.

## Architecture and limitations

MCP translates protocol DTOs into Gargantuan-owned semantic requests on `IGargantuanAdapter`. `MockGargantuanAdapter` remains the default standalone implementation and never advertises ProjectWrite. `StudioGargantuanAdapter` implements project, hierarchy, schema, selection, and bounded project-command semantics over `IStudioSessionClient`; `StudioSessionClient` implements Studio's committed descriptor and named-pipe protocol. Studio owns the document/schema/selection services, capability checks, shared command runner, and all EditorHost routes. MCP never receives an EditorHost client, raw DataModel state, or authority contexts/capabilities.

- [Current MCP architecture](devdocs/CurrentArchitecture/McpArchitecture.md)
- [Studio integration status](devdocs/FutureArchitecture/StudioMcpIntegration.md)
- [Studio-side bridge follow-up contract](devdocs/StudioBridgeContract.md)
- [Threat model](devdocs/ThreatModel.md)

Resources and prompts were deliberately omitted: the inspection surface already provides the bounded parameterized reads, and duplicating each tool as a resource would add no meaningful abstraction. HTTP and remote hosting are also deliberately absent.

## License

[MIT](LICENSE)
