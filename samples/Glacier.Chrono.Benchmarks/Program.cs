using System;
using System.Diagnostics;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;
using Glacier.Chrono.Compression;
using Glacier.Chrono.Storage;

namespace Glacier.Chrono.Benchmarks;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--bdn", StringComparison.OrdinalIgnoreCase))
        {
            BenchmarkRunner.Run<ChronoBenchmarks>();
            return;
        }

        Console.WriteLine("================================================================================");
        Console.WriteLine("               GLACIER.CHRONO HIGH-PERFORMANCE PHYSICAL BENCHMARK               ");
        Console.WriteLine("================================================================================");

        // 1. Multi-threaded Concurrent Ingest (8 producer threads)
        const int totalItems = 4_000_000;
        const int capacity = 4_194_304; // 2^22
        const int threads = 8;
        int itemsPerThread = totalItems / threads;

        var buffer = new HotRingBuffer<TelemetryRow>(capacity);

        // Warmup
        Parallel.For(0, 4, t =>
        {
            for (int i = 0; i < 50_000; i++)
            {
                var r = new TelemetryRow { Timestamp = i, CpuUsage = 50f, MemUsage = 80f, EntityId = t };
                buffer.Write(in r);
            }
        });

        // Reset buffer
        buffer = new HotRingBuffer<TelemetryRow>(capacity);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < itemsPerThread; i++)
            {
                var r = new TelemetryRow { Timestamp = i, CpuUsage = 50.0f + (i & 7), MemUsage = 80f, EntityId = t };
                buffer.Write(in r);
            }
        });
        sw.Stop();

        double elapsedSec = sw.Elapsed.TotalSeconds;
        double throughput = totalItems / elapsedSec;
        double latencyNs = (sw.Elapsed.TotalMilliseconds * 1_000_000.0) / totalItems;
        Console.WriteLine($"  Multi-Threaded Ingest ({threads}T, {totalItems:N0} rows):  {sw.Elapsed.TotalMilliseconds,8:F2} ms ({throughput / 1_000_000.0:F2} M rows/sec, {latencyNs:F1} ns/row)");

        // 2. Single-Threaded Ingest
        const int singleItems = 1_000_000;
        var singleBuffer = new HotRingBuffer<TelemetryRow>(1_048_576);
        sw.Restart();
        for (int i = 0; i < singleItems; i++)
        {
            var r = new TelemetryRow { Timestamp = i, CpuUsage = 50f, MemUsage = 80f, EntityId = 1 };
            singleBuffer.Write(in r);
        }
        sw.Stop();
        double singleThroughput = singleItems / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  Single-Threaded Ingest (1T, {singleItems:N0} rows):  {sw.Elapsed.TotalMilliseconds,8:F2} ms ({singleThroughput / 1_000_000.0:F2} M rows/sec)");

        // 3. Compression / Decompression Benchmarks
        var bench = new ChronoBenchmarks { N = 100_000 };
        bench.Setup();

        sw.Restart();
        for (int i = 0; i < 50; i++) bench.GorillaCompressFloats();
        sw.Stop();
        Console.WriteLine($"  Gorilla Float Compress (100k):          {sw.Elapsed.TotalMilliseconds / 50.0,8:F2} ms avg");

        sw.Restart();
        for (int i = 0; i < 50; i++) bench.GorillaDecompressFloats();
        sw.Stop();
        Console.WriteLine($"  Gorilla Float Decompress (100k):        {sw.Elapsed.TotalMilliseconds / 50.0,8:F2} ms avg");

        sw.Restart();
        for (int i = 0; i < 50; i++) bench.TimestampCompressDoD();
        sw.Stop();
        Console.WriteLine($"  Timestamp Compress DoD (100k):          {sw.Elapsed.TotalMilliseconds / 50.0,8:F2} ms avg");

        sw.Restart();
        for (int i = 0; i < 50; i++) bench.TimestampDecompressDoD();
        sw.Stop();
        Console.WriteLine($"  Timestamp Decompress DoD (100k):        {sw.Elapsed.TotalMilliseconds / 50.0,8:F2} ms avg");

        sw.Restart();
        for (int i = 0; i < 50; i++) bench.IntegerCompressRle();
        sw.Stop();
        Console.WriteLine($"  Integer RLE Compress (100k):            {sw.Elapsed.TotalMilliseconds / 50.0,8:F2} ms avg");

        sw.Restart();
        for (int i = 0; i < 50; i++) bench.IntegerDecompressRle();
        sw.Stop();
        Console.WriteLine($"  Integer RLE Decompress (100k):          {sw.Elapsed.TotalMilliseconds / 50.0,8:F2} ms avg");

        bench.Cleanup();
        Console.WriteLine("================================================================================");
    }
}
