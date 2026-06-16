# Contributing to Glacier.Chrono

Thank you for your interest in contributing to Glacier.Chrono! This guide outlines our development workflow, coding standards, and repository guidelines.

---

## 1. Code of Conduct

By participating in this project, you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md). Please report any unacceptable behavior to the project maintainers.

---

## 2. Setting Up Your Development Environment

Glacier.Chrono requires the following:
* **SDK**: .NET SDK 10.0 or later.
* **OS**: Windows, macOS, or Linux.
* **IDE**: Visual Studio 2022, JetBrains Rider, or VS Code.

### Building the Project
Clone the repository and run:
```bash
dotnet build
```

### Running the Demo App
To verify correctness and benchmark-level correctness:
```bash
dotnet run --project samples/Glacier.Chrono.Demo/Glacier.Chrono.Demo.csproj -c Release
```

### Running the Benchmarks
We use BenchmarkDotNet for performance regression testing. To run the suite:
```bash
dotnet run --project samples/Glacier.Chrono.Benchmarks/Glacier.Chrono.Benchmarks.csproj -c Release
```

---

## 3. Strict Coding Guidelines

Glacier.Chrono is built for extreme performance. To maintain our current throughput and latency limits, all contributions must strictly adhere to the following rules:

### A. Zero Heap Allocations on Hot Paths
* The "hot" ingestion, compression, decompression, and query paths **must not allocate memory on the managed heap**.
* Avoid `class` allocations, LINQ queries, delegate instantiation (e.g. `Func<...>` or lambda expressions), and boxing/unboxing operations.
* Always verify allocation metrics using the `[MemoryDiagnoser]` in BenchmarkDotNet.

### B. Unmanaged Structs
* Telemetry records must be unmanaged structs annotated with `[StructLayout(LayoutKind.Sequential, Pack = 1)]` to enable direct binary projection and pointer cast operations.

### C. Bit-Packing Optimizations
* Any updates to the bit-packing engines must utilize 64-bit CPU register accumulators.
* Standard bit-by-bit looping must be avoided on critical paths.
* When writing data via `BitWriter`, you **must** call `.Flush()` at the end of the batch to write out any trailing bits.

### D. SIMD/Vectorization First
* Analytical aggregations must use .NET 10 hardware-accelerated SIMD APIs (e.g. `Vector<T>` or `Vector256<T>`).
* Decompressors and aggregators must process multiple elements per CPU clock cycle.

---

## 4. Pull Request Process

1. **Create an Issue**: Open an issue to discuss major architectural proposals before writing code.
2. **Branch Naming**: Use clear branch names: `feature/your-feature` or `bugfix/issue-id`.
3. **Run Verification**: Ensure `dotnet build` succeeds with zero errors/warnings, and that the Demo validation passes.
4. **Benchmark**: If changing any code in `Compression`, `Storage`, or `Query`, run the benchmark suite and paste the before/after results in your PR description.
5. **Submit PR**: Target the `main` branch. Provide a comprehensive summary of changes, motivation, and verification steps.
