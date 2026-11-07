using System;
using System.Buffers.Binary;
using System.IO;
using Glacier.Chrono.Compression;
using Glacier.Chrono.Query;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.Query;

public class ChronoPushdownTests : IDisposable
{
    private readonly string _testDir;

    public ChronoPushdownTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GlacierChronoPushdown_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void CompactorV3_ReadsMetricZoneMapBounds_O1Pruning()
    {
        int batchSize = 128;
        var ring = new HotRingBuffer<TelemetryRow>(256);

        float minCpu = 15.5f;
        float maxCpu = 48.2f;
        float minMem = 128.0f;
        float maxMem = 512.0f;

        for (int i = 0; i < batchSize; i++)
        {
            float cpu = minCpu + (float)(i * (maxCpu - minCpu) / (batchSize - 1));
            float mem = minMem + (float)(i * (maxMem - minMem) / (batchSize - 1));
            ring.Write(new TelemetryRow
            {
                Timestamp = 1000L + i,
                CpuUsage = cpu,
                MemUsage = mem,
                EntityId = 1
            });
        }

        var buffers = new CompactorBuffers(batchSize);
        long nextSeq = 0;
        bool compacted = Compactor.CompactBatch(ring, ref nextSeq, batchSize, _testDir, buffers);
        Assert.True(compacted);

        string chunkPath = Path.Combine(_testDir, "chunk_0.glacier");
        Assert.True(File.Exists(chunkPath));

        bool ok = Compactor.TryReadMetricZoneMap(chunkPath,
            out double readMinCpu, out double readMaxCpu,
            out double readMinMem, out double readMaxMem,
            out int readMinEntity, out int readMaxEntity);

        Assert.True(ok);
        Assert.Equal(minCpu, (float)readMinCpu, 1e-4);
        Assert.Equal(maxCpu, (float)readMaxCpu, 1e-4);
        Assert.Equal(minMem, (float)readMinMem, 1e-4);
        Assert.Equal(maxMem, (float)readMaxMem, 1e-4);

        // O(1) Pruning: threshold > maxCpu means no rows match
        float queryThreshold = 80.0f;
        bool shouldPrune = readMaxCpu < queryThreshold;
        Assert.True(shouldPrune);
    }

    [Fact]
    public void QueryEngine_DecompressesAlpMetricBuffer_MatchesExpectedAverage()
    {
        int rowCount = 64;
        float[] cpuValues = new float[rowCount];
        double expectedSum = 0.0;

        for (int i = 0; i < rowCount; i++)
        {
            float val = 20.0f + (i * 0.5f);
            cpuValues[i] = val;
            expectedSum += val;
        }
        double expectedAvg = expectedSum / rowCount;

        // Compress CPU column with ALP
        byte[] compressedCpu = new byte[AlpCompressor.GetMaxCompressedLength(rowCount, sizeof(float))];
        int cpuLen = AlpCompressor.CompressFloat(cpuValues, compressedCpu);

        // Compress timestamps and entity IDs
        long[] timestamps = new long[rowCount];
        int[] entityIds = new int[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            timestamps[i] = 1000L + i;
            entityIds[i] = 1;
        }

        byte[] compressedTs = new byte[rowCount * 9 + 64];
        int tsLen = TimestampCompressor.Compress(timestamps, compressedTs);

        byte[] compressedEntity = new byte[rowCount * 5 + 64];
        int entityLen = IntegerRleCompressor.Compress(entityIds, compressedEntity);

        // Write custom chunk file with ALP-compressed CPU metric
        string chunkFile = Path.Combine(_testDir, "chunk_alp_0.glacier");
        byte[] header = new byte[Compactor.HeaderSize];

        long tsOffset = Compactor.HeaderSize;
        long cpuOffset = tsOffset + tsLen;
        long memOffset = cpuOffset + cpuLen;
        long entityOffset = memOffset + 4; // dummy mem

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Compactor.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), Compactor.FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), rowCount);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12, 8), 1000L);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(20, 8), 1000L + rowCount - 1);

        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(28, 8), tsOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36, 4), tsLen);

        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40, 8), cpuOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(48, 4), cpuLen);

        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(52, 8), memOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60, 4), 4);

        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(64, 8), entityOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(72, 4), entityLen);

        using (var fs = File.Create(chunkFile))
        {
            fs.Write(header);
            fs.Write(compressedTs.AsSpan(0, tsLen));
            fs.Write(compressedCpu.AsSpan(0, cpuLen));
            fs.Write(new byte[4]); // dummy mem
            fs.Write(compressedEntity.AsSpan(0, entityLen));
        }

        var qBuffers = new QueryBuffers(rowCount);
        double actualAvg = QueryEngine.GetAverageCpuUsage(chunkFile, qBuffers);

        Assert.Equal(expectedAvg, actualAvg, 1e-4);
    }
}
