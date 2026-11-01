using System;
using System.Buffers.Binary;
using System.IO;
using Glacier.Chrono.Query;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.Storage;

public class AosToSoaAndCompactorTests : IDisposable
{
    private readonly string _testDir;

    public AosToSoaAndCompactorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GlacierChronoTests_" + Guid.NewGuid().ToString("N"));
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
            // Ignore cleanup errors in temp dir
        }
    }

    [Fact]
    public void CompactBatch_TransposesAosToSoaAndWritesV2ZoneMapHeader()
    {
        const int batchSize = 100;
        var ring = new HotRingBuffer<TelemetryRow>(128);

        // Ingest unordered rows to verify sorting and zone-map bounds
        for (int i = batchSize - 1; i >= 0; i--)
        {
            ring.Write(new TelemetryRow
            {
                Timestamp = 1000L + i,
                CpuUsage = 50.0f + i,
                MemUsage = 70.0f,
                EntityId = i % 2
            });
        }

        var buffers = new CompactorBuffers(batchSize);
        long nextSeq = 0;
        bool success = Compactor.CompactBatch(ring, ref nextSeq, batchSize, _testDir, buffers);

        Assert.True(success);
        Assert.Equal(batchSize, nextSeq);
        Assert.Equal(batchSize - 1, ring.ReadProgress);

        for (int i = 0; i < batchSize; i++)
        {
            Assert.Equal(1000L + i, buffers.Timestamps[i]);
            Assert.Equal(50.0f + i, buffers.CpuUsages[i]);
        }

        string chunkPath = Path.Combine(_testDir, "chunk_0.glacier");
        Assert.True(File.Exists(chunkPath));

        using var fs = File.OpenRead(chunkPath);
        byte[] header = new byte[Compactor.HeaderSize];
        fs.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        int rowCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        long minTs = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12, 8));
        long maxTs = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(20, 8));
        long tsOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(28, 8));
        int tsLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(36, 4));

        Assert.Equal(Compactor.Magic, magic);
        Assert.Equal(Compactor.FormatVersion, version);
        Assert.Equal(batchSize, rowCount);
        Assert.Equal(1000L, minTs);
        Assert.Equal(1000L + batchSize - 1, maxTs);
        Assert.Equal((long)Compactor.HeaderSize, tsOffset);
        Assert.True(tsLen > 0);
    }

    [Fact]
    public void CompactBatch_PartialBatchSmallerThanBufferCapacity_CompactsExactSliceOnly()
    {
        const int bufferCapacity = 500;
        const int partialBatchSize = 73;
        var ring = new HotRingBuffer<TelemetryRow>(512);

        double expectedCpuSum = 0.0;
        for (int i = 0; i < partialBatchSize; i++)
        {
            float cpu = 40.0f + i * 0.5f;
            expectedCpuSum += cpu;
            ring.Write(new TelemetryRow
            {
                Timestamp = 5000L + i * 10L,
                CpuUsage = cpu,
                MemUsage = 80.0f,
                EntityId = 1
            });
        }

        // Allocate reusable buffers MUCH larger than partialBatchSize, pre-polluted with sentinel data
        var compBuffers = new CompactorBuffers(bufferCapacity);
        Array.Fill(compBuffers.Timestamps, 999_999_999L);
        Array.Fill(compBuffers.CpuUsages, -9999.0f);
        Array.Fill(compBuffers.EntityIds, 1);

        long nextSeq = 0;
        bool success = Compactor.CompactBatch(ring, ref nextSeq, partialBatchSize, _testDir, compBuffers);

        Assert.True(success);
        Assert.Equal(partialBatchSize, nextSeq);
        Assert.Equal(partialBatchSize - 1, ring.ReadProgress);

        string chunkPath = Path.Combine(_testDir, "chunk_0.glacier");
        var zoneMap = QueryEngine.ReadChunkZoneMap(chunkPath);
        Assert.Equal(partialBatchSize, zoneMap.RowCount);
        Assert.Equal(5000L, zoneMap.MinTimestamp);
        Assert.Equal(5000L + (partialBatchSize - 1) * 10L, zoneMap.MaxTimestamp);

        var queryBuffers = new QueryBuffers(bufferCapacity);
        double actualAvg = QueryEngine.GetAverageCpuUsageForEntity(chunkPath, 1, queryBuffers);
        Assert.Equal(expectedCpuSum / partialBatchSize, actualAvg, precision: 5);
    }

    [Fact]
    public void CompactBatch_BatchSizeExceedsBuffers_ThrowsArgumentOutOfRangeException()
    {
        var ring = new HotRingBuffer<TelemetryRow>(64);
        var buffers = new CompactorBuffers(32);
        long nextSeq = 0;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Compactor.CompactBatch(ring, ref nextSeq, 33, _testDir, buffers));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Compactor.CompactBatch(ring, ref nextSeq, 0, _testDir, buffers));
    }

    [Fact]
    public void CompactBatch_InsufficientData_ReturnsFalse()
    {
        var ring = new HotRingBuffer<TelemetryRow>(128);
        ring.Write(new TelemetryRow { Timestamp = 1 });

        var buffers = new CompactorBuffers(100);
        long nextSeq = 0;
        bool success = Compactor.CompactBatch(ring, ref nextSeq, 100, _testDir, buffers);

        Assert.False(success);
        Assert.Equal(0, nextSeq);
    }
}
