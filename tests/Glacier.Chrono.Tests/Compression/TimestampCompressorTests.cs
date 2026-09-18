using System;
using Glacier.Chrono.Compression;
using Xunit;

namespace Glacier.Chrono.Tests.Compression;

public class TimestampCompressorTests
{
    [Fact]
    public void Compress_Empty_ReturnsZero()
    {
        byte[] dest = new byte[32];
        Assert.Equal(0, TimestampCompressor.Compress(ReadOnlySpan<long>.Empty, dest));
        Assert.Equal(0, TimestampCompressor.Decompress(dest, Span<long>.Empty));
    }

    [Fact]
    public void RoundTrip_SingleTimestamp()
    {
        long[] src = [1_700_000_000_000L];
        byte[] buffer = new byte[32];
        int bytes = TimestampCompressor.Compress(src, buffer);
        Assert.Equal(8, bytes);

        long[] dest = new long[1];
        int count = TimestampCompressor.Decompress(buffer.AsSpan(0, bytes), dest);
        Assert.Equal(1, count);
        Assert.Equal(src[0], dest[0]);
    }

    [Fact]
    public void RoundTrip_TwoTimestamps()
    {
        long[] src = [1_000_000L, 1_001_000L];
        byte[] buffer = new byte[32];
        int bytes = TimestampCompressor.Compress(src, buffer);
        Assert.Equal(16, bytes);

        long[] dest = new long[2];
        int count = TimestampCompressor.Decompress(buffer.AsSpan(0, bytes), dest);
        Assert.Equal(2, count);
        Assert.Equal(src[0], dest[0]);
        Assert.Equal(src[1], dest[1]);
    }

    [Fact]
    public void RoundTrip_ConstantInterval_DoDZero_MaximumCompression()
    {
        const int n = 10_000;
        long[] src = new long[n];
        long baseTs = 1_700_000_000_000L;
        for (int i = 0; i < n; i++)
        {
            src[i] = baseTs + (i * 1000L); // Exact 1-second interval
        }

        byte[] buffer = new byte[n * 9 + 64];
        int bytesWritten = TimestampCompressor.Compress(src, buffer);

        // First timestamp (8B) + first delta (8B) + (9998 * 1 bit = 1250B) = ~1266 bytes
        Assert.True(bytesWritten < 1300, $"Expected DoD=0 compression < 1300B but got {bytesWritten}B");

        long[] dest = new long[n];
        int count = TimestampCompressor.Decompress(buffer.AsSpan(0, bytesWritten), dest);

        Assert.Equal(n, count);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(src[i], dest[i]);
        }
    }

    [Theory]
    [InlineData(-63)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(64)]
    public void RoundTrip_7BitDoDBoundaries(long targetDoD)
    {
        // T0, T1 (delta 1000), T2 (delta = 1000 + targetDoD)
        long[] src = [10000L, 11000L, 11000L + 1000L + targetDoD];
        byte[] buf = new byte[64];
        int bytes = TimestampCompressor.Compress(src, buf);

        long[] dest = new long[3];
        TimestampCompressor.Decompress(buf.AsSpan(0, bytes), dest);

        Assert.Equal(src[2], dest[2]);
    }

    [Theory]
    [InlineData(-255)]
    [InlineData(-64)]
    [InlineData(65)]
    [InlineData(256)]
    public void RoundTrip_9BitDoDBoundaries(long targetDoD)
    {
        long[] src = [50000L, 51000L, 51000L + 1000L + targetDoD];
        byte[] buf = new byte[64];
        int bytes = TimestampCompressor.Compress(src, buf);

        long[] dest = new long[3];
        TimestampCompressor.Decompress(buf.AsSpan(0, bytes), dest);

        Assert.Equal(src[2], dest[2]);
    }

    [Theory]
    [InlineData(-2047)]
    [InlineData(-256)]
    [InlineData(257)]
    [InlineData(2048)]
    public void RoundTrip_12BitDoDBoundaries(long targetDoD)
    {
        long[] src = [100000L, 105000L, 105000L + 5000L + targetDoD];
        byte[] buf = new byte[64];
        int bytes = TimestampCompressor.Compress(src, buf);

        long[] dest = new long[3];
        TimestampCompressor.Decompress(buf.AsSpan(0, bytes), dest);

        Assert.Equal(src[2], dest[2]);
    }

    [Theory]
    [InlineData(-100_000L)]
    [InlineData(100_000L)]
    [InlineData(-5_000_000_000L)]
    [InlineData(5_000_000_000L)]
    public void RoundTrip_64BitRawDoDFallback(long targetDoD)
    {
        long[] src = [1_000_000L, 2_000_000L, 2_000_000L + 1_000_000L + targetDoD];
        byte[] buf = new byte[64];
        int bytes = TimestampCompressor.Compress(src, buf);

        long[] dest = new long[3];
        TimestampCompressor.Decompress(buf.AsSpan(0, bytes), dest);

        Assert.Equal(src[2], dest[2]);
    }

    [Fact]
    public void RoundTrip_NegativeDeltas_ClockRewindOrOutOfOrder()
    {
        long[] src = [1000L, 900L, 800L, 700L]; // Descending timestamps
        byte[] buf = new byte[64];
        int bytes = TimestampCompressor.Compress(src, buf);

        long[] dest = new long[4];
        TimestampCompressor.Decompress(buf.AsSpan(0, bytes), dest);

        for (int i = 0; i < src.Length; i++)
        {
            Assert.Equal(src[i], dest[i]);
        }
    }

    [Fact]
    public void TimestampCompressAndDecompress_ZeroHeapAllocations()
    {
        long[] src = new long[1000];
        for (int i = 0; i < src.Length; i++) src[i] = 1_700_000_000_000L + i * 1000L;

        byte[] buf = new byte[src.Length * 9 + 64];
        long[] dest = new long[src.Length];

        // Warmup
        TimestampCompressor.Compress(src, buf);
        TimestampCompressor.Decompress(buf, dest);

        GC.Collect();
        long startBytes = GC.GetAllocatedBytesForCurrentThread();

        int bytes = TimestampCompressor.Compress(src, buf);
        int count = TimestampCompressor.Decompress(buf.AsSpan(0, bytes), dest);

        long endBytes = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, endBytes - startBytes);
    }
}
