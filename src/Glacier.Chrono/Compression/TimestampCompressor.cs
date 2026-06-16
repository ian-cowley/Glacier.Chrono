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

        // Store first timestamp in full (64 bits, split into two 32-bit writes to prevent accumulator overflow)
        ulong t0 = (ulong)values[0];
        writer.WriteBits(t0 >> 32, 32);
        writer.WriteBits(t0 & 0xFFFFFFFF, 32);

        if (values.Length == 1)
        {
            writer.Flush();
            return writer.BytesWritten;
        }

        // Store first delta in full (64 bits, split into two 32-bit writes)
        long firstDelta = values[1] - values[0];
        ulong d0 = (ulong)firstDelta;
        writer.WriteBits(d0 >> 32, 32);
        writer.WriteBits(d0 & 0xFFFFFFFF, 32);

        long prevDelta = firstDelta;
        long prevVal = values[1];

        for (int i = 2; i < values.Length; i++)
        {
            long currVal = values[i];
            long currDelta = currVal - prevVal;
            long dod = currDelta - prevDelta;

            if (dod == 0)
            {
                writer.WriteBit(0);
            }
            else if (dod >= -63 && dod <= 64)
            {
                // Write '10' (2 bits), then write dod + 63 (7 bits)
                writer.WriteBit(1);
                writer.WriteBit(0);
                writer.WriteBits((ulong)(dod + 63), 7);
            }
            else if (dod >= -255 && dod <= 256)
            {
                // Write '110' (3 bits), then write dod + 255 (9 bits)
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBit(0);
                writer.WriteBits((ulong)(dod + 255), 9);
            }
            else if (dod >= -2047 && dod <= 2048)
            {
                // Write '1110' (4 bits), then write dod + 2047 (12 bits)
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBit(0);
                writer.WriteBits((ulong)(dod + 2047), 12);
            }
            else
            {
                // Write '1111' (4 bits), then write raw dod (64 bits, split into two 32-bit writes)
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBit(1);
                writer.WriteBit(1);
                
                ulong dodVal = (ulong)dod;
                writer.WriteBits(dodVal >> 32, 32);
                writer.WriteBits(dodVal & 0xFFFFFFFF, 32);
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

        // Read first timestamp (64 bits, read as two 32-bit parts)
        long firstVal = ((long)reader.ReadBits(32) << 32) | (long)reader.ReadBits(32);
        destination[0] = firstVal;

        if (destination.Length == 1)
        {
            return 1;
        }

        // Read first delta (64 bits, read as two 32-bit parts)
        long firstDelta = ((long)reader.ReadBits(32) << 32) | (long)reader.ReadBits(32);
        destination[1] = firstVal + firstDelta;

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
                    dod = (long)reader.ReadBits(7) - 63;
                }
                else
                {
                    int bit2 = reader.ReadBit();
                    if (bit2 == 0)
                    {
                        // '110' -> 9 bits
                        dod = (long)reader.ReadBits(9) - 255;
                    }
                    else
                    {
                        int bit3 = reader.ReadBit();
                        if (bit3 == 0)
                        {
                            // '1110' -> 12 bits
                            dod = (long)reader.ReadBits(12) - 2047;
                        }
                        else
                        {
                            // '1111' -> 64 bits (read as two 32-bit parts)
                            dod = ((long)reader.ReadBits(32) << 32) | (long)reader.ReadBits(32);
                        }
                    }
                }
            }

            long currDelta = prevDelta + dod;
            long currVal = prevVal + currDelta;
            destination[i] = currVal;

            prevDelta = currDelta;
            prevVal = currVal;
        }

        return destination.Length;
    }
}
