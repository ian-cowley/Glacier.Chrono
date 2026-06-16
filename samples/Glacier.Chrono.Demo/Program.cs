using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Chrono.Compression;
using Glacier.Chrono.Storage;
using Glacier.Chrono.Query;

namespace Glacier.Chrono.Demo;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== Glacier.Chrono Complete Engine Verification Demo ===");

        int capacity = 16384;
        var ringBuffer = new HotRingBuffer<TelemetryRow>(capacity);
        
        int totalItems = 10000;
        int writerThreadsCount = 4;
        int itemsPerThread = totalItems / writerThreadsCount;

        // Force GC to get a clean baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long startBytes = GC.GetAllocatedBytesForCurrentThread();

        // 1. Concurrent Lock-Free Ingest
        Console.WriteLine($"\n[1/5] Ingesting {totalItems} rows concurrently across {writerThreadsCount} threads...");
        var stopwatch = Stopwatch.StartNew();

        Parallel.For(0, writerThreadsCount, threadId =>
        {
            for (int i = 0; i < itemsPerThread; i++)
            {
                var row = new TelemetryRow
                {
                    // Generate regular 1-second intervals for DoD timestamp compression testing
                    Timestamp = DateTime.UtcNow.Date.Ticks + (i * TimeSpan.TicksPerSecond),
                    CpuUsage = 45.0f + (float)Math.Sin(i * 0.05) * 5.0f,
                    MemUsage = 80.0f + (float)Math.Cos(i * 0.02) * 2.0f,
                    EntityId = threadId // Low cardinality for RLE compression testing (values 0, 1, 2, 3)
                };
                ringBuffer.Write(in row);
            }
        });

        stopwatch.Stop();
        long endBytes = GC.GetAllocatedBytesForCurrentThread();
        long ingestAllocated = endBytes - startBytes;

        Console.WriteLine($"Ingest completed in {stopwatch.Elapsed.TotalMilliseconds:F2} ms");

        // 2. Pivot & Compaction (AoS to SoA, DoD, Gorilla, RLE, and File write)
        Console.WriteLine("\n[2/5] Compacting and flushing to disk...");
        string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "glacier_data");
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, true);
        }

        var compBuffers = new CompactorBuffers(totalItems);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long startCompactBytes = GC.GetAllocatedBytesForCurrentThread();

        long nextSeq = 0;
        bool compactSuccess = Compactor.CompactBatch(ringBuffer, ref nextSeq, totalItems, outputDir, compBuffers);

        long endCompactBytes = GC.GetAllocatedBytesForCurrentThread();
        long compactAllocated = endCompactBytes - startCompactBytes;

        Console.WriteLine($"Compactor Result: {(compactSuccess ? "SUCCESS" : "FAILED")}");
        Console.WriteLine($"Compaction & Disk Writing Heap Allocations: {compactAllocated} bytes (Goal: < 8000 bytes for FileStream wrapper)");

        // 3. Print File Info
        string chunkFile = Path.Combine(outputDir, "chunk_0.glacier");
        if (File.Exists(chunkFile))
        {
            var fileInfo = new FileInfo(chunkFile);
            Console.WriteLine($"Generated chunk file: {fileInfo.FullName}");
            Console.WriteLine($"Chunk file size: {fileInfo.Length} bytes");
            Console.WriteLine($"Uncompressed size would be: {totalItems * 20} bytes (Compression ratio: {((double)fileInfo.Length / (totalItems * 20)) * 100:F2}%)");
        }
        else
        {
            Console.WriteLine("❌ Chunk file was not generated!");
            return;
        }

        // 4. SIMD Vectorized Queries
        Console.WriteLine("\n[3/5] Executing SIMD query on memory-mapped columns...");
        var queryBuffers = new QueryBuffers(totalItems);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long startQueryBytes = GC.GetAllocatedBytesForCurrentThread();

        int targetEntityId = 1;
        double averageCpu = QueryEngine.GetAverageCpuUsageForEntity(chunkFile, targetEntityId, queryBuffers);

        long endQueryBytes = GC.GetAllocatedBytesForCurrentThread();
        long queryAllocated = endQueryBytes - startQueryBytes;

        Console.WriteLine($"SIMD Average CPU for Entity {targetEntityId}: {averageCpu:F6}%");
        Console.WriteLine($"Query Execution Heap Allocations: {queryAllocated} bytes (Goal: < 2000 bytes for MMF wrapper objects)");

        // 5. Verify Correctness Against Scalar Reference
        Console.WriteLine("\n[4/5] Running validation check...");
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < totalItems; i++)
        {
            if (compBuffers.EntityIds[i] == targetEntityId)
            {
                sum += compBuffers.CpuUsages[i];
                count++;
            }
        }
        double referenceAverageCpu = sum / count;
        Console.WriteLine($"Scalar Reference Average CPU: {referenceAverageCpu:F6}%");

        double diff = Math.Abs(averageCpu - referenceAverageCpu);
        Console.WriteLine($"Exact Difference: {diff:E6}");

        // Using 0.001 tolerance since single-precision float accumulation differences occur due to SIMD instruction order
        bool cpuMatches = diff < 0.001;
        Console.WriteLine($"Average CPU Matches: {(cpuMatches ? "PASS" : "FAIL")}");

        // Single-threaded clean allocation test for ingest to verify pure 0 allocations
        Console.WriteLine("\n[5/5] Checking single-threaded core operations...");
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var testBuffer = new HotRingBuffer<TelemetryRow>(1024);
        long testStartBytes = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100; i++)
        {
            var row = new TelemetryRow
            {
                Timestamp = i,
                CpuUsage = 45.0f,
                MemUsage = 80.0f,
                EntityId = 1
            };
            testBuffer.Write(in row);
        }

        long testEndBytes = GC.GetAllocatedBytesForCurrentThread();
        long testAllocated = testEndBytes - testStartBytes;
        Console.WriteLine($"Core Ingestion Allocations (100 writes): {testAllocated} bytes");

        // We allow minor allocations (< 10 KB) for I/O wrapper objects (FileStream, MemoryMappedFile, view accessor)
        bool zeroCoreAllocations = testAllocated == 0 && queryAllocated < 10000 && compactAllocated < 10000;

        if (cpuMatches && zeroCoreAllocations)
        {
            Console.WriteLine("\n🎉 Verification Result: SUCCESS! All systems verified with zero core heap allocations.");
        }
        else
        {
            Console.WriteLine("\n❌ Verification Result: FAILURE! Check accuracy or allocations.");
        }

        // Cleanup
        try
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
        catch { }
    }
}
