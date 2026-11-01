using System;
using System.Buffers.Binary;
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
    [InlineData(1000)] // Larger batch verifying Vector.Widen double precision
    public void SimdQuery_CpuAndMem_MatchesDoublePrecisionScalarReference(int rowCount)
    {
        var ring = new HotRingBuffer<TelemetryRow>(rowCount * 2);
        const int targetEntity = 2;
        double expectedCpuSum = 0.0;
        double expectedMemSum = 0.0;
        int expectedCount = 0;

        for (int i = 0; i < rowCount; i++)
        {
            int entity = i % 4;
            float cpu = 30.0f + (i * 0.1f);
            float mem = 512.25f + (i * 0.375f);
            ring.Write(new TelemetryRow
            {
                Timestamp = 1000L + i,
                CpuUsage = cpu,
                MemUsage = mem,
                EntityId = entity
            });

            if (entity == targetEntity)
            {
                expectedCpuSum += cpu;
                expectedMemSum += mem;
                expectedCount++;
            }
        }

        var compBuffers = new CompactorBuffers(rowCount);
        long nextSeq = 0;
        Compactor.CompactBatch(ring, ref nextSeq, rowCount, _testDir, compBuffers);

        string chunkPath = Path.Combine(_testDir, "chunk_0.glacier");
        var queryBuffers = new QueryBuffers(rowCount);

        double simdCpuAvg = QueryEngine.GetAverageCpuUsageForEntity(chunkPath, targetEntity, queryBuffers);
        double simdMemAvg = QueryEngine.GetAverageMemUsageForEntity(chunkPath, targetEntity, queryBuffers);

        Assert.Equal(expectedCpuSum / expectedCount, simdCpuAvg, precision: 9);
        Assert.Equal(expectedMemSum / expectedCount, simdMemAvg, precision: 9);
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
        byte[] badHeader = new byte[64];
        File.WriteAllBytes(dummyFile, badHeader);

        var queryBuffers = new QueryBuffers(64);
        Assert.Throws<InvalidDataException>(() =>
        {
            QueryEngine.GetAverageCpuUsageForEntity(dummyFile, 1, queryBuffers);
        });
    }

    [Fact]
    public void ZoneMapPruning_And_MultiChunkTimeRangeScan_PrunesDisjointChunksAndSkipsFullContainmentTimestamps()
    {
        string multiDir = Path.Combine(_testDir, "multi_chunks");
        const int chunkRows = 64;
        var ring = new HotRingBuffer<TelemetryRow>(512);
        var compBuffers = new CompactorBuffers(chunkRows);
        long nextSeq = 0;

        // Create 3 chunks:
        // Chunk 0: timestamps [1000 .. 1063] (completely BEFORE query range [2020, 3030] -> PRUNED!)
        // Chunk 1: timestamps [2000 .. 2063] (PARTIAL overlap [2020..2063])
        // Chunk 2: timestamps [2100 .. 2163] (WHOLLY CONTAINED inside [2020, 3030] -> skips timestamp decompression!)
        // Chunk 3: timestamps [3000 .. 3063] (PARTIAL overlap [3000..3030])
        // Chunk 4: timestamps [4000 .. 4063] (completely AFTER query range [2020, 3030] -> PRUNED!)
        long[] bases = [1000L, 2000L, 2100L, 3000L, 4000L];
        double expectedCpuSum = 0.0;
        double expectedMemUnfilteredSum = 0.0;
        int expectedFilteredCount = 0;
        int expectedUnfilteredCount = 0;

        for (int c = 0; c < bases.Length; c++)
        {
            for (int i = 0; i < chunkRows; i++)
            {
                long ts = bases[c] + i;
                float cpu = 10.0f + c * 20.0f + i * 0.25f;
                float mem = 100.0f + c * 50.0f + i * 0.5f;
                int entity = i % 3;

                ring.Write(new TelemetryRow
                {
                    Timestamp = ts,
                    CpuUsage = cpu,
                    MemUsage = mem,
                    EntityId = entity
                });

                if (ts >= 2020L && ts <= 3030L)
                {
                    expectedUnfilteredCount++;
                    expectedMemUnfilteredSum += mem;
                    if (entity == 1)
                    {
                        expectedCpuSum += cpu;
                        expectedFilteredCount++;
                    }
                }
            }
            Assert.True(Compactor.CompactBatch(ring, ref nextSeq, chunkRows, multiDir, compBuffers));
        }

        // Adversarial verification 1: Corrupt the compressed payload of Chunk 0 (seq 0) and Chunk 4 (seq 256) AFTER the 128B header!
        // Because O(1) zone-map pruning rejects them from header [MinTimestamp, MaxTimestamp] alone, they MUST NOT be read!
        foreach (string prunedName in new[] { "chunk_0.glacier", $"chunk_{4 * chunkRows}.glacier" })
        {
            string p = Path.Combine(multiDir, prunedName);
            byte[] bytes = File.ReadAllBytes(p);
            for (int b = Compactor.HeaderSize; b < bytes.Length; b++) bytes[b] = 0xFF;
            File.WriteAllBytes(p, bytes);
        }

        // Adversarial verification 2: Corrupt ONLY the compressed Timestamp column bytes of Chunk 2 (seq 128), which is wholly contained in [2020, 3030]!
        // Because full containment skips timestamp column I/O & decompression, querying [2020, 3030] MUST succeed!
        {
            string c2Path = Path.Combine(multiDir, $"chunk_{2 * chunkRows}.glacier");
            byte[] bytes = File.ReadAllBytes(c2Path);
            long tsOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(28, 8));
            int tsLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(36, 4));
            for (int b = (int)tsOffset; b < (int)tsOffset + tsLen; b++) bytes[b] = 0xAA;
            File.WriteAllBytes(c2Path, bytes);
        }

        var qBuffers = new QueryBuffers(chunkRows);

        double actualFilteredCpuAvg = QueryEngine.GetAverageCpuUsageForEntityInDirectory(
            multiDir,
            targetEntityId: 1,
            startTimestamp: 2020L,
            endTimestamp: 3030L,
            qBuffers);

        double actualUnfilteredMemAvg = QueryEngine.GetAverageMemUsageInDirectory(
            multiDir,
            startTimestamp: 2020L,
            endTimestamp: 3030L,
            qBuffers);

        Assert.Equal(expectedCpuSum / expectedFilteredCount, actualFilteredCpuAvg, precision: 8);
        Assert.Equal(expectedMemUnfilteredSum / expectedUnfilteredCount, actualUnfilteredMemAvg, precision: 8);
    }

    [Fact]
    public void TimeRangeQuery_DuplicateBoundaryTimestampsAndInteriorGap_BinarySearchExact()
    {
        string gapDir = Path.Combine(_testDir, "gap_test");
        var ring = new HotRingBuffer<TelemetryRow>(64);

        // Timestamps with duplicates at boundaries and a gap between 100 and 200:
        // [50, 100, 100, 100, 200, 200, 300]
        (long Ts, float Cpu, int Entity)[] rows =
        [
            (50L,  10f, 1),
            (100L, 20f, 1),
            (100L, 30f, 1),
            (100L, 40f, 1),
            (200L, 50f, 1),
            (200L, 60f, 1),
            (300L, 70f, 1)
        ];

        foreach (var r in rows)
        {
            ring.Write(new TelemetryRow { Timestamp = r.Ts, CpuUsage = r.Cpu, MemUsage = r.Cpu * 2f, EntityId = r.Entity });
        }

        var comp = new CompactorBuffers(16);
        long seq = 0;
        Assert.True(Compactor.CompactBatch(ring, ref seq, rows.Length, gapDir, comp));

        string chunk = Path.Combine(gapDir, "chunk_0.glacier");
        var qBuf = new QueryBuffers(16);

        // Inclusive [100, 200] must include all 3 rows at 100 and both rows at 200: (20+30+40+50+60)/5 = 40.0
        Assert.Equal(40.0, QueryEngine.GetAverageCpuUsageForEntity(chunk, 1, 100L, 200L, qBuf), precision: 6);
        Assert.Equal(80.0, QueryEngine.GetAverageMemUsageForEntity(chunk, 1, 100L, 200L, qBuf), precision: 6);

        // Exact single duplicate timestamp [100, 100] -> (20+30+40)/3 = 30.0
        Assert.Equal(30.0, QueryEngine.GetAverageCpuUsage(chunk, 100L, 100L, qBuf), precision: 6);

        // Interior gap [120, 180] inside [50, 300] -> 0 matches -> 0.0
        Assert.Equal(0.0, QueryEngine.GetAverageCpuUsage(chunk, 120L, 180L, qBuf));

        // Inverted range [200, 100] -> 0.0
        Assert.Equal(0.0, QueryEngine.GetAverageCpuUsage(chunk, 200L, 100L, qBuf));
    }
}
