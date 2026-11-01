using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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

        for (int i = 0; i < 4; i++)
        {
            bool w = buffer.TryWrite(i, TimeSpan.FromMilliseconds(100));
            Assert.True(w);
        }

        var sw = Stopwatch.StartNew();
        bool success = buffer.TryWrite(999, TimeSpan.FromMilliseconds(50));
        sw.Stop();

        Assert.False(success);
        Assert.True(sw.ElapsedMilliseconds >= 35);
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
        Assert.Equal(4, buffer.WriteCursor);
    }

    [Fact]
    public void Concurrent_SPSC_HighThroughput_IntegrityVerified()
    {
        const int totalItems = 40_000;
        const int capacity = 4096;
        var buffer = new HotRingBuffer<int>(capacity);
        Exception? workerError = null;

        var producer = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < totalItems; i++)
                {
                    buffer.Write(i, TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref workerError, ex, null);
            }
        })
        { IsBackground = true, Name = "SPSC-Producer" };

        var consumer = new Thread(() =>
        {
            try
            {
                int readCount = 0;
                long nextSeq = 0;
                int[] temp = new int[256];
                SpinWait spinner = default;

                while (readCount < totalItems && Volatile.Read(ref workerError) is null)
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
                        spinner.Reset();
                    }
                    else
                    {
                        spinner.SpinOnce();
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref workerError, ex, null);
            }
        })
        { IsBackground = true, Name = "SPSC-Consumer" };

        producer.Start();
        consumer.Start();

        Assert.True(producer.Join(TimeSpan.FromSeconds(15)), "Producer timed out.");
        Assert.True(consumer.Join(TimeSpan.FromSeconds(15)), "Consumer timed out.");
        if (workerError is not null) throw workerError;
    }

    [Fact]
    public void Concurrent_MPMC_StressTest_ZeroDataLoss()
    {
        int producerCount = Math.Clamp(Environment.ProcessorCount, 2, 4);
        const int itemsPerThread = 10_000;
        int totalItems = producerCount * itemsPerThread;
        const int capacity = 4096;

        var buffer = new HotRingBuffer<long>(capacity);
        long[] received = new long[totalItems];
        int receivedIndex = 0;
        Exception? workerError = null;

        var consumer = new Thread(() =>
        {
            try
            {
                long nextSeq = 0;
                long[] batch = new long[256];
                SpinWait spinner = default;

                while (Volatile.Read(ref receivedIndex) < totalItems && Volatile.Read(ref workerError) is null)
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
                        spinner.Reset();
                    }
                    else
                    {
                        spinner.SpinOnce();
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref workerError, ex, null);
            }
        })
        { IsBackground = true, Name = "MPMC-Consumer" };

        var producers = new Thread[producerCount];
        for (int p = 0; p < producerCount; p++)
        {
            int threadId = p;
            producers[p] = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < itemsPerThread; i++)
                    {
                        long val = ((long)(threadId + 1) << 32) | (uint)i;
                        buffer.Write(val, TimeSpan.FromSeconds(10));
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref workerError, ex, null);
                }
            })
            { IsBackground = true, Name = $"MPMC-Producer-{threadId}" };
        }

        consumer.Start();
        foreach (var t in producers) t.Start();

        foreach (var t in producers)
        {
            Assert.True(t.Join(TimeSpan.FromSeconds(15)), "Producer thread timed out.");
        }
        Assert.True(consumer.Join(TimeSpan.FromSeconds(15)), "Consumer thread timed out.");
        if (workerError is not null) throw workerError;

        Assert.Equal(totalItems, Volatile.Read(ref receivedIndex));

        var uniqueReceived = new HashSet<long>(received);
        Assert.Equal(totalItems, uniqueReceived.Count);
    }
}
