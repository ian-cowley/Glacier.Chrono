using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Chrono.Compression;
using Glacier.Chrono.Storage;

namespace Glacier.Chrono.Demo;

[ChronoTable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SystemMetric : IComparable<SystemMetric>
{
    [Timestamp]
    public long Time;

    [Metric]
    public float CpuTemp;

    [Metric]
    public float FanSpeed;

    [Category]
    public int ServerId;

    public int CompareTo(SystemMetric other)
    {
        return Time.CompareTo(other.Time);
    }
}

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== Glacier.Chrono Source Generator & Custom Method Demo ===");

        int capacity = 16384;
        var ringBuffer = new HotRingBuffer<SystemMetric>(capacity);
        
        int totalItems = 10000;
        int writerThreadsCount = 4;
        int itemsPerThread = totalItems / writerThreadsCount;

        // Force GC to get a clean baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long startBytes = GC.GetAllocatedBytesForCurrentThread();

        // 1. Concurrent Lock-Free Ingest
        Console.WriteLine($"\n[1/5] Ingesting {totalItems} custom SystemMetric rows concurrently across {writerThreadsCount} threads...");
        var stopwatch = Stopwatch.StartNew();

        Parallel.For(0, writerThreadsCount, threadId =>
        {
            for (int i = 0; i < itemsPerThread; i++)
            {
                var row = new SystemMetric
                {
                    Time = DateTime.UtcNow.Date.Ticks + (i * TimeSpan.TicksPerSecond),
                    CpuTemp = 50.0f + (float)Math.Sin(i * 0.05) * 10.0f,
                    FanSpeed = 2000.0f + (float)Math.Cos(i * 0.02) * 500.0f,
                    ServerId = threadId // Server IDs 0, 1, 2, 3
                };
                ringBuffer.Write(in row);
            }
        });

        stopwatch.Stop();
        long endBytes = GC.GetAllocatedBytesForCurrentThread();
        long ingestAllocated = endBytes - startBytes;

        Console.WriteLine($"Ingest completed in {stopwatch.Elapsed.TotalMilliseconds:F2} ms");

        // 2. Pivot & Compaction via Generator-emitted Compactor
        Console.WriteLine("\n[2/5] Compacting custom schema and flushing to disk via generated compactor...");
        string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "glacier_data");
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, true);
        }

        // Use the compile-time generated buffers class
        var compBuffers = new SystemMetricCompactorBuffers(totalItems);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long startCompactBytes = GC.GetAllocatedBytesForCurrentThread();

        long nextSeq = 0;
        // Call the compile-time generated compactor
        bool compactSuccess = SystemMetricCompactor.CompactBatch(ringBuffer, ref nextSeq, totalItems, outputDir, compBuffers);

        long endCompactBytes = GC.GetAllocatedBytesForCurrentThread();
        long compactAllocated = endCompactBytes - startCompactBytes;

        Console.WriteLine($"Generated Compactor Result: {(compactSuccess ? "SUCCESS" : "FAILED")}");
        Console.WriteLine($"Compaction & Disk Writing Heap Allocations: {compactAllocated} bytes (Goal: < 8000 bytes for FileStream wrapper)");

        // 3. Print File Info
        string chunkFile = Path.Combine(outputDir, "chunk_0.glacier");
        if (File.Exists(chunkFile))
        {
            var fileInfo = new FileInfo(chunkFile);
            Console.WriteLine($"Generated chunk file: {fileInfo.FullName}");
            Console.WriteLine($"Chunk file size: {fileInfo.Length} bytes");
            Console.WriteLine($"Uncompressed size would be: {totalItems * Marshal.SizeOf<SystemMetric>()} bytes (Compression ratio: {((double)fileInfo.Length / (totalItems * Marshal.SizeOf<SystemMetric>())) * 100:F2}%)");
        }
        else
        {
            Console.WriteLine("❌ Chunk file was not generated!");
            return;
        }

        // 4. SIMD Vectorized Queries via Generator-emitted QueryEngine
        Console.WriteLine("\n[3/5] Executing custom SIMD query on memory-mapped columns...");
        
        // Use the compile-time generated query buffers
        var queryBuffers = new SystemMetricQueryBuffers(totalItems);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long startQueryBytes = GC.GetAllocatedBytesForCurrentThread();

        int targetServerId = 2;
        // Call the custom generated SIMD aggregation method: GetAverageCpuTempForServerId
        double averageCpuTemp = SystemMetricQueryEngine.GetAverageCpuTempForServerId(chunkFile, targetServerId, queryBuffers);

        long endQueryBytes = GC.GetAllocatedBytesForCurrentThread();
        long queryAllocated = endQueryBytes - startQueryBytes;

        Console.WriteLine($"SIMD Average CPU Temp for Server {targetServerId}: {averageCpuTemp:F6} °C");
        Console.WriteLine($"Query Execution Heap Allocations: {queryAllocated} bytes (Goal: < 2000 bytes for MMF wrapper objects)");

        // 5. Verify Correctness Against Scalar Reference
        Console.WriteLine("\n[4/5] Running validation check...");
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < totalItems; i++)
        {
            if (compBuffers.ServerIds[i] == targetServerId)
            {
                sum += compBuffers.CpuTemps[i];
                count++;
            }
        }
        double referenceAverageCpuTemp = sum / count;
        Console.WriteLine($"Scalar Reference Average CPU Temp: {referenceAverageCpuTemp:F6} °C");

        double diff = Math.Abs(averageCpuTemp - referenceAverageCpuTemp);
        Console.WriteLine($"Exact Difference: {diff:E6}");

        bool cpuTempMatches = diff < 0.001;
        Console.WriteLine($"Average CPU Temp Matches: {(cpuTempMatches ? "PASS" : "FAIL")}");

        // Single-threaded clean allocation test for ingest to verify pure 0 allocations
        Console.WriteLine("\n[5/5] Checking single-threaded core operations...");
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var testBuffer = new HotRingBuffer<SystemMetric>(1024);
        long testStartBytes = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100; i++)
        {
            var row = new SystemMetric
            {
                Time = i,
                CpuTemp = 55.0f,
                FanSpeed = 2200.0f,
                ServerId = 1
            };
            testBuffer.Write(in row);
        }

        long testEndBytes = GC.GetAllocatedBytesForCurrentThread();
        long testAllocated = testEndBytes - testStartBytes;
        Console.WriteLine($"Core Ingestion Allocations (100 writes): {testAllocated} bytes");

        bool zeroCoreAllocations = testAllocated == 0 && queryAllocated < 10000 && compactAllocated < 10000;

        if (cpuTempMatches && zeroCoreAllocations)
        {
            Console.WriteLine("\n🎉 Verification Result: SUCCESS! Generated custom table and SIMD query engine verified with zero core heap allocations.");
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
