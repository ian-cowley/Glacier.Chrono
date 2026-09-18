using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.StressChallenge;

public class HotRingBufferMpmcStressTests
{
    [Fact]
    public async Task HotRingBuffer_8Producers4Consumers_120kItems_ZeroLossZeroCorruption()
    {
        const int producerCount = 8;
        const int itemsPerProducer = 15_000;
        const int totalItems = producerCount * itemsPerProducer; // 120,000 items
        const int capacity = 16384; // 16K slot ring buffer

        var buffer = new HotRingBuffer<long>(capacity);
        long[] received = new long[totalItems];
        long nextSequenceToClaim = 0;
        int totalReceivedCount = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // 4 concurrent consumers reading batches with monotonic watermarking
        const int consumerCount = 4;
        var consumerTasks = new Task[consumerCount];
        long globalCommittedWatermark = -1;

        // Sequence completion tracking to safely advance read progress without slot race
        var completedBatches = new ConcurrentDictionary<long, int>();

        for (int c = 0; c < consumerCount; c++)
        {
            consumerTasks[c] = Task.Run(() =>
            {
                long[] localBuffer = new long[256];

                while (!cts.Token.IsCancellationRequested && Volatile.Read(ref totalReceivedCount) < totalItems)
                {
                    long seq = Interlocked.Add(ref nextSequenceToClaim, localBuffer.Length) - localBuffer.Length;
                    if (seq >= totalItems)
                    {
                        break;
                    }

                    int count = (int)Math.Min(localBuffer.Length, totalItems - seq);
                    var span = localBuffer.AsSpan(0, count);

                    // Wait until writer commits this sequence batch
                    SpinWait spinner = default;
                    while (!buffer.TryReadBatch(seq, span))
                    {
                        if (cts.Token.IsCancellationRequested) return;
                        spinner.SpinOnce();
                    }

                    // Store received items
                    for (int i = 0; i < count; i++)
                    {
                        received[seq + i] = span[i];
                    }

                    Interlocked.Add(ref totalReceivedCount, count);
                    completedBatches[seq] = count;

                    // Advance contiguous watermark
                    while (completedBatches.TryRemove(Volatile.Read(ref globalCommittedWatermark) + 1, out int batchLen))
                    {
                        long newWatermark = Interlocked.Add(ref globalCommittedWatermark, batchLen);
                        buffer.CommitReadProgress(newWatermark);
                    }
                }
            });
        }

        // 8 concurrent producers under intense contention
        var producerTasks = new Task[producerCount];
        for (int p = 0; p < producerCount; p++)
        {
            int producerId = p;
            producerTasks[p] = Task.Run(() =>
            {
                for (int i = 0; i < itemsPerProducer; i++)
                {
                    long payload = ((long)(producerId + 1) << 32) | (uint)i;
                    buffer.Write(payload, TimeSpan.FromSeconds(10), cts.Token);
                }
            });
        }

        // Await all producers and consumers
        await Task.WhenAll(producerTasks);
        await Task.WhenAll(consumerTasks);

        Assert.Equal(totalItems, Volatile.Read(ref totalReceivedCount));

        // Verify zero duplicates & zero corruption across all 120,000 items
        var seen = new HashSet<long>(totalItems);
        for (int i = 0; i < totalItems; i++)
        {
            long item = received[i];
            Assert.NotEqual(0L, item);
            Assert.True(seen.Add(item), $"Duplicate item detected at index {i}: {item:X16}");
        }

        Assert.Equal(totalItems, seen.Count);
    }

    [Fact]
    public void HotRingBuffer_ExtremeContention_BoundedSpinWait_TimesOutDeterministically()
    {
        const int capacity = 8;
        var buffer = new HotRingBuffer<int>(capacity);

        // Fill buffer to capacity
        for (int i = 0; i < capacity; i++)
        {
            bool ok = buffer.TryWrite(i, TimeSpan.FromMilliseconds(50));
            Assert.True(ok);
        }

        // 9th write with no reader progress MUST time out gracefully without unbounded spinning
        var sw = Stopwatch.StartNew();
        bool timedOut = !buffer.TryWrite(999, TimeSpan.FromMilliseconds(100));
        sw.Stop();

        Assert.True(timedOut, "Expected TryWrite to time out when buffer is full.");
        Assert.True(sw.ElapsedMilliseconds >= 70, $"Expected timeout around 100ms, elapsed: {sw.ElapsedMilliseconds}ms");
    }
}
