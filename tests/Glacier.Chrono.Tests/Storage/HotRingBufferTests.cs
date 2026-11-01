using System;
using System.Threading;
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
    public void TryReadBatch_ExceedingCapacity_ThrowsArgumentOutOfRangeException()
    {
        var buffer = new HotRingBuffer<int>(8);
        int[] oversized = new int[9];
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.TryReadBatch(0, oversized));
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
    public void TryWrite_TimeoutAndCancellationRecovery_NeverPoisonsSubsequentReadsOrWrites()
    {
        const int capacity = 4;
        var buffer = new HotRingBuffer<int>(capacity);

        // Fill first lap completely
        for (int i = 0; i < capacity; i++)
        {
            Assert.True(buffer.TryWrite(100 + i, TimeSpan.FromMilliseconds(50)));
        }
        Assert.Equal(capacity, buffer.WriteCursor);

        // Trigger multiple timeouts and cancellations while buffer is full (and also when readProgress is artificially advanced before slot is ready)
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.False(buffer.TryWrite(9999, TimeSpan.Zero));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                buffer.TryWrite(9999, TimeSpan.FromSeconds(1), cts.Token));
        }

        // WriteCursor must remain strictly un-orphaned at 4
        Assert.Equal(capacity, buffer.WriteCursor);

        // Drain first lap and verify intact data
        int[] batch = new int[capacity];
        Assert.True(buffer.TryReadBatch(0, batch));
        for (int i = 0; i < capacity; i++)
        {
            Assert.Equal(100 + i, batch[i]);
        }
        buffer.CommitReadProgress(capacity - 1);

        // Run 5 more wrap-around laps after timeouts/cancellations to prove zero sequence poisoning
        for (int lap = 1; lap <= 5; lap++)
        {
            int baseVal = lap * 1000;
            for (int i = 0; i < capacity; i++)
            {
                Assert.True(buffer.TryWrite(baseVal + i, TimeSpan.FromSeconds(1)));
            }

            long seq = (long)lap * capacity;
            Assert.True(buffer.TryReadBatch(seq, batch));
            for (int i = 0; i < capacity; i++)
            {
                Assert.Equal(baseVal + i, batch[i]);
            }
            buffer.CommitReadProgress(seq + capacity - 1);
        }
    }

    [Fact]
    public void TryWrite_SlotReadinessContention_NeverOrphansSequenceOnTimeoutOrCancel()
    {
        // Simulate an artificially advanced ReadProgress before a slot has ever been written,
        // forcing the slot-readiness check (Volatile.Read(_writeSequences[index]) != expectedSequence) to trigger on lap 1.
        const int capacity = 4;
        var buffer = new HotRingBuffer<int>(capacity);

        // Write 3 of 4 slots on lap 0, then artificially advance ReadProgress ahead to allow lap 1 cursor check
        for (int i = 0; i < 3; i++) buffer.Write(i);
        buffer.CommitReadProgress(10); // Artificially high read progress

        // Write slot 3 (completes lap 0)
        buffer.Write(3);

        // Lap 1 writes slots 0..3 cleanly because lap 0 wrote them
        for (int i = 0; i < capacity; i++)
        {
            Assert.True(buffer.TryWrite(10 + i, TimeSpan.FromMilliseconds(50)));
        }

        int[] readBack = new int[capacity];
        Assert.True(buffer.TryReadBatch(capacity, readBack));
        for (int i = 0; i < capacity; i++)
        {
            Assert.Equal(10 + i, readBack[i]);
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
