using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Glacier.Chrono.Compression;

/// <summary>
/// Mode of ALP compression applied to a block.
/// </summary>
public enum AlpCompressionMode : byte
{
    Raw = 0,
    AlpFloat = 1,
    AlpDouble = 2,
    AlpRdDouble = 3,
    Constant = 4
}

/// <summary>
/// Interface for ALP floating-point compression codecs.
/// </summary>
public interface IAlpCompressionCodec
{
    byte[] Compress(ReadOnlySpan<float> values, out int exceptionCount);
    float[] Decompress(ReadOnlySpan<byte> compressed, int originalCount);
    byte[] CompressDouble(ReadOnlySpan<double> values, out int exceptionCount);
    double[] DecompressDouble(ReadOnlySpan<byte> compressed, int originalCount);
}

/// <summary>
/// Hardware-accelerated, zero-allocation lossless Adaptive Lossless floating-Point (ALP)
/// and ALP_RD compression engine for single- and double-precision IEEE 754 floats.
/// </summary>
public static class AlpCompressor
{
    public const uint Magic = 0x414C5031; // 'ALP1' in ASCII
    public const int HeaderSize = 48;

    private static readonly double[] s_pow10Double =
    [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10,
        1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18
    ];

    private static readonly double[] s_invPow10Double =
    [
        1e-0, 1e-1, 1e-2, 1e-3, 1e-4, 1e-5, 1e-6, 1e-7, 1e-8, 1e-9, 1e-10,
        1e-11, 1e-12, 1e-13, 1e-14, 1e-15, 1e-16, 1e-17, 1e-18
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetMaxCompressedLength(int count, int elementSize)
    {
        return HeaderSize + (count * Math.Max(elementSize, 8) * 2) + (count * (2 + elementSize)) + 128;
    }

    // =========================================================================
    // FLOAT COMPRESSION
    // =========================================================================

    public static int CompressFloat(ReadOnlySpan<float> source, Span<byte> destination)
    {
        return CompressFloat(source, destination, out _);
    }

    public static int CompressFloat(ReadOnlySpan<float> source, Span<byte> destination, out int exceptionCount)
    {
        int count = source.Length;
        if (count == 0)
        {
            exceptionCount = 0;
            return 0;
        }

        if (count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Block element count must not exceed 65535.");
        }

        int maxLen = GetMaxCompressedLength(count, sizeof(float));
        if (destination.Length < maxLen)
        {
            throw new ArgumentException($"Destination buffer too small. Expected at least {maxLen} bytes.", nameof(destination));
        }

        // 1. Zone-map bounds and constant check
        float minVal = float.MaxValue;
        float maxVal = float.MinValue;
        bool allEqual = true;
        uint firstBits = BitConverter.SingleToUInt32Bits(source[0]);
        bool hasNonFinite = false;

        for (int i = 0; i < count; i++)
        {
            float v = source[i];
            uint bits = BitConverter.SingleToUInt32Bits(v);
            if (bits != firstBits) allEqual = false;

            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                hasNonFinite = true;
            }
            else
            {
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
            }
        }

        ulong zoneMapMinBits = hasNonFinite && minVal > maxVal
            ? (ulong)firstBits
            : (ulong)BitConverter.SingleToUInt32Bits(minVal <= maxVal ? minVal : source[0]);
        ulong zoneMapMaxBits = hasNonFinite && minVal > maxVal
            ? (ulong)firstBits
            : (ulong)BitConverter.SingleToUInt32Bits(maxVal >= minVal ? maxVal : source[0]);

        // Constant optimization: all bits equal (e.g. all 42.0f or all 0.0f)
        if (allEqual)
        {
            exceptionCount = 0;
            WriteHeader(destination, AlpCompressionMode.Constant, exponent: 0, bitWidth: 0,
                exponentRight: 0, bitWidthRight: 0, (ushort)count, exceptionCount: 0,
                minBase: 0, minBaseRight: 0, zoneMapMinBits: (ulong)firstBits, zoneMapMaxBits: (ulong)firstBits);
            return HeaderSize;
        }

        // 2. Decimal Exponent Search on sample
        int sampleSize = Math.Min(count, 128);
        int stride = Math.Max(1, count / sampleSize);
        int bestExp = 0;
        int bestScore = int.MaxValue;

        for (int e = 0; e <= 10; e++)
        {
            double factor = s_pow10Double[e];
            int sampleExceptions = 0;
            long sampleMin = long.MaxValue;
            long sampleMax = long.MinValue;

            for (int s = 0; s < count; s += stride)
            {
                float x = source[s];
                if (!float.IsFinite(x) || x == -0.0f)
                {
                    sampleExceptions++;
                    continue;
                }

                double scaled = (double)x * factor;
                if (scaled < -9e18 || scaled > 9e18)
                {
                    sampleExceptions++;
                    continue;
                }

                long v = (long)Math.Round(scaled);
                float reconstructed = (float)((double)v / factor);
                if (BitConverter.SingleToUInt32Bits(reconstructed) == BitConverter.SingleToUInt32Bits(x))
                {
                    if (v < sampleMin) sampleMin = v;
                    if (v > sampleMax) sampleMax = v;
                }
                else
                {
                    sampleExceptions++;
                }
            }

            ulong range = (sampleMin <= sampleMax) ? (ulong)(sampleMax - sampleMin) : 0UL;
            int bitWidth = range == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(range);
            int score = (count * bitWidth) + (sampleExceptions * (count / sampleSize) * 48);

            if (score < bestScore)
            {
                bestScore = score;
                bestExp = e;
            }
        }

        // 3. Full pass encoding
        long[] encoded = ArrayPool<long>.Shared.Rent(count);
        ushort[] exIndices = ArrayPool<ushort>.Shared.Rent(count);
        uint[] exBits = ArrayPool<uint>.Shared.Rent(count);

        try
        {
            int exCount = 0;
            long minBase = long.MaxValue;
            long maxBase = long.MinValue;
            double factor = s_pow10Double[bestExp];

            for (int i = 0; i < count; i++)
            {
                float x = source[i];
                uint rawBits = BitConverter.SingleToUInt32Bits(x);

                if (float.IsFinite(x) && BitConverter.SingleToUInt32Bits(x) != 0x80000000u) // preserve -0.0f as exception
                {
                    double scaled = (double)x * factor;
                    if (scaled >= -9e18 && scaled <= 9e18)
                    {
                        long v = (long)Math.Round(scaled);
                        float reconstructed = (float)((double)v / factor);
                        if (BitConverter.SingleToUInt32Bits(reconstructed) == rawBits)
                        {
                            encoded[i] = v;
                            if (v < minBase) minBase = v;
                            if (v > maxBase) maxBase = v;
                            continue;
                        }
                    }
                }

                // Exception
                encoded[i] = long.MinValue;
                exIndices[exCount] = (ushort)i;
                exBits[exCount] = rawBits;
                exCount++;
            }

            exceptionCount = exCount;

            if (minBase > maxBase) minBase = maxBase = 0;
            ulong baseRange = (ulong)(maxBase - minBase);
            int bitWidth = baseRange == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(baseRange);

            // Subtract base offset
            ulong[] unsignedValues = ArrayPool<ulong>.Shared.Rent(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    unsignedValues[i] = encoded[i] == long.MinValue ? 0UL : (ulong)(encoded[i] - minBase);
                }

                int packedBytes = (count * bitWidth + 7) / 8;
                Span<byte> packedDest = destination.Slice(HeaderSize, packedBytes);
                PackBits(unsignedValues.AsSpan(0, count), bitWidth, packedDest);

                int offset = HeaderSize + packedBytes;

                // Write exceptions dictionary
                for (int k = 0; k < exCount; k++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset, 2), exIndices[k]);
                    offset += 2;
                }
                for (int k = 0; k < exCount; k++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), exBits[k]);
                    offset += 4;
                }

                // Write Header
                WriteHeader(destination, AlpCompressionMode.AlpFloat, (byte)bestExp, (byte)bitWidth,
                    exponentRight: 0, bitWidthRight: 0, (ushort)count, (ushort)exCount,
                    minBase, minBaseRight: 0, zoneMapMinBits, zoneMapMaxBits);

                return offset;
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(unsignedValues);
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(encoded);
            ArrayPool<ushort>.Shared.Return(exIndices);
            ArrayPool<uint>.Shared.Return(exBits);
        }
    }

    public static int DecompressFloat(ReadOnlySpan<byte> source, Span<float> destination)
    {
        if (source.IsEmpty) return 0;
        if (source.Length < HeaderSize)
            throw new InvalidDataException("Source buffer is too small for an ALP header.");

        ReadHeader(source, out var mode, out byte exp, out byte bitWidth, out _, out _,
            out ushort count, out ushort exCount, out long minBase, out _, out ulong minBits, out _);

        if (destination.Length < count)
            throw new ArgumentException($"Destination span too small. Required: {count}, Actual: {destination.Length}", nameof(destination));

        if (mode == AlpCompressionMode.Constant)
        {
            float constVal = BitConverter.UInt32BitsToSingle((uint)minBits);
            destination.Slice(0, count).Fill(constVal);
            return count;
        }

        if (mode != AlpCompressionMode.AlpFloat)
            throw new InvalidDataException($"Expected AlpFloat compression mode, but got {mode}.");

        int packedBytes = (count * bitWidth + 7) / 8;
        int expectedTotal = HeaderSize + packedBytes + (exCount * 2) + (exCount * 4);
        if (source.Length < expectedTotal)
            throw new InvalidDataException($"ALP stream truncated. Expected {expectedTotal} bytes, got {source.Length}.");

        ulong[] unpacked = ArrayPool<ulong>.Shared.Rent(count);
        try
        {
            ReadOnlySpan<byte> packedSlice = source.Slice(HeaderSize, packedBytes);
            UnpackBits(packedSlice, bitWidth, count, unpacked.AsSpan(0, count));

            double factor = s_pow10Double[exp];
            for (int i = 0; i < count; i++)
            {
                long v = (long)unpacked[i] + minBase;
                destination[i] = (float)((double)v / factor);
            }

            // Patch exceptions
            int exOffset = HeaderSize + packedBytes;
            int exValOffset = exOffset + (exCount * 2);

            for (int k = 0; k < exCount; k++)
            {
                ushort idx = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(exOffset + (k * 2), 2));
                uint rawBits = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(exValOffset + (k * 4), 4));
                destination[idx] = BitConverter.UInt32BitsToSingle(rawBits);
            }

            return count;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(unpacked);
        }
    }

    // =========================================================================
    // DOUBLE COMPRESSION (ALP & ALP_RD)
    // =========================================================================

    public static int CompressDouble(ReadOnlySpan<double> source, Span<byte> destination)
    {
        return CompressDouble(source, destination, out _);
    }

    public static int CompressDouble(ReadOnlySpan<double> source, Span<byte> destination, out int exceptionCount)
    {
        int count = source.Length;
        if (count == 0)
        {
            exceptionCount = 0;
            return 0;
        }

        if (count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Block element count must not exceed 65535.");
        }

        int maxLen = GetMaxCompressedLength(count, sizeof(double));
        if (destination.Length < maxLen)
        {
            throw new ArgumentException($"Destination buffer too small. Expected at least {maxLen} bytes.", nameof(destination));
        }

        // 1. Zone-map bounds and constant check
        double minVal = double.MaxValue;
        double maxVal = double.MinValue;
        bool allEqual = true;
        ulong firstBits = BitConverter.DoubleToUInt64Bits(source[0]);
        bool hasNonFinite = false;

        for (int i = 0; i < count; i++)
        {
            double v = source[i];
            ulong bits = BitConverter.DoubleToUInt64Bits(v);
            if (bits != firstBits) allEqual = false;

            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                hasNonFinite = true;
            }
            else
            {
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
            }
        }

        ulong zoneMapMinBits = hasNonFinite && minVal > maxVal
            ? firstBits
            : BitConverter.DoubleToUInt64Bits(minVal <= maxVal ? minVal : source[0]);
        ulong zoneMapMaxBits = hasNonFinite && minVal > maxVal
            ? firstBits
            : BitConverter.DoubleToUInt64Bits(maxVal >= minVal ? maxVal : source[0]);

        if (allEqual)
        {
            exceptionCount = 0;
            WriteHeader(destination, AlpCompressionMode.Constant, exponent: 0, bitWidth: 0,
                exponentRight: 0, bitWidthRight: 0, (ushort)count, exceptionCount: 0,
                minBase: 0, minBaseRight: 0, zoneMapMinBits: firstBits, zoneMapMaxBits: firstBits);
            return HeaderSize;
        }

        // 2. Sample evaluation for Standard ALP vs ALP_RD
        int sampleSize = Math.Min(count, 128);
        int stride = Math.Max(1, count / sampleSize);

        int bestStandardExp = 0;
        int bestStandardScore = int.MaxValue;

        for (int e = 0; e <= 18; e++)
        {
            double factor = s_pow10Double[e];
            int ex = 0;
            long sMin = long.MaxValue;
            long sMax = long.MinValue;

            for (int s = 0; s < count; s += stride)
            {
                double x = source[s];
                if (!double.IsFinite(x) || x == -0.0)
                {
                    ex++;
                    continue;
                }

                double scaled = x * factor;
                if (scaled < -9e18 || scaled > 9e18)
                {
                    ex++;
                    continue;
                }

                long v = (long)Math.Round(scaled);
                double reconstructed = (double)v / factor;
                if (BitConverter.DoubleToUInt64Bits(reconstructed) == BitConverter.DoubleToUInt64Bits(x))
                {
                    if (v < sMin) sMin = v;
                    if (v > sMax) sMax = v;
                }
                else
                {
                    ex++;
                }
            }

            ulong range = (sMin <= sMax) ? (ulong)(sMax - sMin) : 0UL;
            int bitWidth = range == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(range);
            int score = (count * bitWidth) + (ex * (count / sampleSize) * 80);

            if (score < bestStandardScore)
            {
                bestStandardScore = score;
                bestStandardExp = e;
            }
        }

        // Test ALP_RD candidate on sample
        int bestRdExp = 0;
        int bestRdScore = int.MaxValue;

        for (int eR = 1; eR <= 10; eR++)
        {
            double factorR = s_pow10Double[eR];
            int ex = 0;
            long sMinL = long.MaxValue;
            long sMaxL = long.MinValue;
            long sMinR = long.MaxValue;
            long sMaxR = long.MinValue;

            for (int s = 0; s < count; s += stride)
            {
                double x = source[s];
                if (!double.IsFinite(x) || x == -0.0 || x < -1e9 || x > 1e9)
                {
                    ex++;
                    continue;
                }

                long left = (long)Math.Floor(x);
                double frac = x - left;
                long right = (long)Math.Round(frac * factorR);

                double reconstructed = (double)left + ((double)right / factorR);
                if (BitConverter.DoubleToUInt64Bits(reconstructed) == BitConverter.DoubleToUInt64Bits(x))
                {
                    if (left < sMinL) sMinL = left;
                    if (left > sMaxL) sMaxL = left;
                    if (right < sMinR) sMinR = right;
                    if (right > sMaxR) sMaxR = right;
                }
                else
                {
                    ex++;
                }
            }

            ulong rangeL = (sMinL <= sMaxL) ? (ulong)(sMaxL - sMinL) : 0UL;
            ulong rangeR = (sMinR <= sMaxR) ? (ulong)(sMaxR - sMinR) : 0UL;
            int bwL = rangeL == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(rangeL);
            int bwR = rangeR == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(rangeR);
            int score = (count * (bwL + bwR)) + (ex * (count / sampleSize) * 80);

            if (score < bestRdScore)
            {
                bestRdScore = score;
                bestRdExp = eR;
            }
        }

        // Choose between Standard ALP and ALP_RD
        bool useAlpRd = bestRdScore < bestStandardScore && bestRdScore < (count * 64);

        if (!useAlpRd)
        {
            // Execute Standard ALP Double
            return CompressDoubleStandard(source, destination, bestStandardExp, zoneMapMinBits, zoneMapMaxBits, out exceptionCount);
        }
        else
        {
            // Execute ALP_RD Double
            return CompressDoubleAlpRd(source, destination, bestRdExp, zoneMapMinBits, zoneMapMaxBits, out exceptionCount);
        }
    }

    private static int CompressDoubleStandard(
        ReadOnlySpan<double> source,
        Span<byte> destination,
        int exp,
        ulong zoneMapMinBits,
        ulong zoneMapMaxBits,
        out int exceptionCount)
    {
        int count = source.Length;
        long[] encoded = ArrayPool<long>.Shared.Rent(count);
        ushort[] exIndices = ArrayPool<ushort>.Shared.Rent(count);
        ulong[] exBits = ArrayPool<ulong>.Shared.Rent(count);

        try
        {
            int exCount = 0;
            long minBase = long.MaxValue;
            long maxBase = long.MinValue;
            double factor = s_pow10Double[exp];

            for (int i = 0; i < count; i++)
            {
                double x = source[i];
                ulong rawBits = BitConverter.DoubleToUInt64Bits(x);

                if (double.IsFinite(x) && rawBits != 0x8000000000000000u)
                {
                    double scaled = x * factor;
                    if (scaled >= -9e18 && scaled <= 9e18)
                    {
                        long v = (long)Math.Round(scaled);
                        double reconstructed = (double)v / factor;
                        if (BitConverter.DoubleToUInt64Bits(reconstructed) == rawBits)
                        {
                            encoded[i] = v;
                            if (v < minBase) minBase = v;
                            if (v > maxBase) maxBase = v;
                            continue;
                        }
                    }
                }

                // Exception
                encoded[i] = long.MinValue;
                exIndices[exCount] = (ushort)i;
                exBits[exCount] = rawBits;
                exCount++;
            }

            exceptionCount = exCount;
            if (minBase > maxBase) minBase = maxBase = 0;
            ulong baseRange = (ulong)(maxBase - minBase);
            int bitWidth = baseRange == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(baseRange);

            ulong[] unsignedValues = ArrayPool<ulong>.Shared.Rent(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    unsignedValues[i] = encoded[i] == long.MinValue ? 0UL : (ulong)(encoded[i] - minBase);
                }

                int packedBytes = (count * bitWidth + 7) / 8;
                Span<byte> packedDest = destination.Slice(HeaderSize, packedBytes);
                PackBits(unsignedValues.AsSpan(0, count), bitWidth, packedDest);

                int offset = HeaderSize + packedBytes;

                // Write exceptions dictionary
                for (int k = 0; k < exCount; k++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset, 2), exIndices[k]);
                    offset += 2;
                }
                for (int k = 0; k < exCount; k++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, 8), exBits[k]);
                    offset += 8;
                }

                WriteHeader(destination, AlpCompressionMode.AlpDouble, (byte)exp, (byte)bitWidth,
                    exponentRight: 0, bitWidthRight: 0, (ushort)count, (ushort)exCount,
                    minBase, minBaseRight: 0, zoneMapMinBits, zoneMapMaxBits);

                return offset;
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(unsignedValues);
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(encoded);
            ArrayPool<ushort>.Shared.Return(exIndices);
            ArrayPool<ulong>.Shared.Return(exBits);
        }
    }

    private static int CompressDoubleAlpRd(
        ReadOnlySpan<double> source,
        Span<byte> destination,
        int expRight,
        ulong zoneMapMinBits,
        ulong zoneMapMaxBits,
        out int exceptionCount)
    {
        int count = source.Length;
        long[] leftEncoded = ArrayPool<long>.Shared.Rent(count);
        long[] rightEncoded = ArrayPool<long>.Shared.Rent(count);
        ushort[] exIndices = ArrayPool<ushort>.Shared.Rent(count);
        ulong[] exBits = ArrayPool<ulong>.Shared.Rent(count);

        try
        {
            int exCount = 0;
            long minBaseL = long.MaxValue;
            long maxBaseL = long.MinValue;
            long minBaseR = long.MaxValue;
            long maxBaseR = long.MinValue;
            double factorR = s_pow10Double[expRight];

            for (int i = 0; i < count; i++)
            {
                double x = source[i];
                ulong rawBits = BitConverter.DoubleToUInt64Bits(x);

                if (double.IsFinite(x) && rawBits != 0x8000000000000000u && x >= -1e9 && x <= 1e9)
                {
                    long left = (long)Math.Floor(x);
                    double frac = x - left;
                    long right = (long)Math.Round(frac * factorR);

                    double reconstructed = (double)left + ((double)right / factorR);
                    if (BitConverter.DoubleToUInt64Bits(reconstructed) == rawBits)
                    {
                        leftEncoded[i] = left;
                        rightEncoded[i] = right;
                        if (left < minBaseL) minBaseL = left;
                        if (left > maxBaseL) maxBaseL = left;
                        if (right < minBaseR) minBaseR = right;
                        if (right > maxBaseR) maxBaseR = right;
                        continue;
                    }
                }

                // Exception
                leftEncoded[i] = long.MinValue;
                rightEncoded[i] = long.MinValue;
                exIndices[exCount] = (ushort)i;
                exBits[exCount] = rawBits;
                exCount++;
            }

            exceptionCount = exCount;
            if (minBaseL > maxBaseL) minBaseL = maxBaseL = 0;
            if (minBaseR > maxBaseR) minBaseR = maxBaseR = 0;

            ulong rangeL = (ulong)(maxBaseL - minBaseL);
            ulong rangeR = (ulong)(maxBaseR - minBaseR);
            int bitWidthL = rangeL == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(rangeL);
            int bitWidthR = rangeR == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(rangeR);

            ulong[] unsignedL = ArrayPool<ulong>.Shared.Rent(count);
            ulong[] unsignedR = ArrayPool<ulong>.Shared.Rent(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    unsignedL[i] = leftEncoded[i] == long.MinValue ? 0UL : (ulong)(leftEncoded[i] - minBaseL);
                    unsignedR[i] = rightEncoded[i] == long.MinValue ? 0UL : (ulong)(rightEncoded[i] - minBaseR);
                }

                int packedBytesL = (count * bitWidthL + 7) / 8;
                int packedBytesR = (count * bitWidthR + 7) / 8;

                PackBits(unsignedL.AsSpan(0, count), bitWidthL, destination.Slice(HeaderSize, packedBytesL));
                PackBits(unsignedR.AsSpan(0, count), bitWidthR, destination.Slice(HeaderSize + packedBytesL, packedBytesR));

                int offset = HeaderSize + packedBytesL + packedBytesR;

                for (int k = 0; k < exCount; k++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset, 2), exIndices[k]);
                    offset += 2;
                }
                for (int k = 0; k < exCount; k++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, 8), exBits[k]);
                    offset += 8;
                }

                WriteHeader(destination, AlpCompressionMode.AlpRdDouble, exponent: 0, (byte)bitWidthL,
                    (byte)expRight, (byte)bitWidthR, (ushort)count, (ushort)exCount,
                    minBaseL, minBaseR, zoneMapMinBits, zoneMapMaxBits);

                return offset;
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(unsignedL);
                ArrayPool<ulong>.Shared.Return(unsignedR);
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(leftEncoded);
            ArrayPool<long>.Shared.Return(rightEncoded);
            ArrayPool<ushort>.Shared.Return(exIndices);
            ArrayPool<ulong>.Shared.Return(exBits);
        }
    }

    public static int DecompressDouble(ReadOnlySpan<byte> source, Span<double> destination)
    {
        if (source.IsEmpty) return 0;
        if (source.Length < HeaderSize)
            throw new InvalidDataException("Source buffer is too small for an ALP header.");

        ReadHeader(source, out var mode, out byte exp, out byte bitWidth, out byte expR, out byte bitWidthR,
            out ushort count, out ushort exCount, out long minBase, out long minBaseR, out ulong minBits, out _);

        if (destination.Length < count)
            throw new ArgumentException($"Destination span too small. Required: {count}, Actual: {destination.Length}", nameof(destination));

        if (mode == AlpCompressionMode.Constant)
        {
            double constVal = BitConverter.UInt64BitsToDouble(minBits);
            destination.Slice(0, count).Fill(constVal);
            return count;
        }

        if (mode == AlpCompressionMode.AlpDouble)
        {
            int packedBytes = (count * bitWidth + 7) / 8;
            int expectedTotal = HeaderSize + packedBytes + (exCount * 2) + (exCount * 8);
            if (source.Length < expectedTotal)
                throw new InvalidDataException($"ALP stream truncated. Expected {expectedTotal} bytes, got {source.Length}.");

            ulong[] unpacked = ArrayPool<ulong>.Shared.Rent(count);
            try
            {
                ReadOnlySpan<byte> packedSlice = source.Slice(HeaderSize, packedBytes);
                UnpackBits(packedSlice, bitWidth, count, unpacked.AsSpan(0, count));

                double factor = s_pow10Double[exp];
                for (int i = 0; i < count; i++)
                {
                    long v = (long)unpacked[i] + minBase;
                    destination[i] = (double)v / factor;
                }

                int exOffset = HeaderSize + packedBytes;
                int exValOffset = exOffset + (exCount * 2);

                for (int k = 0; k < exCount; k++)
                {
                    ushort idx = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(exOffset + (k * 2), 2));
                    ulong rawBits = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(exValOffset + (k * 8), 8));
                    destination[idx] = BitConverter.UInt64BitsToDouble(rawBits);
                }

                return count;
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(unpacked);
            }
        }
        else if (mode == AlpCompressionMode.AlpRdDouble)
        {
            int packedBytesL = (count * bitWidth + 7) / 8;
            int packedBytesR = (count * bitWidthR + 7) / 8;
            int expectedTotal = HeaderSize + packedBytesL + packedBytesR + (exCount * 2) + (exCount * 8);
            if (source.Length < expectedTotal)
                throw new InvalidDataException($"ALP_RD stream truncated. Expected {expectedTotal} bytes, got {source.Length}.");

            ulong[] unpackedL = ArrayPool<ulong>.Shared.Rent(count);
            ulong[] unpackedR = ArrayPool<ulong>.Shared.Rent(count);
            try
            {
                ReadOnlySpan<byte> packedSliceL = source.Slice(HeaderSize, packedBytesL);
                ReadOnlySpan<byte> packedSliceR = source.Slice(HeaderSize + packedBytesL, packedBytesR);

                UnpackBits(packedSliceL, bitWidth, count, unpackedL.AsSpan(0, count));
                UnpackBits(packedSliceR, bitWidthR, count, unpackedR.AsSpan(0, count));

                double factorR = s_pow10Double[expR];
                for (int i = 0; i < count; i++)
                {
                    long left = (long)unpackedL[i] + minBase;
                    long right = (long)unpackedR[i] + minBaseR;
                    destination[i] = (double)left + ((double)right / factorR);
                }

                int exOffset = HeaderSize + packedBytesL + packedBytesR;
                int exValOffset = exOffset + (exCount * 2);

                for (int k = 0; k < exCount; k++)
                {
                    ushort idx = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(exOffset + (k * 2), 2));
                    ulong rawBits = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(exValOffset + (k * 8), 8));
                    destination[idx] = BitConverter.UInt64BitsToDouble(rawBits);
                }

                return count;
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(unpackedL);
                ArrayPool<ulong>.Shared.Return(unpackedR);
            }
        }
        else
        {
            throw new InvalidDataException($"Unsupported ALP compression mode: {mode}");
        }
    }

    // =========================================================================
    // HEADER SERIALIZATION (48 BYTES)
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHeader(
        Span<byte> destination,
        AlpCompressionMode mode,
        byte exponent,
        byte bitWidth,
        byte exponentRight,
        byte bitWidthRight,
        ushort count,
        ushort exceptionCount,
        long minBase,
        long minBaseRight,
        ulong zoneMapMinBits,
        ulong zoneMapMaxBits)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0x00, 4), Magic);
        destination[0x04] = (byte)mode;
        destination[0x05] = exponent;
        destination[0x06] = bitWidth;
        destination[0x07] = exponentRight;
        destination[0x08] = bitWidthRight;
        destination[0x09] = 0; // Reserved
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(0x0A, 2), count);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(0x0C, 2), exceptionCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(0x0E, 2), 0); // Reserved2
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(0x10, 8), minBase);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(0x18, 8), minBaseRight);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(0x20, 8), zoneMapMinBits);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(0x28, 8), zoneMapMaxBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadHeader(
        ReadOnlySpan<byte> source,
        out AlpCompressionMode mode,
        out byte exponent,
        out byte bitWidth,
        out byte exponentRight,
        out byte bitWidthRight,
        out ushort count,
        out ushort exceptionCount,
        out long minBase,
        out long minBaseRight,
        out ulong zoneMapMinBits,
        out ulong zoneMapMaxBits)
    {
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(0x00, 4));
        if (magic != Magic)
        {
            throw new InvalidDataException($"Invalid ALP magic bytes: 0x{magic:X8}. Expected 0x{Magic:X8}.");
        }

        mode = (AlpCompressionMode)source[0x04];
        exponent = source[0x05];
        bitWidth = source[0x06];
        exponentRight = source[0x07];
        bitWidthRight = source[0x08];
        count = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(0x0A, 2));
        exceptionCount = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(0x0C, 2));
        minBase = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(0x10, 8));
        minBaseRight = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(0x18, 8));
        zoneMapMinBits = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(0x20, 8));
        zoneMapMaxBits = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(0x28, 8));
    }

    // =========================================================================
    // BIT-PACKING ENGINE
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void PackBits(ReadOnlySpan<ulong> values, int bitWidth, Span<byte> destination)
    {
        if (bitWidth == 0 || values.IsEmpty) return;
        int requiredBytes = (values.Length * bitWidth + 7) / 8;
        destination.Slice(0, requiredBytes).Clear();

        ulong mask = bitWidth == 64 ? ulong.MaxValue : ((1UL << bitWidth) - 1UL);
        int bitOffset = 0;
        for (int i = 0; i < values.Length; i++)
        {
            ulong val = values[i] & mask;
            int byteStart = bitOffset >> 3;
            int bitInByte = bitOffset & 7;

            ulong shifted = val << bitInByte;
            int available = destination.Length - byteStart;
            for (int b = 0; b < 8 && b < available; b++)
            {
                destination[byteStart + b] |= (byte)((shifted >> (b * 8)) & 0xFF);
            }

            if (bitInByte + bitWidth > 64 && (byteStart + 8) < destination.Length)
            {
                ulong high = val >> (64 - bitInByte);
                destination[byteStart + 8] |= (byte)(high & 0xFF);
            }

            bitOffset += bitWidth;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void UnpackBits(ReadOnlySpan<byte> packed, int bitWidth, int count, Span<ulong> destination)
    {
        if (bitWidth == 0)
        {
            destination.Slice(0, count).Clear();
            return;
        }

        ulong mask = bitWidth == 64 ? ulong.MaxValue : ((1UL << bitWidth) - 1UL);
        int bitOffset = 0;

        for (int i = 0; i < count; i++)
        {
            int byteStart = bitOffset >> 3;
            int bitInByte = bitOffset & 7;

            ulong accumulator = 0;
            int bytesLeft = packed.Length - byteStart;
            for (int b = 0; b < 8 && b < bytesLeft; b++)
            {
                accumulator |= ((ulong)packed[byteStart + b]) << (b * 8);
            }

            ulong val = (accumulator >> bitInByte) & mask;
            if (bitInByte + bitWidth > 64 && (byteStart + 8) < packed.Length)
            {
                ulong high = (ulong)packed[byteStart + 8];
                val |= (high << (64 - bitInByte)) & mask;
            }

            destination[i] = val;
            bitOffset += bitWidth;
        }
    }
}

/// <summary>
/// Default implementation of <see cref="IAlpCompressionCodec"/> backed by <see cref="AlpCompressor"/>.
/// </summary>
public sealed class AlpCompressionCodec : IAlpCompressionCodec
{
    public byte[] Compress(ReadOnlySpan<float> values, out int exceptionCount)
    {
        int maxLen = AlpCompressor.GetMaxCompressedLength(values.Length, sizeof(float));
        byte[] buffer = new byte[maxLen];
        int written = AlpCompressor.CompressFloat(values, buffer, out exceptionCount);
        if (written == buffer.Length) return buffer;
        byte[] result = new byte[written];
        Array.Copy(buffer, result, written);
        return result;
    }

    public float[] Decompress(ReadOnlySpan<byte> compressed, int originalCount)
    {
        float[] result = new float[originalCount];
        AlpCompressor.DecompressFloat(compressed, result);
        return result;
    }

    public byte[] CompressDouble(ReadOnlySpan<double> values, out int exceptionCount)
    {
        int maxLen = AlpCompressor.GetMaxCompressedLength(values.Length, sizeof(double));
        byte[] buffer = new byte[maxLen];
        int written = AlpCompressor.CompressDouble(values, buffer, out exceptionCount);
        if (written == buffer.Length) return buffer;
        byte[] result = new byte[written];
        Array.Copy(buffer, result, written);
        return result;
    }

    public double[] DecompressDouble(ReadOnlySpan<byte> compressed, int originalCount)
    {
        double[] result = new double[originalCount];
        AlpCompressor.DecompressDouble(compressed, result);
        return result;
    }
}
