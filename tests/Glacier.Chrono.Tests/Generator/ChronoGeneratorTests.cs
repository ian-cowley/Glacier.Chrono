using System;
using System.IO;
using System.Runtime.InteropServices;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.Generator;

[ChronoTable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CustomSensorMetric : IComparable<CustomSensorMetric>
{
    [Timestamp]
    public long RecordedAt;

    [Metric]
    public float Temperature;

    [Metric]
    public float Humidity;

    [Category]
    public int DeviceId;

    public int CompareTo(CustomSensorMetric other) => RecordedAt.CompareTo(other.RecordedAt);
}

/// <summary>
/// 7-column schema (1 Timestamp + 4 Metric + 2 Category) that previously overflowed the hardcoded 64-byte header.
/// </summary>
[ChronoTable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WideClusterTelemetry : IComparable<WideClusterTelemetry>
{
    [Timestamp]
    public long Timestamp;

    [Metric]
    public float CpuLoad;

    [Metric]
    public float MemoryUsed;

    [Metric]
    public float DiskIops;

    [Metric]
    public float NetworkMbps;

    [Category]
    public int NodeId;

    [Category]
    public int RegionId;

    public int CompareTo(WideClusterTelemetry other) => Timestamp.CompareTo(other.Timestamp);
}

/// <summary>
/// 9-column schema (1 Timestamp + 6 Metric + 2 Category -> 192-byte dynamic header).
/// </summary>
[ChronoTable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UltraWideTelemetry
{
    [Timestamp]
    public long Ts;

    [Metric] public float M1;
    [Metric] public float M2;
    [Metric] public float M3;
    [Metric] public float M4;
    [Metric] public float M5;
    [Metric] public float M6;

    [Category] public int C1;
    [Category] public int C2;
}

/// <summary>
/// 0-category schema (1 Timestamp + 2 Metric, 0 Category) verifying unfiltered query generation.
/// </summary>
[ChronoTable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PureGaugeSample
{
    [Timestamp]
    public long SampleTime;

    [Metric]
    public float Voltage;

    [Metric]
    public float CurrentAmps;
}

public class ChronoGeneratorTests : IDisposable
{
    private readonly string _testDir;

    public ChronoGeneratorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "GlacierGeneratorTests_" + Guid.NewGuid().ToString("N"));
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
    public void GeneratedCompactorAndQueryEngine_ExecutesEndToEnd()
    {
        const int count = 200;
        var ring = new HotRingBuffer<CustomSensorMetric>(count * 2);

        for (int i = 0; i < count; i++)
        {
            ring.Write(new CustomSensorMetric
            {
                RecordedAt = 1_000_000L + i,
                Temperature = 20.0f + (i * 0.05f),
                Humidity = 45.0f,
                DeviceId = i % 2
            });
        }

        var compBuffers = new CustomSensorMetricCompactorBuffers(count);
        long nextSeq = 0;
        bool success = CustomSensorMetricCompactor.CompactBatch(ring, ref nextSeq, count, _testDir, compBuffers);

        Assert.True(success);

        string chunkFile = Path.Combine(_testDir, "chunk_0.glacier");
        Assert.True(File.Exists(chunkFile));

        var queryBuffers = new CustomSensorMetricQueryBuffers(count);
        double avgTemp = CustomSensorMetricQueryEngine.GetAverageTemperatureForDeviceId(chunkFile, targetDeviceId: 0, queryBuffers);

        Assert.True(avgTemp > 20.0 && avgTemp < 35.0);

        // Verify buffer bounds check throws ArgumentException when queryBuffers is smaller than chunk rowCount
        var tinyBuffers = new CustomSensorMetricQueryBuffers(10);
        Assert.Throws<ArgumentException>(() =>
            CustomSensorMetricQueryEngine.GetAverageTemperatureForDeviceId(chunkFile, 0, tinyBuffers));
    }

    [Fact]
    public void Wide7ColumnAnd9ColumnTables_DynamicHeaderAndPartialBatchAndTimeRange_Succeed()
    {
        Assert.Equal(128, WideClusterTelemetryCompactor.HeaderSize);
        Assert.Equal(192, UltraWideTelemetryCompactor.HeaderSize);

        string wideDir = Path.Combine(_testDir, "wide7");
        const int bufferCapacity = 512;
        const int partialBatch = 175; // Partial batch < bufferCapacity!

        var ring = new HotRingBuffer<WideClusterTelemetry>(bufferCapacity);
        double expectedNetSum = 0.0;
        int expectedNetCount = 0;

        // Write in reverse timestamp order to verify sorting + partial-batch slicing
        for (int i = partialBatch - 1; i >= 0; i--)
        {
            long ts = 10_000L + i * 100L;
            float net = 250.0f + i * 1.25f;
            int region = i % 3;

            ring.Write(new WideClusterTelemetry
            {
                Timestamp = ts,
                CpuLoad = 40.0f + i * 0.1f,
                MemoryUsed = 16_384.0f + i,
                DiskIops = 1200.0f + i * 2f,
                NetworkMbps = net,
                NodeId = i % 5,
                RegionId = region
            });

            if (region == 2 && ts >= 12_000L && ts <= 20_000L)
            {
                expectedNetSum += net;
                expectedNetCount++;
            }
        }

        var compBuffers = new WideClusterTelemetryCompactorBuffers(bufferCapacity);
        // Pre-pollute trailing slots to ensure partial batch ignores indices >= partialBatch
        Array.Fill(compBuffers.NetworkMbpss, 999_999.0f);
        Array.Fill(compBuffers.RegionIds, 2);

        long nextSeq = 0;
        Assert.True(WideClusterTelemetryCompactor.CompactBatch(ring, ref nextSeq, partialBatch, wideDir, compBuffers));
        Assert.Equal(partialBatch, nextSeq);

        string chunkFile = Path.Combine(wideDir, "chunk_0.glacier");
        var zm = WideClusterTelemetryQueryEngine.ReadChunkZoneMap(chunkFile);
        Assert.Equal(partialBatch, zm.RowCount);
        Assert.Equal(10_000L, zm.MinTimestamp);
        Assert.Equal(10_000L + (partialBatch - 1) * 100L, zm.MaxTimestamp);

        var qBuffers = new WideClusterTelemetryQueryBuffers(bufferCapacity);
        double actualNetAvg = WideClusterTelemetryQueryEngine.GetAverageNetworkMbpsForRegionId(
            chunkFile,
            targetRegionId: 2,
            startTimestamp: 12_000L,
            endTimestamp: 20_000L,
            qBuffers);

        Assert.Equal(expectedNetSum / expectedNetCount, actualNetAvg, precision: 5);

        // Also verify 9-column table end-to-end
        string ultraDir = Path.Combine(_testDir, "wide9");
        var ultraRing = new HotRingBuffer<UltraWideTelemetry>(128);
        for (int i = 0; i < 64; i++)
        {
            ultraRing.Write(new UltraWideTelemetry
            {
                Ts = 1000L + i,
                M1 = 1f, M2 = 2f, M3 = 3f, M4 = 4f, M5 = 5f, M6 = 60.0f + i,
                C1 = 1, C2 = i % 2
            });
        }
        var ultraComp = new UltraWideTelemetryCompactorBuffers(64);
        long ultraSeq = 0;
        Assert.True(UltraWideTelemetryCompactor.CompactBatch(ultraRing, ref ultraSeq, 64, ultraDir, ultraComp));
        var ultraQuery = new UltraWideTelemetryQueryBuffers(64);
        double avgM6 = UltraWideTelemetryQueryEngine.GetAverageM6ForC2InDirectory(ultraDir, targetC2: 1, 1000L, 2000L, ultraQuery);
        Assert.Equal(92.0, avgM6, precision: 5);
    }

    [Fact]
    public void ZeroCategoryTable_EmitsUnfilteredAndTimeRangeQueries()
    {
        Assert.Equal(64, PureGaugeSampleCompactor.HeaderSize);

        string dir = Path.Combine(_testDir, "pure_gauge");
        const int count = 90;
        var ring = new HotRingBuffer<PureGaugeSample>(128);

        double expectedRangeSum = 0.0;
        int expectedRangeCount = 0;

        for (int i = 0; i < count; i++)
        {
            long ts = 5000L + i * 10L;
            float v = 12.0f + i * 0.02f;
            ring.Write(new PureGaugeSample { SampleTime = ts, Voltage = v, CurrentAmps = 2.5f });

            if (ts >= 5200L && ts <= 5600L)
            {
                expectedRangeSum += v;
                expectedRangeCount++;
            }
        }

        var comp = new PureGaugeSampleCompactorBuffers(128);
        long seq = 0;
        Assert.True(PureGaugeSampleCompactor.CompactBatch(ring, ref seq, count, dir, comp));

        string chunkFile = Path.Combine(dir, "chunk_0.glacier");
        var qBuf = new PureGaugeSampleQueryBuffers(128);

        double rangeAvg = PureGaugeSampleQueryEngine.GetAverageVoltage(chunkFile, 5200L, 5600L, qBuf);
        Assert.Equal(expectedRangeSum / expectedRangeCount, rangeAvg, precision: 5);

        double dirAvg = PureGaugeSampleQueryEngine.GetAverageCurrentAmpsInDirectory(dir, 5000L, 6000L, qBuf);
        Assert.Equal(2.5, dirAvg, precision: 5);
    }
}
