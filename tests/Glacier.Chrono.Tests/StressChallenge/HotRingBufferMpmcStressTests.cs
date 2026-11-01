using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.StressChallenge;

public class HotRingBufferMpmcStressTests
{
    [Fact]
    public void HotRingBuffer_MultiProducerMultiConsumer_ZeroLossZeroCorruption()
    {
        // Scale threads to available cores (2..4) and use dedicated OS threads to prevent ThreadPool starvation on 2-vCPU CI runners
        int producerCount = Math.Clamp(Environment.ProcessorCount, 2, 4);
        int consumerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        const int itemsPerProducer = 10_000;
        int totalItems = producerCount * itemsPerProducer;
        const int capacity = 4096;

        var buffer = new HotRingBuffer<long>(capacity);
        long[] received = new long[totalItems];
        long nextSequenceToClaim = 0;
        int totalReceivedCount = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Exception? workerError = null;

        long globalCommittedWatermark = -1;
        var completedBatches = new ConcurrentDictionary<long, int>();

        var consumerThreads = new Thread[consumerCount];
        for (int c = 0; c < consumerCount; c++)
        {
            int cid = c;
            consumerThreads[c] = new Thread(() =>
            {
                try
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

                        SpinWait spinner = default;
                        while (!buffer.TryReadBatch(seq, span))
                        {
                            if (cts.Token.IsCancellationRequested || Volatile.Read(ref workerError) is not null) return;
                            if (spinner.NextSpinWillYield)
                            {
                                Thread.Yield();
                            }
                            else
                            {
                                spinner.SpinOnce();
                            }
                        }

                        for (int i = 0; i < count; i++)
                        {
                            received[seq + i] = span[i];
                        }

                        Interlocked.Add(ref totalReceivedCount, count);
                        completedBatches[seq] = count;

                        while (completedBatches.TryRemove(Volatile.Read(ref globalCommittedWatermark) + 1, out int batchLen))
                        {
                            long newWatermark = Interlocked.Add(ref globalCommittedWatermark, batchLen);
                            buffer.CommitReadProgress(newWatermark);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref workerError, ex, null);
                }
            })
            { IsBackground = true, Name = $"StressConsumer-{cid}" };
        }

        var producerThreads = new Thread[producerCount];
        for (int p = 0; p < producerCount; p++)
        {
            int producerId = p;
            producerThreads[p] = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < itemsPerProducer; i++)
                    {
                        long payload = ((long)(producerId + 1) << 32) | (uint)i;
                        buffer.Write(payload, TimeSpan.FromSeconds(10), cts.Token);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref workerError, ex, null);
                }
            })
            { IsBackground = true, Name = $"StressProducer-{producerId}" };
        }

        foreach (var t in consumerThreads) t.Start();
        foreach (var t in producerThreads) t.Start();

        foreach (var t in producerThreads)
        {
            Assert.True(t.Join(TimeSpan.FromSeconds(20)), "Producer timed out.");
        }
        foreach (var t in consumerThreads)
        {
            Assert.True(t.Join(TimeSpan.FromSeconds(20)), "Consumer timed out.");
        }
        if (workerError is not null) throw workerError;

        Assert.Equal(totalItems, Volatile.Read(ref totalReceivedCount));

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

        for (int i = 0; i < capacity; i++)
        {
            bool ok = buffer.TryWrite(i, TimeSpan.FromMilliseconds(50));
            Assert.True(ok);
        }

        var sw = Stopwatch.StartNew();
        bool timedOut = !buffer.TryWrite(999, TimeSpan.FromMilliseconds(100));
        sw.Stop();

        Assert.True(timedOut, "Expected TryWrite to time out when buffer is full.");
        Assert.True(sw.ElapsedMilliseconds >= 70, $"Expected timeout around 100ms, elapsed: {sw.ElapsedMilliseconds}ms");
        Assert.Equal(capacity, buffer.WriteCursor);
    }
}
