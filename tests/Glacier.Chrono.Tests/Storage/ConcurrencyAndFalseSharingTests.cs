using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.Storage;

public class ConcurrencyAndFalseSharingTests
{
    [Fact]
    public void PaddedCursor_Layout_HasFieldAtOffset64AndSize128()
    {
        Assert.Equal(128, Marshal.SizeOf<PaddedCursor>());
        int offset = (int)Marshal.OffsetOf<PaddedCursor>(nameof(PaddedCursor.Value));
        Assert.Equal(64, offset);
    }

    [Fact]
    public void PaddedSequence_Layout_HasSize64AndOffset0()
    {
        Assert.Equal(64, Marshal.SizeOf<PaddedSequence>());
        int offset = (int)Marshal.OffsetOf<PaddedSequence>(nameof(PaddedSequence.Value));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void TryWrite_WhenBufferFullAndReaderPaused_TimesOutGracefully()
    {
        var buffer = new HotRingBuffer<int>(4);

        // Fill buffer to capacity
        for (int i = 0; i < 4; i++)
        {
            bool w = buffer.TryWrite(i, TimeSpan.FromMilliseconds(100));
            Assert.True(w);
        }

        // 5th write must time out because readProgress is -1L
        var sw = Stopwatch.StartNew();
        bool success = buffer.TryWrite(999, TimeSpan.FromMilliseconds(50));
        sw.Stop();

        Assert.False(success);
        Assert.True(sw.ElapsedMilliseconds >= 35);
        // Write cursor must NOT have advanced permanently
        Assert.Equal(4, buffer.WriteCursor);
    }

    [Fact]
    public void TryWrite_RespectsCancellationToken()
    {
        var buffer = new HotRingBuffer<int>(4);
        for (int i = 0; i < 4; i++) buffer.Write(i);

        using var cts = new CancellationTokenSource(30);
        Assert.Throws<OperationCanceledException>(() =>
        {
            buffer.TryWrite(999, TimeSpan.FromSeconds(5), cts.Token);
        });
    }

    [Fact]
    public async Task Concurrent_SPSC_HighThroughput_IntegrityVerified()
    {
        const int totalItems = 100_000;
        const int capacity = 4096;
        var buffer = new HotRingBuffer<int>(capacity);

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < totalItems; i++)
            {
                buffer.Write(i);
            }
        });

        var consumer = Task.Run(() =>
        {
            int readCount = 0;
            long nextSeq = 0;
            int[] temp = new int[256];

            while (readCount < totalItems)
            {
                int toRead = Math.Min(temp.Length, totalItems - readCount);
                var span = temp.AsSpan(0, toRead);
                if (buffer.TryReadBatch(nextSeq, span))
                {
                    for (int i = 0; i < toRead; i++)
                    {
                        Assert.Equal(readCount + i, span[i]);
                    }
                    readCount += toRead;
                    nextSeq += toRead;
                    buffer.CommitReadProgress(nextSeq - 1);
                }
                else
                {
                    Thread.SpinWait(10);
                }
            }
        });

        await Task.WhenAll(producer, consumer);
    }

    [Fact]
    public async Task Concurrent_MPMC_StressTest_ZeroDataLoss()
    {
        const int threadCount = 4;
        const int itemsPerThread = 25_000;
        const int totalItems = threadCount * itemsPerThread;
        const int capacity = 8192;

        var buffer = new HotRingBuffer<long>(capacity);
        long[] received = new long[totalItems];
        int receivedIndex = 0;

        // Start consumer
        var consumer = Task.Run(() =>
        {
            long nextSeq = 0;
            long[] batch = new long[512];
            while (Volatile.Read(ref receivedIndex) < totalItems)
            {
                int needed = Math.Min(batch.Length, totalItems - Volatile.Read(ref receivedIndex));
                if (needed <= 0) break;

                var span = batch.AsSpan(0, needed);
                if (buffer.TryReadBatch(nextSeq, span))
                {
                    for (int i = 0; i < needed; i++)
                    {
                        received[nextSeq + i] = span[i];
                    }
                    nextSeq += needed;
                    Interlocked.Add(ref receivedIndex, needed);
                    buffer.CommitReadProgress(nextSeq - 1);
                }
                else
                {
                    Thread.Yield();
                }
            }
        });

        // Start producers
        Parallel.For(0, threadCount, t =>
        {
            for (int i = 0; i < itemsPerThread; i++)
            {
                long val = ((long)t << 32) | (uint)i;
                buffer.Write(val);
            }
        });

        var completedTask = await Task.WhenAny(consumer, Task.Delay(15000));
        Assert.True(completedTask == consumer, "Consumer timed out waiting for all MPMC items.");
        await consumer;
        Assert.Equal(totalItems, Volatile.Read(ref receivedIndex));

        // Verify zero corruption & zero duplicate data
        var uniqueReceived = new HashSet<long>(received);
        Assert.Equal(totalItems, uniqueReceived.Count);
    }
}
