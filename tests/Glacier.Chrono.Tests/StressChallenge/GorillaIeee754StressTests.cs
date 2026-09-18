using System;
using System.Collections.Generic;
using Glacier.Chrono.Compression;
using Xunit;

namespace Glacier.Chrono.Tests.StressChallenge;

public class GorillaIeee754StressTests
{
    [Fact]
    public void Gorilla_AllIeee754Specials_BitExactFidelity()
    {
        // Construct comprehensive suite of IEEE 754 specials:
        var specials = new List<float>
        {
            // Zeros
            0.0f,
            -0.0f,
            // Infinities
            float.PositiveInfinity,
            float.NegativeInfinity,
            // NaNs
            float.NaN,
            BitConverter.UInt32BitsToSingle(0x7FC00000), // Quiet NaN
            BitConverter.UInt32BitsToSingle(0x7F800001), // Signaling NaN
            BitConverter.UInt32BitsToSingle(0xFFC00000), // Negative NaN
            BitConverter.UInt32BitsToSingle(0x7FFFFFFF), // Max payload NaN
            // Extremes
            float.MaxValue,
            float.MinValue,
            // Subnormals
            float.Epsilon,                                // Smallest positive subnormal (0x00000001)
            BitConverter.UInt32BitsToSingle(0x007FFFFF), // Largest positive subnormal
            BitConverter.UInt32BitsToSingle(0x80000001), // Smallest negative subnormal
            BitConverter.UInt32BitsToSingle(0x807FFFFF), // Largest negative subnormal
            1e-40f,
            -1e-40f,
            // Boundary normals
            1e-38f,
            -1e-38f,
            1.0f,
            -1.0f
        };

        var src = specials.ToArray();
        byte[] buffer = new byte[src.Length * 8 + 64];

        int bytesWritten = GorillaCompressor.Compress(src, buffer);
        Assert.True(bytesWritten > 0);

        float[] decompressed = new float[src.Length];
        int decompCount = GorillaCompressor.Decompress(buffer.AsSpan(0, bytesWritten), decompressed);

        Assert.Equal(src.Length, decompCount);

        for (int i = 0; i < src.Length; i++)
        {
            uint expectedBits = BitConverter.SingleToUInt32Bits(src[i]);
            uint actualBits = BitConverter.SingleToUInt32Bits(decompressed[i]);

            Assert.True(expectedBits == actualBits,
                $"Bit mismatch at index {i}: Expected 0x{expectedBits:X8} ({src[i]}), got 0x{actualBits:X8} ({decompressed[i]})");
        }
    }

    [Theory]
    [InlineData(0.0f, -0.0f, 1000)]
    [InlineData(float.NaN, float.NaN, 500)]
    [InlineData(-0.0f, -0.0f, 500)]
    [InlineData(float.PositiveInfinity, float.NegativeInfinity, 600)]
    [InlineData(float.Epsilon, float.MaxValue, 800)]
    public void Gorilla_HomogeneousAndAlternatingSpecials_RoundTripStress(float val1, float val2, int count)
    {
        float[] src = new float[count];
        for (int i = 0; i < count; i++)
        {
            src[i] = (i % 2 == 0) ? val1 : val2;
        }

        byte[] buffer = new byte[count * 8 + 128];
        int bytesWritten = GorillaCompressor.Compress(src, buffer);
        Assert.True(bytesWritten > 0);

        float[] dest = new float[count];
        int decompCount = GorillaCompressor.Decompress(buffer.AsSpan(0, bytesWritten), dest);

        Assert.Equal(count, decompCount);
        for (int i = 0; i < count; i++)
        {
            uint expected = BitConverter.SingleToUInt32Bits(src[i]);
            uint actual = BitConverter.SingleToUInt32Bits(dest[i]);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Gorilla_MixedAdversarialStream_10kValues_PreservesEveryBit()
    {
        const int count = 10_000;
        float[] src = new float[count];
        var rng = new Random(777);

        float[] pool =
        [
            0.0f, -0.0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
            float.MaxValue, float.MinValue, float.Epsilon, 1e-35f, -1e-35f,
            3.14159265f, 2.71828182f, 0.5f, -0.5f, 100.0f, -100.0f
        ];

        for (int i = 0; i < count; i++)
        {
            // 70% random normal floats, 30% adversarial specials
            if (rng.NextDouble() < 0.3)
            {
                src[i] = pool[rng.Next(pool.Length)];
            }
            else
            {
                src[i] = (float)(rng.NextDouble() * 10000.0 - 5000.0);
            }
        }

        byte[] buffer = new byte[count * 8 + 256];
        int bytesWritten = GorillaCompressor.Compress(src, buffer);
        Assert.True(bytesWritten > 0);

        float[] dest = new float[count];
        int decompCount = GorillaCompressor.Decompress(buffer.AsSpan(0, bytesWritten), dest);

        Assert.Equal(count, decompCount);
        for (int i = 0; i < count; i++)
        {
            uint expectedBits = BitConverter.SingleToUInt32Bits(src[i]);
            uint actualBits = BitConverter.SingleToUInt32Bits(dest[i]);
            Assert.True(expectedBits == actualBits,
                $"Bit mismatch at index {i}/{count}: Expected 0x{expectedBits:X8}, got 0x{actualBits:X8}");
        }
    }
}
