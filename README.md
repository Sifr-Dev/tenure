# Tenure

Tenure is a .NET CLI for detecting heap-retention regressions on Linux. It compares retained bytes and object counts by type between two snapshots and exits nonzero when a configured budget is exceeded.

**Status: 0.x.** Snapshot capture, the versioned snapshot format, and comparison engine work on Linux. Root-reachable shallow-byte analysis, exclusive dominator retention diagnostics, and deterministic root-path attribution are available with `capture --root-analysis`. The existing version-1 snapshot `retainedBytes` remain **shallow object sizes**, aggregated by type, for compatibility. This repository does not claim to replace `dotnet-gcdump` or a heap viewer.

## Why

The official `dotnet-gcdump` tool collects snapshots but does not provide a Linux CLI comparison and CI budget gate. Tenure is intended to complement that collection tooling, not replace it.

## Build and test

Requires a compatible .NET 10 SDK (`10.0.100` with `latestFeature` in `global.json`). Dependencies and package cache are pinned locally; no second SDK or global tool installation is required.

```sh
dotnet test tests/Tenure.Tests/Tenure.Tests.csproj
dotnet run --project src/Tenure -- diff baseline.json candidate.json --fail-over 1048576
```

dotnet run --project src/Tenure -- capture --pid <process-id> --output snapshot.json --duration 2000
```

Capture attaches a real EventPipe session using the CLR heap-snapshot keywords, aggregates object sizes by type, and writes the versioned snapshot. It stops the session after the requested duration (up to five minutes), also when cancelled. Snapshot collection can force a full garbage collection; do not treat it as a zero-cost production profiler.

Add `--root-analysis` to print root-reachable shallow-byte totals, the 20 largest exclusive dominator-retention roots, and deterministic root paths to standard error. Reachability totals include shared objects once; exclusive retention counts an object under a root only when every path from any root passes through that root. The JSON snapshot remains version 1 and uses shallow sizes, so existing comparisons stay compatible. Dominator computation traverses the captured graph and adds CPU/memory cost beyond the capture itself; it is intended for diagnostics, not a zero-cost production profiler.

`samples/AllocationWorkload` is a bounded fixture used to verify the capture path. A real two-second capture against it produced 2,337 type groups and a `System.Byte[]` group with 614 objects and 60,356 shallow bytes. That is a fixture result, not a benchmark claim.

Example snapshot (version 1):

```json
{
  "version": 1,
  "types": [
    { "name": "App.Customer", "retainedBytes": 160, "objectCount": 3 },
    { "name": "System.Byte[]", "retainedBytes": 64, "objectCount": 1 }
  ]
}
```

Types are sorted by ordinal name. Duplicate names, negative values, unsupported versions, and malformed JSON are rejected. Type names are limited to 4096 bytes, snapshots to 256 MiB. The diff lists only positive retained-byte deltas, ordered by size then name. `--allow <type>` excludes a type from the comparison. `--fail-over <bytes>` returns exit code 1 only when the positive total is **greater than** the budget.

## Scope and limitations

- Current snapshots contain type names, shallow object bytes, and object counts. Exclusive dominator retention is computed for `--root-analysis` diagnostics, but is not stored in the version-1 snapshot or used by `diff` yet.
- Live snapshot capture is implemented and tested against a bounded workload. Unsupported targets fail with a diagnostic and no partial snapshot.
- Dumps can contain sensitive type names; do not publish them without review.
- JIT and NativeAOT builds are both goals. NativeAOT distribution will use an explicit `linux-x64` RID and document its glibc requirement; no claim is made for `arch-x64` or every distribution.
- This is not production profiling guidance. Snapshot collection can force a full garbage collection; capture support will document that cost when implemented.
