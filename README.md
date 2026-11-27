# Tenure

Tenure is a .NET CLI for detecting heap-retention regressions on Linux. It compares retained bytes and object counts by type between two snapshots and exits nonzero when a configured budget is exceeded.

**Status: 0.x.** Snapshot capture, the versioned snapshot format, and comparison engine work on Linux. Root-reachable shallow-byte analysis, exclusive dominator retention diagnostics, deterministic root-path attribution, exclusive root-kind retention gating (`diff --by-root-kind`), and ownership-evidence retention gating (`diff --by-ownership`) are available. The version-1 snapshot `retainedBytes` remain **shallow object sizes**, aggregated by type, for compatibility; root-kind retention is an added version-2 field with its own documented meaning. This repository does not claim to replace `dotnet-gcdump` or a heap viewer.

## Why

The official `dotnet-gcdump` tool collects snapshots but does not provide a Linux CLI comparison and CI budget gate. Tenure is intended to complement that collection tooling, not replace it.

## Build and test

Requires a compatible .NET 10 SDK (`10.0.100` with `latestFeature` in `global.json`). Dependencies and package cache are pinned locally; no second SDK or global tool installation is required.

```sh
dotnet test tests/Tenure.Tests/Tenure.Tests.csproj
dotnet run --project src/Tenure -- diff baseline.json candidate.json --fail-over 1048576
dotnet run --project src/Tenure -- diff baseline.json candidate.json --by-root-kind --fail-over-root-kind 262144
dotnet run --project src/Tenure -- diff baseline.json candidate.json --by-ownership --fail-over-ownership 262144
dotnet run --project src/Tenure -- capture --pid <process-id> --output snapshot.json --duration 2000 --root-analysis
dotnet run --project src/Tenure -- capture --pid <process-id> --output snapshot.json --duration 2000 --ownership-paths
```

Capture attaches a real EventPipe session using the CLR heap-snapshot keywords, aggregates object sizes by type, and writes the versioned snapshot. It stops the session after the requested duration (up to five minutes), also when cancelled. Snapshot collection can force a full garbage collection; do not treat it as a zero-cost production profiler.

Add `--root-analysis` to print root-reachable shallow-byte totals, exclusive dominator retention by root kind, the 20 largest exclusive dominator-retention roots, and deterministic root paths to standard error. Reachability totals include shared objects once; exclusive retention counts an object under a root only when every path from any root passes through that root. The snapshot then becomes **version 2** and carries `rootKindRetention` in addition to the unchanged version-1 `types` array. Dominator computation traverses the captured graph and adds CPU/memory cost beyond the capture itself; it is intended for diagnostics, not a zero-cost production profiler.

Add `--ownership-paths` to also record **which retained type** grew under each root kind and **which observed ownership chain** reached it. This is the difference between "Handle roots retain 20,042 more bytes" and "Handle roots retain 11,544 more bytes of `System.Reflection.RuntimeMethodInfo` through `Handle -> RuntimeTypeCache -> MemberInfoCache\`1[System.Reflection.RuntimeMethodInfo] -> System.Reflection.RuntimeMethodInfo[] -> System.Reflection.RuntimeMethodInfo`". The chain is recorded as type names, not addresses, so two captures of the same workload in separate processes can be compared. `--ownership-paths` implies `--root-analysis`.

`samples/AllocationWorkload` is a bounded fixture used to verify the capture path. A real two-second capture early in its run produced 110 type groups and a `System.Byte[]` group with 129 objects and 134,168 shallow bytes; excluding `System.Byte[]` objects still reachable only through shared references, exclusive root-kind retention attributed Stack 188,282 bytes / 296 objects, Handle 3,838 / 50, and Finalizer 40 / 1. These are fixture observations, not benchmark claims. Capturing the same live process twice is **not** a no-op: the fixture allocates a second batch after its first delay, and a second capture observed 199 type groups with `System.Byte[]` at 253 objects and 204,374 shallow bytes.

With `--ownership-paths`, two captures of **separate runs** of the fixture showed the ownership report naming the cause: `Stack | System.Byte[] | 134168 -> 201240 (+67072, +64 objects)` through `Stack -> System.Object[] -> System.Byte[]`, i.e. exactly the 64 extra 1 KiB arrays the fixture allocates, and the exclusive totals rising 192,160 -> 267,078 bytes. Diagnostics-related types (`System.Reflection.*`, `MemberInfoCache`, `RuntimeTypeCache`) also appeared in that comparison because the first capture of a process runs before that process has finished its own reflection warm-up; a real CI baseline should therefore be taken after warm-up. These are fixture observations, not benchmark claims.

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

Example snapshot (version 3, `capture --ownership-paths`; the version-1 and version-2 fields are still present):

