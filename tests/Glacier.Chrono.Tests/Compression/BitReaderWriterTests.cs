using System;
using Glacier.Chrono.Compression;
using Xunit;

namespace Glacier.Chrono.Tests.Compression;

public class BitReaderWriterTests
{
    [Fact]
    public void WriteBit_And_ReadBit_RoundTrip()
    {
        byte[] buffer = new byte[16];
        var writer = new BitWriter(buffer);

        int[] bits = [1, 0, 1, 1, 0, 0, 1, 0, 1];
        foreach (var b in bits) writer.WriteBit(b);
        writer.Flush();

        var reader = new BitReader(buffer);
        foreach (var expected in bits)
        {
            Assert.Equal(expected, reader.ReadBit());
        }
    }

    [Fact]
    public void WriteBits_AcrossByteBoundaries_ReadsCorrectly()
    {
        byte[] buffer = new byte[32];
        var writer = new BitWriter(buffer);

        writer.WriteBits(0b101, 3);
        writer.WriteBits(0b11110000, 8);
        writer.WriteBits(0b10101, 5);
        writer.WriteBits(0x12345678, 32);
        writer.Flush();

        var reader = new BitReader(buffer);
        Assert.Equal(0b101UL, reader.ReadBits(3));
        Assert.Equal(0b11110000UL, reader.ReadBits(8));
        Assert.Equal(0b10101UL, reader.ReadBits(5));
        Assert.Equal(0x12345678UL, reader.ReadBits(32));
    }

    [Fact]
    public void WriteBits_And_ReadBits_AllWidths1To64_AcrossAllBitAlignments0To7_RoundTrip()
    {
        byte[] buffer = new byte[4096];

        // Test every initial bit offset (0..7) combined with every bit width (1..64)
        for (int alignment = 0; alignment <= 7; alignment++)
        {
            Array.Fill(buffer, (byte)0xCC); // Non-zero dirty destination buffer
            var writer = new BitWriter(buffer);

            ulong prefix = alignment == 0 ? 0UL : (0xABUL & ((1UL << alignment) - 1));
            if (alignment > 0)
            {
                writer.WriteBits(prefix, alignment);
            }

            for (int width = 1; width <= 64; width++)
            {
                ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
                ulong payload = (0xDEADBEEFCAFEBABEUL ^ ((ulong)width * 0x9E3779B97F4A7C15UL)) & mask;
                writer.WriteBits(payload, width);
            }

            // Also test ulong.MaxValue and 0UL at 64-bit width while unaligned
            writer.WriteBits(ulong.MaxValue, 64);
            writer.WriteBits(0UL, 64);
            writer.WriteBits(0x8000_0000_0000_0001UL, 64);
            writer.Flush();

            var reader = new BitReader(buffer.AsSpan(0, writer.BytesWritten));
            if (alignment > 0)
            {
                Assert.Equal(prefix, reader.ReadBits(alignment));
            }

            for (int width = 1; width <= 64; width++)
            {
                ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
                ulong expected = (0xDEADBEEFCAFEBABEUL ^ ((ulong)width * 0x9E3779B97F4A7C15UL)) & mask;
                ulong actual = reader.ReadBits(width);
                Assert.Equal(expected, actual);
            }

            Assert.Equal(ulong.MaxValue, reader.ReadBits(64));
            Assert.Equal(0UL, reader.ReadBits(64));
            Assert.Equal(0x8000_0000_0000_0001UL, reader.ReadBits(64));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65)]
    [InlineData(128)]
    public void WriteBits_And_ReadBits_InvalidBitCount_ThrowsArgumentOutOfRangeException(int invalidCount)
    {
        byte[] buffer = new byte[16];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var writer = new BitWriter(buffer);
            writer.WriteBits(1UL, invalidCount);
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var reader = new BitReader(buffer);
            _ = reader.ReadBits(invalidCount);
        });
    }

    [Fact]
    public void WriteBits_DestinationOverflow_ThrowsInvalidOperationException()
    {
        byte[] buffer = new byte[1];
        Assert.Throws<InvalidOperationException>(() =>
        {
            var writer = new BitWriter(buffer);
            writer.WriteBits(0xFFFFFFFF, 32);
        });

        // Also test overflow on split 64-bit write when unaligned
        byte[] smallBuf = new byte[4];
        Assert.Throws<InvalidOperationException>(() =>
        {
            var writer = new BitWriter(smallBuf);
            writer.WriteBit(1);
            writer.WriteBits(ulong.MaxValue, 64);
        });
    }

    [Fact]
    public void ReadBits_PastSourceLength_ThrowsInvalidOperationException()
    {
        byte[] buffer = [0xAA];
        Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new BitReader(buffer);
            _ = reader.ReadBits(8);
            _ = reader.ReadBits(1);
        });
    }
}
