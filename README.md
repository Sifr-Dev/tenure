# Tenure

Tenure is a .NET CLI for detecting heap-retention regressions on Linux. It compares retained bytes and object counts by type between two snapshots and exits nonzero when a configured budget is exceeded.

**Status: 0.x.** Snapshot capture, the versioned snapshot format, and comparison engine work on Linux. Root-reachable shallow-byte analysis, exclusive dominator retention diagnostics, deterministic root-path attribution, and exclusive root-kind retention gating (`diff --by-root-kind`) are available. The version-1 snapshot `retainedBytes` remain **shallow object sizes**, aggregated by type, for compatibility; root-kind retention is an added version-2 field with its own documented meaning. This repository does not claim to replace `dotnet-gcdump` or a heap viewer.

## Why

The official `dotnet-gcdump` tool collects snapshots but does not provide a Linux CLI comparison and CI budget gate. Tenure is intended to complement that collection tooling, not replace it.

## Build and test

Requires a compatible .NET 10 SDK (`10.0.100` with `latestFeature` in `global.json`). Dependencies and package cache are pinned locally; no second SDK or global tool installation is required.

```sh
dotnet test tests/Tenure.Tests/Tenure.Tests.csproj
dotnet run --project src/Tenure -- diff baseline.json candidate.json --fail-over 1048576
dotnet run --project src/Tenure -- diff baseline.json candidate.json --by-root-kind --fail-over-root-kind 262144
dotnet run --project src/Tenure -- capture --pid <process-id> --output snapshot.json --duration 2000 --root-analysis
```

Capture attaches a real EventPipe session using the CLR heap-snapshot keywords, aggregates object sizes by type, and writes the versioned snapshot. It stops the session after the requested duration (up to five minutes), also when cancelled. Snapshot collection can force a full garbage collection; do not treat it as a zero-cost production profiler.

Add `--root-analysis` to print root-reachable shallow-byte totals, exclusive dominator retention by root kind, the 20 largest exclusive dominator-retention roots, and deterministic root paths to standard error. Reachability totals include shared objects once; exclusive retention counts an object under a root only when every path from any root passes through that root. The snapshot then becomes **version 2** and carries `rootKindRetention` in addition to the unchanged version-1 `types` array. Dominator computation traverses the captured graph and adds CPU/memory cost beyond the capture itself; it is intended for diagnostics, not a zero-cost production profiler.

`samples/AllocationWorkload` is a bounded fixture used to verify the capture path. A real two-second capture early in its run produced 110 type groups and a `System.Byte[]` group with 129 objects and 134,168 shallow bytes; excluding `System.Byte[]` objects still reachable only through shared references, exclusive root-kind retention attributed Stack 188,282 bytes / 296 objects, Handle 3,838 / 50, and Finalizer 40 / 1. These are fixture observations, not benchmark claims. Capturing the same live process twice is **not** a no-op: the fixture allocates a second batch after its first delay, and a second capture observed 199 type groups with `System.Byte[]` at 253 objects and 204,374 shallow bytes.

Example snapshot (version 1, type comparison only):

```json
{
  "version": 1,
  "types": [
    { "name": "App.Customer", "retainedBytes": 160, "objectCount": 3 },
    { "name": "System.Byte[]", "retainedBytes": 64, "objectCount": 1 }
  ]
}
```

Example snapshot (version 2, `capture --root-analysis`):

```json
{
  "version": 2,
  "types": [
    { "name": "App.Customer", "retainedBytes": 160, "objectCount": 3 },
    { "name": "System.Byte[]", "retainedBytes": 64, "objectCount": 1 }
  ],
  "rootKindRetention": [
    { "kind": 0, "retainedBytes": 188282, "objectCount": 296 },
    { "kind": 2, "retainedBytes": 3838, "objectCount": 50 }
  ]
}
```

Types are sorted by ordinal name. Duplicate names, negative values, unsupported versions, and malformed JSON are rejected. Type names are limited to 4096 bytes, snapshots to 256 MiB. The diff lists only positive retained-byte deltas, ordered by size then name. `--allow <type>` excludes a type from the comparison. `--fail-over <bytes>` returns exit code 1 only when the positive total is **greater than** the budget.

## Snapshot format versioning

- **Version 1** — `types` only; `retainedBytes` is shallow object bytes aggregated by type name. Every existing comparison and snapshot keeps this meaning. A snapshot without `rootKindRetention` is written as version 1.
- **Version 2** — adds the required `rootKindRetention` array: shallow bytes exclusively dominated by roots of one GC-root kind, sorted by `kind`. `types` keeps its version-1 meaning and values, so version-1 consumers can ignore the new field; the version number changes because a version-1 reader that ignores unknown fields is not the same as a snapshot whose change in meaning is invisible.
- Readers accept versions 1 and 2. A version-2 document without `rootKindRetention`, a version-1 document that declares it, duplicate kinds, and negative values are all rejected rather than repaired.
- `RootKinds` values mirror the TraceEvent `GCRootKind` enum and are pinned in code: `0` Stack, `1` Finalizer, `2` Handle, `3` Older, `4` SizedRef, `5` Overflow.

## Root-kind retention gate

`--by-root-kind` (or `--fail-over-root-kind <bytes>`, which implies it) compares exclusive retentions per root kind and exits 1 when the positive total exceeds the budget, using the same strict "greater than" rule as `--fail-over`. Because roots occupy disjoint dominator subtrees, no object is counted under two kinds. Objects reachable only through shared references that no single root exclusively dominates are excluded, so the totals are a **lower bound** on root-reachable bytes, not a partition of the whole heap. If either snapshot is version 1 the command exits 2 instead of reporting a zero delta, because a missing field is not evidence of no regression.

## Scope and limitations

- Exclusive dominator retention is stored per GC-root kind in version 2 and is gateable with `--by-root-kind`. It is **not** stored per type or per root path, so `tenure explain`-style "which root retains this type" queries still require a live capture with `--root-analysis`.
- Capturing the same process twice is not idempotent: the heap changes between sessions. Compare snapshots of separate runs of the same workload, and treat a diff of two snapshots from one long-lived process as a measurement of that process's growth.
- Root-kind totals are shallow bytes excluding objects that no single root exclusively dominates; they are not comparable to `dotnet-counters` GC heap size or to the version-1 `retainedBytes` totals, which count all objects by type regardless of reachability.
- Live snapshot capture is implemented and tested against a bounded workload. Unsupported targets fail with a diagnostic and no partial snapshot.
- Dumps can contain sensitive type names; do not publish them without review.
- JIT and NativeAOT builds are both goals. NativeAOT distribution will use an explicit `linux-x64` RID and document its glibc requirement; no claim is made for `arch-x64` or every distribution.
- This is not production profiling guidance. Snapshot collection can force a full garbage collection; capture support will document that cost when implemented.
