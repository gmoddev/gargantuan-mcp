# Foundation 1 Threat Model

## Assets and trust boundaries

The MCP client and every argument are untrusted. Server policy, adapter capabilities, protocol stdout integrity, opaque identities, bounded process resources, and future engine authority are protected assets. Foundation 1 is local stdio and has no remote authentication boundary.

| Threat | Mitigation / test evidence |
| --- | --- |
| Malformed or oversized arguments | Explicit ID, string, query, page, depth, selection, and token validation; hostile-boundary tests |
| Deep JSON / transport abuse | SDK JSON-RPC parser plus typed schemas; semantic collection and response limits |
| Hierarchy/resource amplification | Depth 8, page 200, search 1,000, response 512 KiB, concurrency 8 |
| Invalid/stale opaque identity | Format validation and stable `InvalidArgument`, `NotFound`, `StaleIdentity` contract |
| Continuation token tampering/cross-scope reuse | Bounded Base64 cursor with version and operation scope; safe rejection |
| Policy bypass/capability spoofing | Policy constructed only from local startup options; tool discovery intersected with adapter support; MCP/project data cannot elevate it |
| Exception or secret leakage | Boundary catches all adapter exceptions; generic internal error; diagnostics only to stderr |
| stdout corruption | console provider routes Trace and above to stderr; real SDK subprocess tests parse discovery/calls repeatedly |
| Filesystem/path injection | No project paths in active contracts; no file tools; save contract deferred to Studio-owned destination policy |
| Shell/process execution | No execution tool or subprocess launcher in server semantics |
| Mock confused with live engine | Adapter descriptor includes `IsMock`; docs and project metadata identify mock explicitly |
| MCP authority escalation | Contracts contain no engine authority types; future route is Studio command -> EditorHost -> MutationGateway |

Residual risk is limited to ordinary local-process and dependency risk. HTTP, authentication, script access, Play/Test, authoring, and viewport access require new threat reviews before implementation.
