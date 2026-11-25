# Tenure

Tenure is a .NET CLI for detecting heap-retention regressions on Linux. It compares retained bytes and object counts by type between two snapshots and exits nonzero when a configured budget is exceeded.

**Status: 0.x.** The current milestone is a deterministic snapshot format and comparison engine. Live EventPipe capture and root-path attribution are not yet implemented; this repository does not claim to replace `dotnet-gcdump` or a heap viewer.

## Why

The official `dotnet-gcdump` tool collects snapshots but does not provide a Linux CLI comparison and CI budget gate. Tenure is intended to complement that collection tooling, not replace it.

## Build and test

Requires a compatible .NET 10 SDK (`10.0.100` with `latestFeature` in `global.json`). Dependencies and package cache are pinned locally; no second SDK or global tool installation is required.

```sh
dotnet test tests/Tenure.Tests/Tenure.Tests.csproj
dotnet run --project src/Tenure -- diff baseline.json candidate.json --fail-over 1048576
```

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

- Current snapshots contain type names, retained bytes, and object counts. There is no GC-root path yet.
- Snapshot capture from a running process is planned but not implemented.
- Dumps can contain sensitive type names; do not publish them without review.
- JIT and NativeAOT builds are both goals. NativeAOT distribution will use an explicit `linux-x64` RID and document its glibc requirement; no claim is made for `arch-x64` or every distribution.
- This is not production profiling guidance. Snapshot collection can force a full garbage collection; capture support will document that cost when implemented.
