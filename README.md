# Gargantuan MCP

An independent, conservative Model Context Protocol server foundation for Gargantuan developer tooling.

> **Foundation 1 status:** this server uses deterministic mock data. It does **not** connect to a live Gargantuan engine or Studio session and it cannot acquire engine authority.

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

The default policy advertises seven read tools. To opt into the mock-only Studio-local selection write for development, append `--allow-studio-local-write` to the server command. MCP arguments and mock project data cannot change this policy.

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

Opaque mock identifiers such as `gtn_workspace_part` are adapter-owned handles, not engine `ObjectId` internals and not path identities. A future Studio adapter must map them to stable Gargantuan ObjectId semantics.

## Architecture and limitations

MCP translates protocol DTOs into Gargantuan-owned semantic requests on `IGargantuanAdapter`. `MockGargantuanAdapter` is the only implementation today. Future live authoring must route through Studio commands/capabilities, EditorHost, and the authoritative mutation gateway; MCP must never access raw DataModel state or manufacture authority contexts/capabilities.

- [Current MCP architecture](devdocs/CurrentArchitecture/McpArchitecture.md)
- [Future Studio integration](devdocs/FutureArchitecture/StudioMcpIntegration.md)
- [Threat model](devdocs/ThreatModel.md)

Resources and prompts were deliberately omitted: Foundation 1's small tool surface already provides the bounded parameterized reads, and duplicating each tool as a resource would add no meaningful abstraction. HTTP and remote hosting are also deliberately absent.

## License

[MIT](LICENSE)
