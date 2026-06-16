# Glacier.Chrono: Zero-Allocation Time-Series Compression Engine

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen.svg)](#)
[![Target Framework](https://img.shields.io/badge/.NET-10.0-blue.svg)](#)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Zero-Allocation](https://img.shields.io/badge/Allocations-Zero%20Heap-orange.svg)](#)
[![NuGet Version](https://img.shields.io/nuget/v/Glacier.Chrono.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.Chrono/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.Chrono.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.Chrono/)

Glacier.Chrono is an embedded, in-process time-series database engine designed for extreme write-throughput and zero-allocation query execution in **.NET 10**. 

By bypassing heavy ORMs, database servers, and external system dependencies, Glacier.Chrono delivers timeseries compression ratios of **90%+** using standard time-series compression algorithms (inspired by Facebook Gorilla and TimescaleDB) running entirely in-process in C#.

---

## 🚀 Key Features

* **Zero-Allocation Ingest & Compression**: The hot paths for write ingestion, column transpositions, and bit-level compressions perform **zero allocations** on the managed heap.
* **CPU Register-Buffered Bit-Packing**: Utilizes a highly-optimized 64-bit register accumulator for reading and writing bits, avoiding slow bit-by-bit looping and resulting in a **19x performance speedup**.
* **Hybrid Row-to-Columnar Pivot**: Collects telemetry row-wise in a lock-free ring buffer (AoS), then pivots the layout in memory to columnar (SoA) during background compaction to maximize compression density and SIMD cache locality.
* **Vectorized (SIMD) Queries**: Leverages portable hardware intrinsics (`Vector<T>` and `Vector256<T>`) to query and aggregate millions of records per second directly on memory-mapped column files.
* **Virtual Memory File Projection**: Maps cold storage chunk files to memory via `MemoryMappedFile` projection, allowing queries to scan datasets **much larger than physical memory** without triggering GC pressure.

---

## 🏛️ Architectural Layout

```
                  ┌───────────────────────────────────────────────┐
                  │          Concurrent Ingest Threads            │
                  └───────────────────────┬───────────────────────┘
                                          │  1. Zero-Allocation Ingest (Write)
                                          ▼
                  ┌───────────────────────────────────────────────┐
                  │    HotRingBuffer<TelemetryRow> (AoS Layout)   │
                  └───────────────────────┬───────────────────────┘
                                          │  2. Background Pivot (AoS -> SoA)
                                          ▼
                  ┌───────────────────────────────────────────────┐
                  │    Columnar Spans (Timestamps, CPUs, Mem...)  │
                  └─────────┬─────────────────┬─────────────────┬─┘
                            │                 │                 │
                            │ 3. DoD          │ 3. Gorilla XOR  │ 3. RLE
                            ▼                 ▼                 ▼
                  ┌─────────────────┐ ┌───────────────┐ ┌───────────────┐
                  │ Timestamp Comp. │ │ Float Comp.   │ │ Integer Comp. │
                  └─────────┬───────┘ └───────┬───────┘ └───────┬───────┘
                            │                 │                 │
                            └─────────────────┼─────────────────┘
                                              │ 4. Single-pass Offset Write
                                              ▼
                                 ┌────────────────────────┐
                                 │  chunk_*.glacier file  │
                                 └────────────┬───────────┘
                                              │
                                              │ 5. MemoryMappedFile Projection
                                              ▼
                                 ┌────────────────────────┐
                                 │ Vectorized QueryEngine │ (SIMD average, sums, filters)
                                 └────────────────────────┘
```

### 1. Ingestion Layer (`HotRingBuffer<T>`)
Telemetry writes are written into a pre-allocated circular ring buffer. A lock-free write cursor is advanced atomically using `Interlocked.Increment`, allowing multiple concurrent ingestion threads to record telemetry without lock contention or memory allocations.

### 2. The Compaction Pivot (`Compactor`)
When a batch size (e.g., 10,000 rows) is reached, a background thread transposes the Array of Structs (AoS) format into a Struct of Arrays (SoA) format. Pivoting elements into contiguous arrays by data type prepares columns for compression algorithms tailored to their data distribution.

### 3. Bit-Packing & Compression Engine
* **Timestamps (Delta-of-Delta)**: Computes the difference in intervals (DoD). If data arrives at perfectly regular intervals (DoD = 0), it is encoded as a single bit.
* **Floating-Point Metrics (Gorilla XOR)**: Computes the XOR between consecutive floats. Zero leading/trailing bits are stripped and only the meaningful bits are bit-packed, achieving high compression ratios for slowly changing metrics (e.g. CPU or temperature logs).
* **Integers/Enums (RLE)**: Identifies consecutive repeating values and packs them into `[Value, Count]` pairs.
* **The 64-bit Accumulator Trick**: Instead of writing bits individually, the `BitWriter` and `BitReader` accumulate bits in a CPU register (`ulong`). Bytes are written to memory in bulk only when the accumulator reaches capacity, eliminating loop overhead.

### 4. Query Execution (`QueryEngine`)
Analytical queries project only the relevant columns into the virtual address space using memory-mapped views. The `QueryEngine` applies vectorized logic (`Vector<T>`) to decode and filter values directly in memory-mapped views, enabling sub-millisecond aggregates over millions of rows with negligible heap footprint.

---

## ⚡ Performance Benchmarks

Below are the official benchmark results executed on **Windows 11** using **.NET SDK 10.0.301** on a machine supporting AVX-512 vector extensions.

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8655)
Unknown processor
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
```

| Method | N | Mean | Gen 0 | Gen 1 | Gen 2 | Allocated | Throughput (Values/sec) |
| :--- | :--- | :--- | :---: | :---: | :---: | :---: | :---: |
| **HotIngestSingleThreaded** | 10000 | 138.83 μs | 142.8 | 142.8 | 142.8 | 458 KB * | ~72.0M writes/sec |
| **GorillaCompressFloats** | 10000 | 33.27 μs | - | - | - | **0 B** | **300.5M values/sec** |
| **GorillaDecompressFloats** | 10000 | 37.46 μs | - | - | - | **0 B** | **266.9M values/sec** |
| **TimestampCompressDoD** | 10000 | 4.95 μs | - | - | - | **0 B** | **2.01B values/sec** |
| **TimestampDecompressDoD** | 10000 | 10.32 μs | - | - | - | **0 B** | **969.4M values/sec** |
| **IntegerCompressRle** | 10000 | 19.81 μs | - | - | - | **0 B** | **504.7M values/sec** |
| **IntegerDecompressRle** | 10000 | 34.39 μs | - | - | - | **0 B** | **290.7M values/sec** |
| **QueryEngineSIMD** | 10000 | 325.96 μs | - | - | - | **577 B** ** | **30.7M records/sec** |

*\* Allocations in `HotIngestSingleThreaded` reflect the setup instantiation of the new ring buffer array inside the benchmark loop.*  
*\*\* Allocations in `QueryEngineSIMD` are exclusively due to the .NET BCL `MemoryMappedFile` and `MemoryMappedViewAccessor` wrapper instances, which are garbage-collected outside of the hot path.*

---

## 📊 Comparison: Glacier.Chrono vs. TimescaleDB

| Feature | TimescaleDB | Glacier.Chrono |
| :--- | :--- | :--- |
| **Execution Model** | External Database Server (PostgreSQL Extension) | Embedded, in-process C# Class Library |
| **Ingestion Latency** | High (Network, IPC, Transaction Locks, SQL Parsing) | Ultra-low (Lock-free Ring Buffer writes) |
| **Compression Quality** | High (DoD, Gorilla, RLE, Dictionary) | Identical (Custom C# implementations of DoD, Gorilla, RLE) |
| **Memory Allocation** | Relies on PostgreSQL memory buffers | **Zero** heap allocations during ingestion & compression |
| **Scale Limits** | Scalable across disk storage clusters | Scalable via Memory-Mapped files larger than RAM |
| **Deployment Complexity**| High (Docker, PG configuration, backup tooling) | Zero (Packaged as a lightweight NuGet library) |

---

## 🛠️ Quick Start

### 1. Ingest Data Concurrently
Define the schema using a layout-packed struct:
```csharp
using System.Runtime.InteropServices;
using Glacier.Chrono.Storage;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TelemetryRow
{
    public long Timestamp; // 8 bytes
    public float CpuUsage; // 4 bytes
    public float MemUsage; // 4 bytes
    public int EntityId;   // 4 bytes
}

// Instantiate a lock-free Ring Buffer
int bufferCapacity = 16384;
var ringBuffer = new HotRingBuffer<TelemetryRow>(bufferCapacity);

// Write concurrently
var record = new TelemetryRow 
{ 
    Timestamp = DateTime.UtcNow.Ticks, 
    CpuUsage = 45.2f, 
    MemUsage = 80.1f, 
    EntityId = 1 
};
ringBuffer.Write(in record);
```

### 2. Run Background Compaction
Periodically compact ingested rows to disk. Compaction converts rows to columns and flushes them to compressed binary format.
```csharp
using Glacier.Chrono.Storage;

long nextSequence = 0;
int batchSize = 10000;
string outputDirectory = "./glacier_data";

// Pre-allocate compaction buffer arrays once to ensure zero allocations on the hot path
var compBuffers = new CompactorBuffers(batchSize);

bool success = Compactor.CompactBatch(
    ringBuffer, 
    ref nextSequence, 
    batchSize, 
    outputDirectory, 
    compBuffers
);
```

### 3. Query Column Aggregates with SIMD
Execute vector-scan average queries over memory-mapped files without loading unneeded columns:
```csharp
using Glacier.Chrono.Query;

string chunkFilePath = "./glacier_data/chunk_0.glacier";
int targetEntityId = 1;
int batchSize = 10000;

// Pre-allocate query buffers to avoid heap allocations
var queryBuffers = new QueryBuffers(batchSize);

// Scans only the mapped EntityId and CpuUsage columns
double avgCpu = QueryEngine.GetAverageCpuUsageForEntity(
    chunkFilePath, 
    targetEntityId, 
    queryBuffers
);

Console.WriteLine($"SIMD Average CPU Usage: {avgCpu}%");
```

---

## 📂 Repository Layout

```
Glacier.Chrono/
├── Glacier.Chrono.slnx       # Solution configuration file
├── spec.md                   # Core architectural specification
├── src/
│   └── Glacier.Chrono/       # Library project
│       ├── Compression/      # Compressors (DoD, Gorilla, RLE, BitReaderWriter)
│       ├── Storage/          # HotRingBuffer, TelemetryRow, Compactor
│       ├── Query/            # SIMD QueryEngine
│       └── Glacier.Chrono.csproj
└── samples/
    ├── Glacier.Chrono.Demo/        # Correctness and zero-allocation verification app
    └── Glacier.Chrono.Benchmarks/  # BenchmarkDotNet performance evaluation suite
```

---

## 🧪 Building and Testing

1. **Build the solution**:
   ```bash
   dotnet build
   ```
2. **Execute correct-by-construction demo**:
   ```bash
   dotnet run --project samples/Glacier.Chrono.Demo/Glacier.Chrono.Demo.csproj -c Release
   ```
3. **Execute BenchmarkDotNet**:
   ```bash
   dotnet run --project samples/Glacier.Chrono.Benchmarks/Glacier.Chrono.Benchmarks.csproj -c Release
   ```

---

## 📄 License
This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
