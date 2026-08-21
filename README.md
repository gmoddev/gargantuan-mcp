# Gargantuan MCP

An independent, conservative Model Context Protocol server foundation for Gargantuan developer tooling.

> **Foundation 2 status:** live project, hierarchy, instance, schema, and Studio-local selection integration is implemented through Studio's authenticated current-user Windows named-pipe bridge. The executable still defaults to deterministic mock data. It never connects directly to EditorHost or the engine and it cannot acquire engine authority.

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

The default executable uses `MockGargantuanAdapter` and advertises seven read tools. To opt into Studio-local selection write in either mode, append `--allow-studio-local-write` to the server command. MCP arguments, request metadata, adapter data, and project data cannot change this policy.

For an explicitly enabled live Studio session, start Studio with its descriptor option, wait for that exact file to be published, and pass the same absolute path to MCP:

```powershell
GargantuanStudio --engine <engine> --project <project> `
  --mcp-bridge-descriptor <absolute-LocalApplicationData-session.json>

dotnet Gargantuan.Mcp.dll `
  --studio-bridge-descriptor <absolute-LocalApplicationData-session.json> `
  --allow-studio-local-write
```

Omit `--allow-studio-local-write` to keep `studio.set_selection` out of discovery. MCP reads only the supplied descriptor; it never searches LocalApplicationData or scans processes. One MCP process remains bound to the descriptor's immutable pipe, session ID, and token and never reattaches to a replacement session.

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

See [ToolReference.md](devdocs/ToolReference.md) for arguments, results, pagination, and adapter capabilities.

## Security model

MCP client input is untrusted. The server owns policy; client identity is not engine authority. Requests are bounded by page size, recursion depth, search count, response bytes, property/string/query/selection sizes, continuation-token length, and concurrency. Adapter exceptions are confined and converted to stable safe errors. There are no filesystem, shell, process execution, script, Play/Test, network-listener, HTTP, or authentication tools.

Opaque mock identifiers such as `gtn_workspace_part` are adapter-owned handles, not engine `ObjectId` internals and not path identities. The Studio adapter maps Studio's generation-safe `(Slot, Generation)` identities to session-local opaque `gtn_studio_*` handles without exposing the native representation.

## Architecture and limitations

MCP translates protocol DTOs into Gargantuan-owned semantic requests on `IGargantuanAdapter`. `MockGargantuanAdapter` remains the default standalone implementation. `StudioGargantuanAdapter` implements project, hierarchy, schema, and selection semantics over `IStudioSessionClient`; `StudioSessionClient` implements Studio's committed descriptor and named-pipe protocol. Studio owns the document/schema/selection services, capability checks, bridge host, and all EditorHost routes. MCP never receives an EditorHost client, raw DataModel state, or authority contexts/capabilities.

- [Current MCP architecture](devdocs/CurrentArchitecture/McpArchitecture.md)
- [Studio integration status](devdocs/FutureArchitecture/StudioMcpIntegration.md)
- [Studio-side bridge follow-up contract](devdocs/StudioBridgeContract.md)
- [Threat model](devdocs/ThreatModel.md)

Resources and prompts were deliberately omitted: Foundation 1's small tool surface already provides the bounded parameterized reads, and duplicating each tool as a resource would add no meaningful abstraction. HTTP and remote hosting are also deliberately absent.

## License

[MIT](LICENSE)