```json
{
  "version": 3,
  "types": [
    { "name": "System.Byte[]", "retainedBytes": 201240, "objectCount": 193 }
  ],
  "rootKindRetention": [
    { "kind": 0, "retainedBytes": 201240, "objectCount": 193 }
  ],
  "ownershipRetention": [
    {
      "kind": 0,
      "type": "System.Byte[]",
      "retainedBytes": 201240,
      "objectCount": 193,
      "rootCount": 2,
      "evidence": {
        "path": ["Stack", "System.Object[]", "System.Byte[]"],
        "depth": 1,
        "truncated": false
      }
    }
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
- **Version 3** — adds the required `ownershipRetention` array: the same exclusive retention split by retained type, each group carrying `rootCount` and an `evidence` chain of type names from the root kind to a representative object. Groups are sorted by `kind` then ordinal `type`. `types` and `rootKindRetention` keep their earlier meanings and values, so version-1 and version-2 readers keep working on the fields they know.
- Readers accept versions 1, 2, and 3. A version-2-or-later document without `rootKindRetention`, a version-1 document that declares it, a version-3 document without `ownershipRetention`, a version-1 or version-2 document that declares it, duplicate kinds or ownership groups, unknown root-kind names in evidence, and negative values are all rejected rather than repaired.
- `ownershipRetention` is validated as a strict refinement, not a second measurement: the groups of each root kind must sum **exactly** to that kind's `rootKindRetention` bytes and object count. A snapshot that disagrees with itself is rejected. Each group must retain at least one object and have between 1 and `objectCount` owning roots.
- `evidence.path` starts with the root-kind name and continues with one type name per object; `depth` is the number of edges from the root's own object to the representative, so a representative that is itself the root object has depth 0 and `path` length 2. Chains longer than 256 edges keep a `path` prefix and set `truncated: true`, while `depth` still reports the true distance. An untruncated chain must have `depth == path.Length - 2`; a truncated one must be shorter than its depth.
- `RootKinds` values mirror the TraceEvent `GCRootKind` enum and are pinned in code: `0` Stack, `1` Finalizer, `2` Handle, `3` Older, `4` SizedRef, `5` Overflow.

## Ownership retention gate

`--by-ownership` (or `--fail-over-ownership <bytes>`, which implies it) reports the positive growth of exclusive retention per (root kind, retained type) pair, largest first, with the candidate's observed ownership chain, the object-count change, and the change in the number of owning roots. It exits 1 when the positive total exceeds the budget, using the same strict "greater than" rule as `--fail-over`. If either snapshot is not version 3 the command exits 2 instead of reporting a zero delta, because a version-2 snapshot has the kind totals but cannot say which type is responsible for them. `--allow <type>` excludes a type from the ownership comparison as well.

The evidence chain is **evidence, not a census**. One chain is recorded per (kind, type) group, chosen as the shortest chain and then the ordinally smallest by type names, so the choice is stable between captures of the same workload. Other objects in the same group may be retained through different chains of the same length, and a group's `rootCount` says how many roots of that kind exclusively dominate part of it. Treat the chain as the thread to pull on, not as proof that every byte in the group hangs off it.

## Root-kind retention gate

`--by-root-kind` (or `--fail-over-root-kind <bytes>`, which implies it) compares exclusive retentions per root kind and exits 1 when the positive total exceeds the budget, using the same strict "greater than" rule as `--fail-over`. Because roots occupy disjoint dominator subtrees, no object is counted under two kinds. Objects reachable only through shared references that no single root exclusively dominates are excluded, so the totals are a **lower bound** on root-reachable bytes, not a partition of the whole heap. If either snapshot is version 1 the command exits 2 instead of reporting a zero delta, because a missing field is not evidence of no regression.

## Scope and limitations

- Snapshot collection can force a full garbage collection; do not treat it as a zero-cost production profiler.
- Exclusive dominator retention is gateable per root kind (`--by-root-kind`) and per root kind and retained type (`--by-ownership`). Ownership evidence records **one** observed chain per group, chosen by shortest-then-ordinal type names; it does not enumerate every path, rank dominator subtrees below the root, or map IL/frame stacks. Those are the questions `PerfView` and a heap viewer answer with a fuller graph, and Tenure does not claim to.
- The recorded chain is read from the graph's breadth-first parent links, so it is the shortest path from a root, not necessarily the path the garbage collector's dominator tree considers canonical. It is deterministic for a given graph and comparable across captures of the same workload, which is what a CI gate needs.
- `--ownership-paths` adds a parent walk per (kind, type) group and keeps only the shallowest candidates, so it costs more CPU than `--root-analysis` alone. It is a diagnostic, not a zero-cost production profiler.
- Ownership groups are shallow bytes only, like the root-kind totals they refine. They say nothing about the deep-size contribution of an object's transitive children beyond the shallow bytes already attributed to those children individually.
- Capturing the same process twice is not idempotent: the heap changes between sessions. Compare snapshots of separate runs of the same workload, and treat a diff of two snapshots from one long-lived process as a measurement of that process's growth.
- Root-kind totals are shallow bytes excluding objects that no single root exclusively dominates; they are not comparable to `dotnet-counters` GC heap size or to the version-1 `retainedBytes` totals, which count all objects by type regardless of reachability.
- Live snapshot capture is implemented and tested against a bounded workload. Unsupported targets fail with a diagnostic and no partial snapshot.
- Dumps can contain sensitive type names; do not publish them without review.
- JIT and NativeAOT builds are both goals. NativeAOT distribution will use an explicit `linux-x64` RID and document its glibc requirement; no claim is made for `arch-x64` or every distribution.
- This is not production profiling guidance, and it is not a substitute for `dotnet-gcdump`, `PerfView`, or a full heap viewer.
