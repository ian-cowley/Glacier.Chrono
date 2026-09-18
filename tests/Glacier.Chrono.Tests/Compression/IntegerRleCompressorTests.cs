using System;
using Glacier.Chrono.Compression;
using Xunit;

namespace Glacier.Chrono.Tests.Compression;

public class IntegerRleCompressorTests
{
    [Fact]
    public void Compress_EmptySpan_ReturnsZero()
    {
        byte[] buf = new byte[32];
        Assert.Equal(0, IntegerRleCompressor.Compress(ReadOnlySpan<int>.Empty, buf));
        Assert.Equal(0, IntegerRleCompressor.Decompress(buf, Span<int>.Empty));
    }

    [Fact]
    public void RoundTrip_SingleInteger()
    {
        int[] src = [42];
        byte[] buf = new byte[32];
        int bytes = IntegerRleCompressor.Compress(src, buf);

        int[] dest = new int[1];
        int count = IntegerRleCompressor.Decompress(buf.AsSpan(0, bytes), dest);

        Assert.Equal(1, count);
        Assert.Equal(42, dest[0]);
    }

    [Fact]
    public void RoundTrip_LongRunOfIdenticalValues()
    {
        const int count = 5000;
        int[] src = new int[count];
        Array.Fill(src, 7);

        byte[] buf = new byte[count * 5 + 64];
        int bytesWritten = IntegerRleCompressor.Compress(src, buf);

        // Count (32b) + flag(1b) + val(32b) + runLen(32b) = 97 bits = 13 bytes
        Assert.True(bytesWritten <= 16, $"Expected RLE <= 16 bytes, but got {bytesWritten}");

        int[] dest = new int[count];
        int decompCount = IntegerRleCompressor.Decompress(buf.AsSpan(0, bytesWritten), dest);

        Assert.Equal(count, decompCount);
        for (int i = 0; i < count; i++) Assert.Equal(7, dest[i]);
    }

    [Fact]
    public void RoundTrip_AllDistinctValues()
    {
        int[] src = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        byte[] buf = new byte[src.Length * 5 + 64];
        int bytes = IntegerRleCompressor.Compress(src, buf);

        int[] dest = new int[src.Length];
        int count = IntegerRleCompressor.Decompress(buf.AsSpan(0, bytes), dest);

        Assert.Equal(src.Length, count);
        for (int i = 0; i < src.Length; i++) Assert.Equal(src[i], dest[i]);
    }

    [Fact]
    public void RoundTrip_NegativeAndBoundaryIntegers()
    {
        int[] src = [int.MinValue, -1, 0, 1, int.MaxValue, -1, -1, -1];
        byte[] buf = new byte[src.Length * 5 + 64];
        int bytes = IntegerRleCompressor.Compress(src, buf);

        int[] dest = new int[src.Length];
        IntegerRleCompressor.Decompress(buf.AsSpan(0, bytes), dest);

        Assert.Equal(src, dest);
    }

    [Fact]
    public void Decompress_DestinationTooSmall_ThrowsArgumentException()
    {
        int[] src = [1, 1, 1, 1, 1];
        byte[] buf = new byte[64];
        int bytes = IntegerRleCompressor.Compress(src, buf);

        int[] dest = new int[3]; // Capacity 3 < Count 5
        Assert.Throws<ArgumentException>(() =>
        {
            IntegerRleCompressor.Decompress(buf.AsSpan(0, bytes), dest);
        });
    }

    [Fact]
    public void IntegerRle_ZeroHeapAllocations()
    {
        int[] src = new int[1000];
        for (int i = 0; i < src.Length; i++) src[i] = i / 10;

        byte[] buf = new byte[src.Length * 5 + 64];
        int[] dest = new int[src.Length];

        IntegerRleCompressor.Compress(src, buf);
        IntegerRleCompressor.Decompress(buf, dest);

        GC.Collect();
        long startBytes = GC.GetAllocatedBytesForCurrentThread();

        int bytes = IntegerRleCompressor.Compress(src, buf);
        int count = IntegerRleCompressor.Decompress(buf.AsSpan(0, bytes), dest);

        long endBytes = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, endBytes - startBytes);
        Assert.Equal(src.Length, count);
    }
}
