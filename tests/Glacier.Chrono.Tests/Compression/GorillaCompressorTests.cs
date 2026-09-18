using System;
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

        float[] decomp = new float[10];
        int floats = GorillaCompressor.Decompress(dest, Span<float>.Empty);
        Assert.Equal(0, floats);
    }

    [Fact]
    public void RoundTrip_SingleFloat()
    {
        float[] src = [3.14159f];
        byte[] buffer = new byte[16];
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
