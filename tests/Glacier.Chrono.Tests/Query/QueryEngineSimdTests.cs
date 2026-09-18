using System;
using System.IO;
using Glacier.Chrono.Query;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.Query;

public class QueryEngineSimdTests : IDisposable
{
    private readonly string _testDir;

    public QueryEngineSimdTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GlacierQueryTests_" + Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData(16)]   // Exact multiple of Vector<float>.Count (4, 8, 16)
    [InlineData(100)]  // Non-multiple (tests remainder scalar loop)
    [InlineData(1000)] // Larger batch
    public void SimdQuery_MatchesScalarReferenceCalculation(int rowCount)
    {
        var ring = new HotRingBuffer<TelemetryRow>(rowCount * 2);
        const int targetEntity = 2;
        double expectedSum = 0.0;
        int expectedCount = 0;

        for (int i = 0; i < rowCount; i++)
        {
            int entity = i % 4;
            float cpu = 30.0f + (i * 0.1f);
            ring.Write(new TelemetryRow
            {
                Timestamp = 1000L + i,
                CpuUsage = cpu,
                MemUsage = 60.0f,
                EntityId = entity
            });

            if (entity == targetEntity)
            {
                expectedSum += cpu;
                expectedCount++;
            }
        }

        var compBuffers = new CompactorBuffers(rowCount);
        long nextSeq = 0;
        Compactor.CompactBatch(ring, ref nextSeq, rowCount, _testDir, compBuffers);

        string chunkPath = Path.Combine(_testDir, "chunk_0.glacier");
        var queryBuffers = new QueryBuffers(rowCount);

        double simdAvg = QueryEngine.GetAverageCpuUsageForEntity(chunkPath, targetEntity, queryBuffers);
        double expectedAvg = expectedCount == 0 ? 0.0 : expectedSum / expectedCount;

        Assert.Equal(expectedAvg, simdAvg, precision: 4);
    }

    [Fact]
    public void SimdQuery_TargetEntityNotInChunk_ReturnsZero()
    {
        const int rowCount = 50;
        var ring = new HotRingBuffer<TelemetryRow>(64);
        for (int i = 0; i < rowCount; i++)
        {
            ring.Write(new TelemetryRow { Timestamp = i, CpuUsage = 50f, MemUsage = 50f, EntityId = 1 });
        }

        var compBuffers = new CompactorBuffers(rowCount);
        long nextSeq = 0;
        Compactor.CompactBatch(ring, ref nextSeq, rowCount, _testDir, compBuffers);

        string chunkPath = Path.Combine(_testDir, "chunk_0.glacier");
        var queryBuffers = new QueryBuffers(rowCount);

        double avg = QueryEngine.GetAverageCpuUsageForEntity(chunkPath, targetEntityId: 999, queryBuffers);
        Assert.Equal(0.0, avg);
    }

    [Fact]
    public void SimdQuery_CorruptMagicBytes_ThrowsInvalidDataException()
    {
        string dummyFile = Path.Combine(_testDir, "bad_chunk.glacier");
        byte[] badHeader = new byte[64]; // zeros, magic != 'GLCH'
        File.WriteAllBytes(dummyFile, badHeader);

        var queryBuffers = new QueryBuffers(64);
        Assert.Throws<InvalidDataException>(() =>
        {
            QueryEngine.GetAverageCpuUsageForEntity(dummyFile, 1, queryBuffers);
        });
    }
}
