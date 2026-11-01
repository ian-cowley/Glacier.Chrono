![Glacier.Chrono Banner](assets/banner.jpg)

# Glacier.Chrono: Zero-Allocation Time-Series Compression Engine

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Ready-brightgreen.svg)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![NuGet Version](https://img.shields.io/nuget/v/Glacier.Chrono.svg)](https://www.nuget.org/packages/Glacier.Chrono/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.Chrono.svg)](https://www.nuget.org/packages/Glacier.Chrono/)
[![Ecosystem](https://img.shields.io/badge/Glacier-Ecosystem-blue)](https://github.com/ian-cowley)
[![Zero-Allocation](https://img.shields.io/badge/Allocations-Zero%20Heap-orange.svg)](#)
[![Website](https://img.shields.io/badge/Website-glaciersoftware.dev-66fcf1.svg)](https://glaciersoftware.dev/)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-Support-orange.svg?logo=buy-me-a-coffee)](https://buymeacoffee.com/glaciersoftware)

Glacier.Chrono is an embedded, Native AOT-compatible, in-process time-series database engine designed for extreme write-throughput and zero-allocation query execution in **.NET 10**.

By bypassing heavy ORMs, external database servers, and IPC overhead, Glacier.Chrono delivers time-series compression ratios of **90%+** using specialized time-series codecs (Delta-of-Delta, Gorilla XOR, and Integer RLE) paired with zone-map chunk pruning and double-precision SIMD scans directly in C#.

---

## 🚀 Key Features

* **Zero-Allocation Lock-Free Ingest**: Concurrent MPMC ingestion into a cache-line isolated `HotRingBuffer<T>` (`PaddedCursor` + `PaddedSequence`) with poison-free timeout and `CancellationToken` recovery.
* **Bundled Roslyn Source Generator (`[ChronoTable]`)**: `Glacier.Chrono.Generator` ships directly inside the `Glacier.Chrono` NuGet package (`analyzers/dotnet/cs`). Annotate any unmanaged struct with `[ChronoTable]` (with any number of `[Metric]` and `[Category]` columns, including wide schemas and 0-category tables) to emit strongly-typed compactors and SIMD query engines at compile time.
* **64-Bit Register-Buffered Bit-Packing**: Full `1..64`-bit unaligned `BitWriter` and `BitReader` accumulators with cross-boundary splitting and zero heap allocations.
* **Zone-Map `[MinTimestamp, MaxTimestamp]` Pruning & Range Queries**: `.glacier` v2 chunks store per-chunk timestamp bounds in a dynamic 64-byte-aligned header (`stackalloc` inspected). Disjoint chunks are pruned in **O(1)** without memory-mapping; wholly-contained chunks skip timestamp decompression entirely; partially-overlapping chunks binary-search `[startIndex, endIndex)` and run vectorized `Vector.Widen` double-precision scans.
* **Partial-Batch Compaction & Multi-Chunk Directory Scans**: Flush arbitrary tail/partial batches safely using reusable pre-allocated `CompactorBuffers`, and query across entire `.glacier` directories in a single call.
* **Native AOT Ready**: Built with `<IsAotCompatible>true</IsAotCompatible>` and zero runtime reflection.

---

## 🏛️ Architectural Layout

See [`ARCHITECTURE.md`](ARCHITECTURE.md) for the complete binary format specification, concurrency invariants, and query pipeline details.

```
                  ┌───────────────────────────────────────────────┐
                  │          Concurrent Ingest Threads            │
                  └───────────────────────┬───────────────────────┘
                                          │  1. Lock-Free CAS Ingest (Write / TryWrite)
                                          ▼
                  ┌───────────────────────────────────────────────┐
                  │      HotRingBuffer<TStruct> (AoS Layout)      │
                  └───────────────────────┬───────────────────────┘
                                          │  2. Sort + AoS -> SoA Pivot (Full or Partial Batch)
                                          ▼
                  ┌───────────────────────────────────────────────┐
                  │    Columnar Spans (Timestamps, Metrics, Cats) │
                  └─────────┬─────────────────┬─────────────────┬─┘
                            │                 │                 │
                            │ 3. DoD          │ 3. Gorilla XOR  │ 3. RLE
                            ▼                 ▼                 ▼
                  ┌─────────────────┐ ┌───────────────┐ ┌───────────────┐
                  │ Timestamp Comp. │ │ Float Comp.   │ │ Integer Comp. │
                  └─────────┬───────┘ └───────┬───────┘ └───────┬───────┘
                            │                 │                 │
                            └─────────────────┼─────────────────┘
                                              │ 4. Dynamic 64B-Aligned Header + Zone-Map Bounds
                                              ▼
                                 ┌────────────────────────┐
                                 │  chunk_*.glacier (v2)  │
                                 └────────────┬───────────┘
                                              │
                                              │ 5. O(1) Zone-Map Prune + MMF Column Projection
                                              ▼
                                 ┌────────────────────────┐
                                 │ Vectorized QueryEngine │ (SIMD Vector.Widen double precision)
                                 └────────────────────────┘
```

---

## ⚡ Performance Benchmarks

Executed on **Windows 11** using **.NET SDK 10.0** on an AVX-512 x64 host:

| Method | N | Mean | Gen 0 | Gen 1 | Gen 2 | Allocated | Throughput (Values/sec) |
| :--- | :--- | :--- | :---: | :---: | :---: | :---: | :---: |
| **HotIngestSingleThreaded** | 10000 | 138.83 μs | 142.8 | 142.8 | 142.8 | 458 KB * | ~72.0M writes/sec |
| **GorillaCompressFloats** | 10000 | 33.27 μs | - | - | - | **0 B** | **300.5M values/sec** |
| **GorillaDecompressFloats** | 10000 | 37.46 μs | - | - | - | **0 B** | **266.9M values/sec** |
| **TimestampCompressDoD** | 10000 | 4.95 μs | - | - | - | **0 B** | **2.01B values/sec** |
| **TimestampDecompressDoD** | 10000 | 10.32 μs | - | - | - | **0 B** | **969.4M values/sec** |
| **IntegerCompressRle** | 10000 | 19.81 μs | - | - | - | **0 B** | **504.7M values/sec** |
| **IntegerDecompressRle** | 10000 | 34.39 μs | - | - | - | **0 B** | **290.7M values/sec** |
| **QueryEngineSIMD** | 10000 | 325.96 μs | - | - | - | **< 600 B** ** | **30.7M records/sec** |

*\* Allocations in `HotIngestSingleThreaded` reflect instantiating the `HotRingBuffer` inside the benchmark method; steady-state `Write`/`TryWrite` allocates **0 bytes**.*  
*\*\* Allocations in `QueryEngineSIMD` come solely from OS file/memory-mapping handle wrappers; header inspection uses `stackalloc` and column scans allocate **0 bytes**.*

---

## 🛠️ Quick Start

### 1. Define Your Custom Schema
Install `Glacier.Chrono` (the Roslyn Source Generator is bundled automatically) and annotate an unmanaged struct with `[ChronoTable]`:

```csharp
using System.Runtime.InteropServices;
using Glacier.Chrono.Storage;

namespace MyApp;

[ChronoTable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SystemMetric
{
    [Timestamp] // Compresses with Delta-of-Delta + indexes [MinTimestamp, MaxTimestamp] zone-map
    public long Time;

    [Metric] // Compresses with Gorilla XOR
    public float CpuTemp;

    [Metric]
    public float FanSpeed;

    [Category] // Compresses with Run-Length Encoding
    public int ServerId;
}
```

### 2. Ingest Data Concurrently
Write data concurrently across threads into the lock-free ring buffer:

```csharp
using Glacier.Chrono.Storage;

var ringBuffer = new HotRingBuffer<SystemMetric>(capacity: 16384);

var record = new SystemMetric
{
    Time = DateTime.UtcNow.Ticks,
    CpuTemp = 45.2f,
    FanSpeed = 2100.0f,
    ServerId = 1
};

// Bounded spin-wait with timeout & CancellationToken support
ringBuffer.Write(in record, TimeSpan.FromSeconds(5), cancellationToken);
```

### 3. Compact Full or Partial Batches to `.glacier` Chunks
```csharp
using MyApp;

long nextSequence = 0;
int maxBatchSize = 10_000;
string outputDirectory = "./glacier_data";

// Preallocate reusable buffers once
var compBuffers = new SystemMetricCompactorBuffers(maxBatchSize);

// Supports full batches (10,000) or partial tail flushes (e.g., 500 <= maxBatchSize)
bool flushed = SystemMetricCompactor.CompactBatch(
    ringBuffer,
    ref nextSequence,
    batchSize: 10_000,
    outputDirectory,
    compBuffers);
```

### 4. Execute Zone-Map Pruned & SIMD Time-Range Queries
```csharp
using MyApp;

var queryBuffers = new SystemMetricQueryBuffers(maxBatchSize: 10_000);

// Single-chunk filtered query
double avgServerTemp = SystemMetricQueryEngine.GetAverageCpuTempForServerId(
    "./glacier_data/chunk_0.glacier",
    targetServerId: 1,
    queryBuffers);

// Multi-chunk directory time-range query with O(1) zone-map chunk pruning
double windowAvgTemp = SystemMetricQueryEngine.GetAverageCpuTempForServerIdInDirectory(
    "./glacier_data",
    targetServerId: 1,
    startTimestamp: windowStartTicks,
    endTimestamp: windowEndTicks,
    queryBuffers);
```

---

## 📂 Repository Layout

```
Glacier.Chrono/
├── Glacier.Chrono.slnx            # Solution configuration
├── ARCHITECTURE.md                # Detailed internals, v2 chunk spec, and concurrency design
├── spec.md                        # Original design specification
├── src/
│   ├── Glacier.Chrono/            # Core library (Storage, Compression, SIMD QueryEngine)
│   └── Glacier.Chrono.Generator/  # Roslyn Incremental Source Generator (bundled in nupkg)
├── tests/
│   └── Glacier.Chrono.Tests/      # Unit, SIMD, generator, concurrency, and stress tests
└── samples/
    ├── Glacier.Chrono.Demo/       # End-to-end verification app
    └── Glacier.Chrono.Benchmarks/ # BenchmarkDotNet suite
```

---

## 🧪 Building and Testing

```bash
# Build all projects in Release (TreatWarningsAsErrors enabled)
dotnet build --configuration Release

# Run the full unit & stress test suite
dotnet test --configuration Release

# Run the end-to-end verification demo
dotnet run --project samples/Glacier.Chrono.Demo/Glacier.Chrono.Demo.csproj -c Release
```

---

## 🆕 What's New in v1.1.0

- **Poison-Free `HotRingBuffer<T>` Slot Coordination** — verifies both ring capacity and prior-lap slot readiness *before* advancing `_writeCursor`, eliminating permanent sequence poisoning when `TryWrite` times out or is canceled.
- **Full 64-Bit Unaligned `BitWriter` / `BitReader` & Hardened Codecs** — supports `1..64`-bit writes/reads across any bit alignment (`0..7`), explicit `unchecked` modular DoD arithmetic, and strict `InvalidDataException` validation on corrupt Gorilla streams.
- **`.glacier` v2 Dynamic Headers & Zone-Map Range Queries** — removes the 64-byte / 3-column ceiling with dynamic 64-byte-aligned headers, adds `[MinTimestamp, MaxTimestamp]` O(1) chunk pruning, skips timestamp decompression on wholly-contained chunks, binary-searches partial overlaps, and widens SIMD sums to `double` (`Vector.Widen`).
- **Partial-Batch Compaction & Bundled Source Generator** — safely compacts partial batches (`batchSize <= buffers.RowBuffer.Length`), supports 0-category and wide multi-column `[ChronoTable]` structs, enables `<IsAotCompatible>true</IsAotCompatible>`, and bundles `Glacier.Chrono.Generator.dll` directly inside `Glacier.Chrono.nupkg`.

### v1.0.1
- **128-byte padded `HotRingBuffer` cursors** — eliminates false sharing across producer and consumer cache lines.
- **Bounded spin-wait backoff** and initial unit/stress test suite.

---

## 👥 Credits
Developed by Ian Cowley and Antigravity (Google DeepMind).

---

## 📄 License
This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

---

## ❄️ Glacier Ecosystem & Support

Part of the **[Glacier Ecosystem](https://glaciersoftware.dev/)** — high-performance, zero-allocation developer tooling and data engines natively built for modern C# and .NET 10.

* 🌐 **Website & Documentation**: [glaciersoftware.dev](https://glaciersoftware.dev/)
* ☕ **Buy Me a Coffee**: [buymeacoffee.com/glaciersoftware](https://buymeacoffee.com/glaciersoftware)
* ❤️ **GitHub Sponsors**: [github.com/sponsors/ian-cowley](https://github.com/sponsors/ian-cowley)
* 🐙 **All Repositories**: [github.com/ian-cowley](https://github.com/ian-cowley)
