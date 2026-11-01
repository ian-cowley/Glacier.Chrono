using System;

namespace Glacier.Chrono.Compression;

public static class TimestampCompressor
{
    /// <summary>
    /// Compresses a span of 64-bit integer timestamps using Delta-of-Delta (DoD) compression.
    /// Returns the number of bytes written to the destination buffer.
    /// </summary>
    public static int Compress(ReadOnlySpan<long> values, Span<byte> destination)
    {
        if (values.IsEmpty) return 0;

        var writer = new BitWriter(destination);

        // Store first timestamp in full (64 bits)
        writer.WriteBits((ulong)values[0], 64);

        if (values.Length == 1)
        {
            writer.Flush();
            return writer.BytesWritten;
        }

        // Store first delta in full (64 bits, explicit unchecked two's-complement delta)
        long firstDelta = unchecked(values[1] - values[0]);
        writer.WriteBits((ulong)firstDelta, 64);

        long prevDelta = firstDelta;
        long prevVal = values[1];

        for (int i = 2; i < values.Length; i++)
        {
            long currVal = values[i];
            long currDelta;
            long dod;
            unchecked
            {
                currDelta = currVal - prevVal;
                dod = currDelta - prevDelta;
            }

            if (dod == 0)
            {
                writer.WriteBit(0);
            }
            else if (dod >= -63 && dod <= 64)
            {
                // Write '10' (2 bits), then write dod + 63 (7 bits)
                writer.WriteBit(1);
                writer.WriteBit(0);
                writer.WriteBits((ulong)unchecked(dod + 63), 7);
            }
            else if (dod >= -255 && dod <= 256)
            {
                // Write '110' (3 bits), then write dod + 255 (9 bits)
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBit(0);
                writer.WriteBits((ulong)unchecked(dod + 255), 9);
            }
            else if (dod >= -2047 && dod <= 2048)
            {
                // Write '1110' (4 bits), then write dod + 2047 (12 bits)
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBit(0);
                writer.WriteBits((ulong)unchecked(dod + 2047), 12);
            }
            else
            {
                // Write '1111' (4 bits), then write raw dod (64 bits)
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBits((ulong)dod, 64);
            }

            prevDelta = currDelta;
            prevVal = currVal;
        }

        writer.Flush();
        return writer.BytesWritten;
    }

    /// <summary>
    /// Decompresses a buffer of bytes into a span of 64-bit integer timestamps.
    /// Returns the number of timestamps successfully decompressed.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> source, Span<long> destination)
    {
        if (destination.IsEmpty) return 0;

        var reader = new BitReader(source);

        // Read first timestamp (64 bits)
        long firstVal = unchecked((long)reader.ReadBits(64));
        destination[0] = firstVal;

        if (destination.Length == 1)
        {
            return 1;
        }

        // Read first delta (64 bits)
        long firstDelta = unchecked((long)reader.ReadBits(64));
        destination[1] = unchecked(firstVal + firstDelta);

        long prevDelta = firstDelta;
        long prevVal = destination[1];

        for (int i = 2; i < destination.Length; i++)
        {
            long dod;
            int bit0 = reader.ReadBit();
            if (bit0 == 0)
            {
                dod = 0;
            }
            else
            {
                int bit1 = reader.ReadBit();
                if (bit1 == 0)
                {
                    // '10' -> 7 bits
                    dod = unchecked((long)reader.ReadBits(7) - 63);
                }
                else
                {
                    int bit2 = reader.ReadBit();
                    if (bit2 == 0)
                    {
                        // '110' -> 9 bits
                        dod = unchecked((long)reader.ReadBits(9) - 255);
                    }
                    else
                    {
                        int bit3 = reader.ReadBit();
                        if (bit3 == 0)
                        {
                            // '1110' -> 12 bits
                            dod = unchecked((long)reader.ReadBits(12) - 2047);
                        }
                        else
                        {
                            // '1111' -> 64 bits
                            dod = unchecked((long)reader.ReadBits(64));
                        }
                    }
                }
            }

            long currDelta;
            long currVal;
            unchecked
            {
                currDelta = prevDelta + dod;
                currVal = prevVal + currDelta;
            }
            destination[i] = currVal;

            prevDelta = currDelta;
            prevVal = currVal;
        }

        return destination.Length;
    }
}
