using System;
using System.Buffers.Binary;
using System.IO;
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
    public void CompactBatch_TransposesAosToSoaAndWritesChunk()
    {
        const int batchSize = 100;
        var ring = new HotRingBuffer<TelemetryRow>(128);

        // Ingest un-ordered rows to verify sorting
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

        // Verify sorted order in buffers
        for (int i = 0; i < batchSize; i++)
        {
            Assert.Equal(1000L + i, buffers.Timestamps[i]);
            Assert.Equal(50.0f + i, buffers.CpuUsages[i]);
        }

        string chunkPath = Path.Combine(_testDir, "chunk_0.glacier");
        Assert.True(File.Exists(chunkPath));

        // Verify 64-byte Header
        using var fs = File.OpenRead(chunkPath);
        byte[] header = new byte[64];
        fs.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        int rowCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));

        Assert.Equal(0x48434C47u, magic); // 'GLCH'
        Assert.Equal(1u, version);
        Assert.Equal(batchSize, rowCount);

        long tsOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12, 8));
        int tsLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20, 4));
        Assert.Equal(64L, tsOffset);
        Assert.True(tsLen > 0);
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
