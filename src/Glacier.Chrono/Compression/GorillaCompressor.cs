using System;
using System.Numerics;

namespace Glacier.Chrono.Compression;

public static class GorillaCompressor
{
    /// <summary>
    /// Compresses a span of 32-bit floating point values into a byte buffer using the Gorilla XOR algorithm.
    /// Returns the number of bytes written to the destination buffer.
    /// </summary>
    public static int Compress(ReadOnlySpan<float> values, Span<byte> destination)
    {
        if (values.IsEmpty) return 0;

        var writer = new BitWriter(destination);

        // Store the first value in full (32 bits)
        uint firstVal = BitConverter.SingleToUInt32Bits(values[0]);
        writer.WriteBits(firstVal, 32);

        if (values.Length == 1)
        {
            return writer.BytesWritten;
        }

        uint prevVal = firstVal;
        int prevLz = 32;
        int prevTz = 32;
        bool isFirstXor = true;

        for (int i = 1; i < values.Length; i++)
        {
            uint currVal = BitConverter.SingleToUInt32Bits(values[i]);
            uint xor = currVal ^ prevVal;

            if (xor == 0)
            {
                writer.WriteBit(0);
            }
            else
            {
                writer.WriteBit(1);

                int lz = BitOperations.LeadingZeroCount(xor);
                int tz = BitOperations.TrailingZeroCount(xor);

                // For 32-bit floats, leading zero count is at most 31 since xor != 0
                if (lz > 31) lz = 31;

                if (!isFirstXor && lz >= prevLz && tz >= prevTz)
                {
                    // Case A: write control bit '0', then reuse previous leading/trailing zero counts
                    writer.WriteBit(0);
                    int prevLen = 32 - prevLz - prevTz;
                    uint meaningfulBits = xor >> prevTz;
                    writer.WriteBits(meaningfulBits, prevLen);
                }
                else
                {
                    // Case B: write control bit '1', then 5 bits of leading zeros,
                    // 6 bits of length of meaningful bits, then meaningful bits of xor
                    writer.WriteBit(1);
                    writer.WriteBits((uint)lz, 5);

                    int len = 32 - lz - tz;
                    writer.WriteBits((uint)len, 6);

                    uint meaningfulBits = xor >> tz;
                    writer.WriteBits(meaningfulBits, len);

                    prevLz = lz;
                    prevTz = tz;
                    isFirstXor = false;
                }
            }

            prevVal = currVal;
        }

        writer.Flush();
        return writer.BytesWritten;
    }

    /// <summary>
    /// Decompresses a buffer of bytes into a span of 32-bit floating point values using the Gorilla XOR algorithm.
    /// Returns the number of float values successfully decompressed.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> source, Span<float> destination)
    {
        if (destination.IsEmpty) return 0;

        var reader = new BitReader(source);

        // Read the first value in full (32 bits)
        uint firstVal = (uint)reader.ReadBits(32);
        destination[0] = BitConverter.UInt32BitsToSingle(firstVal);

        if (destination.Length == 1)
        {
            return 1;
        }

        uint prevVal = firstVal;
        int prevLz = 0;
        int prevTz = 0;

        for (int i = 1; i < destination.Length; i++)
        {
            int controlBit = reader.ReadBit();
            if (controlBit == 0)
            {
                destination[i] = BitConverter.UInt32BitsToSingle(prevVal);
            }
            else
            {
                int caseBit = reader.ReadBit();
                uint currVal;
                if (caseBit == 0)
                {
                    // Case A: Use previous leading/trailing zero counts
                    int prevLen = 32 - prevLz - prevTz;
                    uint bits = (uint)reader.ReadBits(prevLen);
                    uint xor = bits << prevTz;
                    currVal = prevVal ^ xor;
                }
                else
                {
                    // Case B: Read new leading zeros and length
                    int lz = (int)reader.ReadBits(5);
                    int len = (int)reader.ReadBits(6);
                    uint bits = (uint)reader.ReadBits(len);
                    int tz = 32 - lz - len;
                    uint xor = bits << tz;
                    currVal = prevVal ^ xor;
                    
                    prevLz = lz;
                    prevTz = tz;
                }

                destination[i] = BitConverter.UInt32BitsToSingle(currVal);
                prevVal = currVal;
            }
        }

        return destination.Length;
    }
}
