using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using Glacier.Chrono.Compression;
using Glacier.Chrono.Storage;
using Glacier.Chrono.Query;

namespace Glacier.Chrono.Benchmarks;

[MemoryDiagnoser]
public class ChronoBenchmarks
{
    private HotRingBuffer<TelemetryRow> _ringBuffer = null!;
    private float[] _cpuUsages = null!;
    private byte[] _cpuCompressedBuffer = null!;
    private int _cpuCompressedSize;
    private float[] _cpuDecompressedBuffer = null!;

    private long[] _timestamps = null!;
    private byte[] _tsCompressedBuffer = null!;
    private int _tsCompressedSize;
    private long[] _tsDecompressedBuffer = null!;

    private int[] _entityIds = null!;
    private byte[] _entityCompressedBuffer = null!;
    private int _entityCompressedSize;
    private int[] _entityDecompressedBuffer = null!;

    private string _tempChunkFile = null!;
    private QueryBuffers _queryBuffers = null!;

    [Params(10000)]
    public int N;

    [GlobalSetup]
    public void Setup()
    {
        _ringBuffer = new HotRingBuffer<TelemetryRow>(N);
        _cpuUsages = new float[N];
        _cpuDecompressedBuffer = new float[N];
        _cpuCompressedBuffer = new byte[N * 6 + 64];

        _timestamps = new long[N];
        _tsDecompressedBuffer = new long[N];
        _tsCompressedBuffer = new byte[N * 9 + 64];

        _entityIds = new int[N];
        _entityDecompressedBuffer = new int[N];
        _entityCompressedBuffer = new byte[N * 5 + 64];

        for (int i = 0; i < N; i++)
        {
            _cpuUsages[i] = 45.0f + (float)Math.Sin(i * 0.05) * 5.0f;
            _timestamps[i] = DateTime.UtcNow.Date.Ticks + (i * TimeSpan.TicksPerSecond);
            _entityIds[i] = i % 4; // Low cardinality
        }

        // Pre-compress for decompression benchmarks
        _cpuCompressedSize = GorillaCompressor.Compress(_cpuUsages, _cpuCompressedBuffer);
        _tsCompressedSize = TimestampCompressor.Compress(_timestamps, _tsCompressedBuffer);
        _entityCompressedSize = IntegerRleCompressor.Compress(_entityIds, _entityCompressedBuffer);

        // Pre-create file for Query benchmark
        string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "benchmark_data");
        Directory.CreateDirectory(tempDir);
        _tempChunkFile = Path.Combine(tempDir, "chunk_0.glacier");

        var compBuffers = new CompactorBuffers(N);
        var ringSetup = new HotRingBuffer<TelemetryRow>(N);
        for (int i = 0; i < N; i++)
        {
            var row = new TelemetryRow
            {
                Timestamp = _timestamps[i],
                CpuUsage = _cpuUsages[i],
                MemUsage = 80.0f,
                EntityId = _entityIds[i]
            };
            ringSetup.Write(in row);
        }
        long nextSeq = 0;
        Compactor.CompactBatch(ringSetup, ref nextSeq, N, tempDir, compBuffers);
        _queryBuffers = new QueryBuffers(N);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            if (File.Exists(_tempChunkFile))
            {
                File.Delete(_tempChunkFile);
            }
            string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "benchmark_data");
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch { }
    }

    [Benchmark]
    public void HotIngestSingleThreaded()
    {
        var buffer = new HotRingBuffer<TelemetryRow>(N);
        for (int i = 0; i < N; i++)
        {
            var row = new TelemetryRow
            {
                Timestamp = i,
                CpuUsage = 45.0f,
                MemUsage = 80.0f,
                EntityId = 1
            };
            buffer.Write(in row);
        }
    }

    [Benchmark]
    public void GorillaCompressFloats()
    {
        _ = GorillaCompressor.Compress(_cpuUsages, _cpuCompressedBuffer);
    }

    [Benchmark]
    public void GorillaDecompressFloats()
    {
        _ = GorillaCompressor.Decompress(_cpuCompressedBuffer.AsSpan(0, _cpuCompressedSize), _cpuDecompressedBuffer);
    }

    [Benchmark]
    public void TimestampCompressDoD()
    {
        _ = TimestampCompressor.Compress(_timestamps, _tsCompressedBuffer);
    }

    [Benchmark]
    public void TimestampDecompressDoD()
    {
        _ = TimestampCompressor.Decompress(_tsCompressedBuffer.AsSpan(0, _tsCompressedSize), _tsDecompressedBuffer);
    }

    [Benchmark]
    public void IntegerCompressRle()
    {
        _ = IntegerRleCompressor.Compress(_entityIds, _entityCompressedBuffer);
    }

    [Benchmark]
    public void IntegerDecompressRle()
    {
        _ = IntegerRleCompressor.Decompress(_entityCompressedBuffer.AsSpan(0, _entityCompressedSize), _entityDecompressedBuffer);
    }

    [Benchmark]
    public void QueryEngineSIMD()
    {
        _ = QueryEngine.GetAverageCpuUsageForEntity(_tempChunkFile, 1, _queryBuffers);
    }
}
