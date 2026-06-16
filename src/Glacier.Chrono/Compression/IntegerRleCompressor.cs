using System;

namespace Glacier.Chrono.Compression;

public static class IntegerRleCompressor
{
    /// <summary>
    /// Compresses a span of 32-bit integers using Run-Length Encoding (RLE).
    /// Returns the number of bytes written to the destination buffer.
    /// </summary>
    public static int Compress(ReadOnlySpan<int> values, Span<byte> destination)
    {
        if (values.IsEmpty) return 0;

        var writer = new BitWriter(destination);

        // Write total count of elements first (32 bits)
        writer.WriteBits((ulong)values.Length, 32);

        int i = 0;
        while (i < values.Length)
        {
            int val = values[i];
            int runLength = 1;
            while (i + runLength < values.Length && values[i + runLength] == val)
            {
                runLength++;
            }

            if (runLength == 1)
            {
                writer.WriteBit(0);
                writer.WriteBits((ulong)(uint)val, 32);
            }
            else
            {
                writer.WriteBit(1);
                writer.WriteBits((ulong)(uint)val, 32);
                writer.WriteBits((ulong)runLength, 32);
            }

            i += runLength;
        }

        writer.Flush();
        return writer.BytesWritten;
    }

    /// <summary>
    /// Decompresses a buffer of bytes into a span of 32-bit integers.
    /// Returns the number of integers successfully decompressed.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> source, Span<int> destination)
    {
        if (destination.IsEmpty) return 0;

        var reader = new BitReader(source);

        // Read total count of elements (32 bits)
        int totalCount = (int)reader.ReadBits(32);
        if (totalCount > destination.Length)
        {
            throw new ArgumentException("Destination span is too small for the decompressed data.", nameof(destination));
        }

        int destIndex = 0;
        while (destIndex < totalCount)
        {
            int controlBit = reader.ReadBit();
            int val = (int)reader.ReadBits(32);

            if (controlBit == 0)
            {
                destination[destIndex++] = val;
            }
            else
            {
                int runLength = (int)reader.ReadBits(32);
                if (destIndex + runLength > totalCount)
                {
                    throw new InvalidOperationException("Corrupt RLE stream: run length exceeds total count.");
                }

                destination.Slice(destIndex, runLength).Fill(val);
                destIndex += runLength;
            }
        }

        return totalCount;
    }
}
