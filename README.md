# Vestige

**Heap-retention regression diagnostics and CI budget gates for .NET on Linux.**

[![.NET](https://img.shields.io/badge/.NET-10-512bd4?style=for-the-badge&logo=dotnet&logoColor=white&labelColor=1b1f27)](https://dotnet.microsoft.com/)
[![Linux](https://img.shields.io/badge/LINUX-x86__64-58a6ff?style=for-the-badge&logo=linux&logoColor=0b0d10&labelColor=1b1f27)](#limitations)
[![CI](https://github.com/Sifr-Dev/tenure/actions/workflows/ci.yml/badge.svg)](https://github.com/Sifr-Dev/tenure/actions/workflows/ci.yml)

> The GitHub repository remains `Sifr-Dev/tenure`; Vestige is the product name.
> The assembly, tool command and NuGet package ID remain `Tenure` / `tenure` so
> existing `dotnet tool` installs keep working.

## What it is

Vestige is a .NET CLI that captures heap snapshots through EventPipe, compares
retained bytes and object counts by type between two snapshots, and exits
nonzero when a configured budget is exceeded. It adds root-kind and
ownership-path retention so a CI gate can say which root and which type grew,
not only that total bytes increased. It complements heap-collection tooling; it
does not replace `dotnet-gcdump` or a heap viewer.

## Current capabilities

- **Versioned snapshot capture over a real EventPipe session**, with bounded
  duration and cancellation. Proof: `src/Tenure/HeapCapture.cs`,
  `tests/Tenure.Tests/CaptureIntegrationTests.cs`
  (`RealEventPipeCaptureProducesAUsableSnapshot`).
- **Version-1 type comparison** of positive retained-byte deltas, deterministic
  ordering, allowlisting and a strict "greater than" budget gate.
  Proof: `src/Tenure/DiffReport.cs`,
  `tests/Tenure.Tests/DiffReportTests.cs::ReportsPositiveDeltasInStableOrder`,
  `::BudgetIsStrictlyGreaterThanTheConfiguredLimit`.
- **Root-reachable and exclusive dominator retention by root kind** (version-2
  snapshots), with `--by-root-kind` and `--fail-over-root-kind`. A version-1
  snapshot exits 2 instead of reporting a false zero delta.
  Proof: `src/Tenure/RetentionGraph.cs`, `src/Tenure/RootKinds.cs`,
  `tests/Tenure.Tests/RetentionGraphTests.cs`,
  `tests/Tenure.Tests/DiffReportTests.cs`
  (`RootKindComparisonRequiresTwoRootAnalyzedSnapshots`).
- **Ownership evidence** (version-3 snapshots): retention split by root kind and
  retained type, with a deterministic shortest observed chain of type names.
  `--by-ownership` and `--fail-over-ownership` gate on it.
  Proof: `src/Tenure/HeapGraphBuilder.cs`,
  `tests/Tenure.Tests/OwnershipRetentionTests.cs`.
- **Strict snapshot validation** that rejects duplicate types, negative values,
  unsupported versions, inconsistent ownership totals and impossible depths
  instead of repairing them.
  Proof: `tests/Tenure.Tests/DiffReportTests.cs::MalformedSnapshotsAreRejected`,
  `tests/Tenure.Tests/OwnershipRetentionTests.cs`.
- **Packaged .NET tool** whose installed command exposes the gates; CI installs
  the packed tool and runs a failing and a passing budget check.
  Proof: `.github/workflows/ci.yml` (`package` job),
  `src/Tenure/Tenure.csproj`.

## Quick start

Requires the .NET 10 SDK (`global.json` pins `10.0.100` with
`latestFeature`). Dependencies are restored from the pinned package sources.

```sh
git clone https://github.com/Sifr-Dev/tenure.git
cd tenure
dotnet test tests/Tenure.Tests/Tenure.Tests.csproj

dotnet run --project src/Tenure -- \
  diff baseline.json candidate.json --fail-over 1048576

dotnet run --project src/Tenure -- \
  diff baseline.json candidate.json --by-root-kind --fail-over-root-kind 262144

dotnet run --project src/Tenure -- \
  diff baseline.json candidate.json --by-ownership --fail-over-ownership 262144

dotnet run --project src/Tenure -- \
  capture --pid <process-id> --output snapshot.json --duration 2000 --root-analysis

dotnet run --project src/Tenure -- \
  capture --pid <process-id> --output snapshot.json --duration 2000 --ownership-paths
```

`--root-analysis` prints reachable totals, exclusive retention by root kind, the
20 largest exclusive roots and deterministic root paths to stderr, and writes a
version-2 snapshot. `--ownership-paths` implies `--root-analysis` and writes a
version-3 snapshot with one representative ownership chain per
(kind, retained type) group.

`--fail-over <bytes>` exits 1 only when the positive total is **greater than**
the budget. `--allow <type>` excludes a type from comparison.

## Verification

Executed on 2026-11-28 on Linux x86-64 with .NET SDK 10.0.114:

```sh
dotnet test tests/Tenure.Tests/Tenure.Tests.csproj
```

Observed: **47 tests passed, 0 failed, 0 skipped** (11 s). The suite includes
real EventPipe captures of the bounded `samples/AllocationWorkload` fixture,
version-1/2/3 round trips, root-kind and ownership gates, malformed-document
rejection and determinism checks.

CI additionally packs the tool, installs it from the local package source, and
verifies that `--by-root-kind` fails above budget and passes below it.

## Architecture

```text
src/Tenure
  Program.cs             CLI parsing and command dispatch
  HeapCapture.cs         EventPipe session and snapshot collection
  HeapGraphBuilder.cs    object graph, references and ownership paths
  RetentionGraph.cs      reachability, dominators and exclusive retention
  RootKinds.cs           pinned mapping to the TraceEvent GCRootKind enum
  HeapSnapshot.cs        versioned snapshot model and validation
  DiffReport.cs          type, root-kind and ownership comparisons
  ReportFormat.cs        human and signed output rendering
tests/Tenure.Tests       unit, validation and live-capture integration suites
samples/AllocationWorkload  bounded fixture used by capture tests
```

## Benchmarks

No benchmark is published. The fixture measurements in earlier notes were
single captures of a synthetic workload, not controlled benchmarks, and are not
presented as performance claims.

## Limitations

- Linux only. NativeAOT distribution targets an explicit `linux-x64` RID and
  documents its glibc requirement; other RIDs are not claimed.
- Snapshot collection can force a full garbage collection. It is not a
  zero-cost production profiler.
- Exclusive retention and ownership groups use shallow object bytes, not deep
  transitive size.
- Root-kind totals are a lower bound: objects shared through references that no
  single root exclusively dominates are excluded.
- Ownership evidence records **one** observed chain per group, chosen as the
  shortest then ordinally smallest by type name. It is a thread to pull on, not
  a census of every path.
- The recorded chain follows breadth-first parent links, not necessarily the
  collector's canonical dominator path.
- `--ownership-paths` costs more CPU than `--root-analysis`.
- Capturing the same live process twice is not idempotent; compare separate
  runs of the same workload for a regression gate.
- Dumps can contain sensitive type names; review before publishing.
- Not a substitute for `dotnet-gcdump`, `PerfView` or a full heap viewer.

## Roadmap (NOT IMPLEMENTED)

- NativeAOT packaging beyond the documented `linux-x64` RID.
- Deep-size attribution in addition to shallow bytes.
- Multi-path ownership evidence and ranking below the root.
- Automated baseline capture in CI workflows.
- Cross-platform capture backends.

## License

No licence file is present in this repository, so no reuse licence is granted.
