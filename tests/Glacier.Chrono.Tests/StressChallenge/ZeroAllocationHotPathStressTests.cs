using System;
using Glacier.Chrono.Compression;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.StressChallenge;

public class ZeroAllocationHotPathStressTests
{
    [Fact]
    public void HotRingBuffer_100kIngestionAndRead_ZeroHeapAllocations()
    {
        const int capacity = 4096;
        const int totalItems = 100_000;
        var buffer = new HotRingBuffer<long>(capacity);
        long[] consumerBatch = new long[256];

        // Warm-up JIT and buffer state
        for (int i = 0; i < 2048; i++)
        {
            buffer.Write((long)i);
        }
        for (int i = 0; i < 2048; i += 256)
        {
            buffer.TryReadBatch(i, consumerBatch.AsSpan(0, 256));
        }
        buffer.CommitReadProgress(2047);

        // Clear GC counters
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        long writeSeq = 2048;
        long readSeq = 2048;

        // Execute 100k lock-free zero-allocation write & read cycles
        while (writeSeq < totalItems + 2048)
        {
            int batch = Math.Min(256, (int)(totalItems + 2048 - writeSeq));
            for (int b = 0; b < batch; b++)
            {
                buffer.Write(writeSeq + b);
            }
            writeSeq += batch;

            var span = consumerBatch.AsSpan(0, batch);
            bool readOk = buffer.TryReadBatch(readSeq, span);
            Assert.True(readOk);

            readSeq += batch;
            buffer.CommitReadProgress(readSeq - 1);
        }

        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        long diff = allocatedAfter - allocatedBefore;

        Assert.Equal(0, diff);
    }

    [Fact]
    public void GorillaCompressAndDecompress_10kFloats_ZeroHeapAllocations()
    {
        const int count = 10_000;
        float[] src = new float[count];
        for (int i = 0; i < count; i++)
        {
            src[i] = 100.0f + (float)Math.Sin(i * 0.05) * 5.0f;
        }

        byte[] destBuffer = new byte[count * 6 + 128];
        float[] decompBuffer = new float[count];

        // Warm-up JIT
        int warmBytes = GorillaCompressor.Compress(src, destBuffer);
        GorillaCompressor.Decompress(destBuffer.AsSpan(0, warmBytes), decompBuffer);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long startBytes = GC.GetAllocatedBytesForCurrentThread();

        int bytesWritten = GorillaCompressor.Compress(src, destBuffer);
        int decompressedCount = GorillaCompressor.Decompress(destBuffer.AsSpan(0, bytesWritten), decompBuffer);

        long endBytes = GC.GetAllocatedBytesForCurrentThread();
        long allocated = endBytes - startBytes;

        Assert.Equal(0, allocated);
        Assert.Equal(count, decompressedCount);
    }
}
