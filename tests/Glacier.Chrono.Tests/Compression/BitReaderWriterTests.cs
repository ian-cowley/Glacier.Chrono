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
    public void WriteBits_DestinationOverflow_ThrowsInvalidOperationException()
    {
        byte[] buffer = new byte[1];
        var writer = new BitWriter(buffer);

        bool threw = false;
        try
        {
            writer.WriteBits(0xFFFFFFFF, 32); // 4 bytes won't fit in 1 byte
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.True(threw, "Expected InvalidOperationException when destination buffer overflows.");
    }

    [Fact]
    public void ReadBits_PastSourceLength_ThrowsInvalidOperationException()
    {
        byte[] buffer = [0xAA];
        var reader = new BitReader(buffer);
        _ = reader.ReadBits(8); // Read all 8 bits

        bool threw = false;
        try
        {
            reader.ReadBits(1); // 9th bit doesn't exist
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.True(threw, "Expected InvalidOperationException when reading past source buffer.");
    }
}
