# Foundation 2 Threat Model

## Assets and trust boundaries

The MCP client and every argument are untrusted. The Studio named-pipe bridge is also an authenticated external failure boundary from MCP's perspective. Server policy, the descriptor token, negotiated adapter capabilities, Studio session identity, protocol stdout integrity, opaque identities, bounded process resources, and engine authority are protected assets. MCP remains local stdio; Studio transport is Windows current-user local IPC, not remote hosting.

| Threat | Mitigation / test evidence |
| --- | --- |
| Malformed or oversized arguments | Explicit ID, string, query, page, depth, selection, and token validation; hostile-boundary tests |
| Deep JSON / transport abuse | SDK JSON-RPC parser plus typed schemas; semantic collection and response limits |
| Hierarchy/resource amplification | Depth 8, page 200, search 1,000, response 512 KiB, concurrency 8 |
| Invalid/stale opaque identity | Format validation and stable `InvalidArgument`, `NotFound`, `StaleIdentity` contract |
| Native identity disclosure or reuse | Session-local opaque handle map; slot/generation never enters MCP results or page tokens; unknown handles and stale native generations fail as `StaleIdentity` |
| Continuation token tampering/cross-scope reuse | Bounded Base64 cursor with version and operation scope; safe rejection |
| Pagination races | Session/operation/argument-scoped cursor, expected snapshot version passed to Studio, returned version independently checked, stable `Conflict` failure |
| Policy bypass/capability spoofing | Policy constructed only from local startup options; tool discovery intersected with negotiated adapter support; destructive delete requires both ProjectWrite and DestructiveWrite at discovery and direct invocation; MCP metadata, requests, project data, and bridge results cannot elevate it |
| Ambient Studio discovery | Studio mode requires one explicit absolute descriptor path under LocalApplicationData; the client performs no directory enumeration, process scan, descriptor polling, or endpoint fallback |
| Descriptor/token disclosure | Descriptor is bounded and read once with delete-compatible sharing; the 256-bit token is held only for authenticated requests, zeroed on client disposal, and never logged or returned through MCP |
| Wrong/stale credential replay | Every request authenticates session ID and token; the pipe and credentials are random per Studio session; the client never re-reads a replacement descriptor; wrong token is `PermissionDenied`, dead/replaced session is `Unavailable` |
| Named-pipe framing abuse | Exact versioned envelope, strict unmapped-field rejection, 64 KiB request/1 MiB response limits, JSON depth 32, request-ID match, one request/response per connection |
| Local resource exhaustion | Four client operations, five-second connection bound, 30-second request deadline, adapter/tool bounds, and cancellation-driven pipe disposal |
| Bridge over-response/resource amplification | Adapter validates strings, identities, collection counts, `PageSize + 1`, selection size, property/schema bounds, and outer 512 KiB response size |
| Exception or secret leakage | Boundary catches all adapter exceptions; generic internal error; diagnostics only to stderr |
| Studio bridge failure or disconnect | Closed error mapping, cancellation propagation, generic unexpected/internal failures, no retry loop or local success fabrication |
| stdout corruption | console provider routes Trace and above to stderr; real SDK subprocess tests parse discovery/calls repeatedly |
| Filesystem/path injection | No project paths in active contracts; no file tools; save contract deferred to Studio-owned destination policy |
| Shell/process execution | No execution tool or subprocess launcher in server semantics |
| Mock/fake confused with live engine | Adapter descriptor includes `IsMock`; fake bridge exists only in tests; default executable and missing live bridge are documented explicitly |
| MCP authority escalation | Contracts contain no engine authority types; the bridge is Studio-owned; no direct EditorHost/DataModel dependency exists |

Residual risk is ordinary same-user local-process exposure of the explicitly handed-off descriptor and dependency risk in the two local processes. HTTP, remote hosting, ProjectWrite, script access, Play/Test, and viewport access require new threat reviews before MCP implementation.
