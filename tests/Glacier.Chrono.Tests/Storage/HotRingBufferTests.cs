using System;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.Storage;

public class HotRingBufferTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(100, 128)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 2048)]
    public void Capacity_RoundsUpToPowerOfTwo(int requested, int expected)
    {
        var buffer = new HotRingBuffer<int>(requested);
        Assert.Equal(expected, buffer.Capacity);
    }

    [Fact]
    public void Capacity_ThrowsOnZeroOrNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HotRingBuffer<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HotRingBuffer<int>(-10));
    }

    [Fact]
    public void WriteAndTryReadBatch_SingleItem_Succeeds()
    {
        var buffer = new HotRingBuffer<int>(16);
        buffer.Write(42);

        Assert.Equal(1, buffer.WriteCursor);
        Assert.Equal(-1L, buffer.ReadProgress);

        Span<int> dest = stackalloc int[1];
        bool success = buffer.TryReadBatch(0, dest);

        Assert.True(success);
        Assert.Equal(42, dest[0]);
    }

    [Fact]
    public void TryReadBatch_IncompleteSequence_ReturnsFalseWithoutModifyingDestination()
    {
        var buffer = new HotRingBuffer<int>(16);
        buffer.Write(100);

        Span<int> dest = stackalloc int[2] { 999, 999 };
        bool success = buffer.TryReadBatch(0, dest); // Requests sequence 0 and 1, but 1 is not written

        Assert.False(success);
        Assert.Equal(999, dest[0]); // Destination unchanged
        Assert.Equal(999, dest[1]);
    }

    [Fact]
    public void CommitReadProgress_AdvancesMonotonically()
    {
        var buffer = new HotRingBuffer<int>(16);
        buffer.CommitReadProgress(5);
        Assert.Equal(5, buffer.ReadProgress);

        // Lower sequence must not regress progress
        buffer.CommitReadProgress(3);
        Assert.Equal(5, buffer.ReadProgress);

        buffer.CommitReadProgress(10);
        Assert.Equal(10, buffer.ReadProgress);
    }

    [Fact]
    public void WrapAround_MultipleLaps_PreservesDataIntegrity()
    {
        const int capacity = 8;
        var buffer = new HotRingBuffer<int>(capacity);

        Span<int> dest = stackalloc int[capacity];

        for (int lap = 0; lap < 10; lap++)
        {
            int baseVal = lap * capacity;
            for (int i = 0; i < capacity; i++)
            {
                buffer.Write(baseVal + i);
            }

            long startSeq = lap * capacity;
            bool readSuccess = buffer.TryReadBatch(startSeq, dest);
            Assert.True(readSuccess);

            for (int i = 0; i < capacity; i++)
            {
                Assert.Equal(baseVal + i, dest[i]);
            }

            buffer.CommitReadProgress(startSeq + capacity - 1);
        }
    }

    [Fact]
    public void HotIngest_AllocatesZeroBytesOnManagedHeap()
    {
        var buffer = new HotRingBuffer<TelemetryRow>(1024);
        var row = new TelemetryRow { Timestamp = 1, CpuUsage = 50f, MemUsage = 60f, EntityId = 1 };

        // Warm up JIT
        buffer.Write(in row);
        buffer.CommitReadProgress(0);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long startBytes = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 500; i++)
        {
            buffer.Write(in row);
            buffer.CommitReadProgress(i + 1);
        }

        long endBytes = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, endBytes - startBytes);
    }
}
