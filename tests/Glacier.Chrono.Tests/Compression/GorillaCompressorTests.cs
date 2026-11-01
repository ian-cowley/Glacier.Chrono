using System;
using System.IO;
using Glacier.Chrono.Compression;
using Xunit;

namespace Glacier.Chrono.Tests.Compression;

public class GorillaCompressorTests
{
    [Fact]
    public void Compress_EmptySpan_ReturnsZero()
    {
        byte[] dest = new byte[64];
        int bytes = GorillaCompressor.Compress(ReadOnlySpan<float>.Empty, dest);
        Assert.Equal(0, bytes);

        int floats = GorillaCompressor.Decompress(dest, Span<float>.Empty);
        Assert.Equal(0, floats);
    }

    [Fact]
    public void RoundTrip_SingleFloat_IntoDirtyBuffer()
    {
        float[] src = [3.14159f];
        byte[] buffer = new byte[16];
        Array.Fill(buffer, (byte)0xFF);
        int bytesWritten = GorillaCompressor.Compress(src, buffer);
        Assert.Equal(4, bytesWritten);

        float[] dest = new float[1];
        int decompCount = GorillaCompressor.Decompress(buffer.AsSpan(0, bytesWritten), dest);
        Assert.Equal(1, decompCount);
        Assert.Equal(3.14159f, dest[0]);
    }

    [Fact]
    public void RoundTrip_IdenticalFloats_HighCompressionRatio()
    {
        const int count = 1000;
        float[] src = new float[count];
        Array.Fill(src, 123.456f);

        byte[] compressed = new byte[count * 4];
        int bytesWritten = GorillaCompressor.Compress(src, compressed);

        // 32 bits for first + 999 bits of '0' = 1031 bits = 130 bytes
        Assert.True(bytesWritten < 150, $"Expected high compression ratio but got {bytesWritten} bytes.");

        float[] dest = new float[count];
        int decompCount = GorillaCompressor.Decompress(compressed.AsSpan(0, bytesWritten), dest);

        Assert.Equal(count, decompCount);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(123.456f, dest[i]);
        }
    }

    [Theory]
    [InlineData(0.0f, -0.0f)]
    [InlineData(float.NaN, float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity, float.Epsilon)]
    [InlineData(float.MinValue, float.MaxValue)]
    [InlineData(1e-10f, 1e10f)]
    public void RoundTrip_SpecialFloats_BitExactFidelity(float v1, float v2)
    {
        float[] src = [v1, v2, v1, v2];
        byte[] buffer = new byte[128];
        int bytes = GorillaCompressor.Compress(src, buffer);

        float[] dest = new float[4];
        GorillaCompressor.Decompress(buffer.AsSpan(0, bytes), dest);

        for (int i = 0; i < src.Length; i++)
        {
            uint expectedBits = BitConverter.SingleToUInt32Bits(src[i]);
            uint actualBits = BitConverter.SingleToUInt32Bits(dest[i]);
            Assert.Equal(expectedBits, actualBits);
        }
    }

    [Fact]
    public void RoundTrip_SmoothSineWave_CaseAAndCaseB()
    {
        const int n = 5000;
        float[] src = new float[n];
        for (int i = 0; i < n; i++)
        {
            src[i] = 25.0f + (float)Math.Sin(i * 0.01) * 10.0f;
        }

        byte[] compressed = new byte[n * 6 + 64];
        int bytesWritten = GorillaCompressor.Compress(src, compressed);

        float[] dest = new float[n];
        int decompCount = GorillaCompressor.Decompress(compressed.AsSpan(0, bytesWritten), dest);

        Assert.Equal(n, decompCount);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(src[i], dest[i]);
        }
    }

    [Fact]
    public void Decompress_CorruptCaseAZeroWindowOnSecondValue_ThrowsInvalidDataException()
    {
        byte[] corrupt = new byte[16];
        var writer = new BitWriter(corrupt);
        writer.WriteBits(BitConverter.SingleToUInt32Bits(1.0f), 32);
        writer.WriteBit(1); // controlBit = 1 (changed)
        writer.WriteBit(0); // caseBit = 0 (invalid: no previous Case B window exists!)
        writer.Flush();

        float[] dest = new float[2];
        Assert.Throws<InvalidDataException>(() => GorillaCompressor.Decompress(corrupt, dest));
    }

    [Fact]
    public void Decompress_CorruptZeroLengthOrOverflowWindow_ThrowsInvalidDataException()
    {
        // Case 1: len == 0
        byte[] zeroLenStream = new byte[16];
        var w1 = new BitWriter(zeroLenStream);
        w1.WriteBits(BitConverter.SingleToUInt32Bits(1.0f), 32);
        w1.WriteBit(1);        // controlBit = 1
        w1.WriteBit(1);        // caseBit = 1 (new window)
        w1.WriteBits(5, 5);    // lz = 5
        w1.WriteBits(0, 6);    // len = 0 (invalid!)
        w1.Flush();

        float[] dest = new float[2];
        Assert.Throws<InvalidDataException>(() => GorillaCompressor.Decompress(zeroLenStream, dest));

        // Case 2: lz + len > 32
        byte[] overflowWindowStream = new byte[16];
        var w2 = new BitWriter(overflowWindowStream);
        w2.WriteBits(BitConverter.SingleToUInt32Bits(1.0f), 32);
        w2.WriteBit(1);        // controlBit = 1
        w2.WriteBit(1);        // caseBit = 1 (new window)
        w2.WriteBits(20, 5);   // lz = 20
        w2.WriteBits(20, 6);   // len = 20 -> lz + len = 40 > 32 (invalid!)
        w2.WriteBits(0xFFFFF, 20);
        w2.Flush();

        Assert.Throws<InvalidDataException>(() => GorillaCompressor.Decompress(overflowWindowStream, dest));
    }

    [Fact]
    public void GorillaCompressAndDecompress_ZeroHeapAllocations()
    {
        float[] src = new float[1000];
        for (int i = 0; i < src.Length; i++) src[i] = (float)i * 0.5f;

        byte[] compressed = new byte[src.Length * 6 + 64];
        float[] dest = new float[src.Length];

        // JIT warm-up
        GorillaCompressor.Compress(src, compressed);
        GorillaCompressor.Decompress(compressed, dest);

        GC.Collect();
        long startBytes = GC.GetAllocatedBytesForCurrentThread();

        int bytes = GorillaCompressor.Compress(src, compressed);
        int count = GorillaCompressor.Decompress(compressed.AsSpan(0, bytes), dest);

        long endBytes = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, endBytes - startBytes);
        Assert.Equal(src.Length, count);
    }
}
