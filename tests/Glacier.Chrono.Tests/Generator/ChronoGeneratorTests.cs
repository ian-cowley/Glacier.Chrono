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
                RecordedAt = DateTime.UtcNow.Ticks + i,
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
    }
}
