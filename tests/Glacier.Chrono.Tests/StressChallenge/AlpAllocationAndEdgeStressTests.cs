using System;
using System.Buffers.Binary;
using System.IO;
using Glacier.Chrono.Compression;
using Xunit;

namespace Glacier.Chrono.Tests.StressChallenge;

public class AlpAllocationAndEdgeStressTests
{
    [Fact]
    public void ZeroAllocation_SteadyState_FloatAndDouble()
    {
        float[] floats = new float[256];
        for (int i = 0; i < floats.Length; i++)
        {
            floats[i] = (float)Math.Round(20.0 + (i * 0.05), 2);
        }

        byte[] destFloat = new byte[AlpCompressor.GetMaxCompressedLength(floats.Length, sizeof(float))];
        float[] restoredFloats = new float[floats.Length];

        // Warm up array pools and JIT
        for (int w = 0; w < 5; w++)
        {
            int len = AlpCompressor.CompressFloat(floats, destFloat);
            AlpCompressor.DecompressFloat(destFloat.AsSpan(0, len), restoredFloats);
        }

        // Measure steady-state allocation for float
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        int writtenF = AlpCompressor.CompressFloat(floats, destFloat);
        AlpCompressor.DecompressFloat(destFloat.AsSpan(0, writtenF), restoredFloats);
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);

        // Double precision
        double[] doubles = new double[256];
        for (int i = 0; i < doubles.Length; i++)
        {
            doubles[i] = Math.Round(100.0 + (i * 0.025), 3);
        }

        byte[] destDouble = new byte[AlpCompressor.GetMaxCompressedLength(doubles.Length, sizeof(double))];
        double[] restoredDoubles = new double[doubles.Length];

        // Warm up
        for (int w = 0; w < 5; w++)
        {
            int len = AlpCompressor.CompressDouble(doubles, destDouble);
            AlpCompressor.DecompressDouble(destDouble.AsSpan(0, len), restoredDoubles);
        }

        allocBefore = GC.GetAllocatedBytesForCurrentThread();
        int writtenD = AlpCompressor.CompressDouble(doubles, destDouble);
        AlpCompressor.DecompressDouble(destDouble.AsSpan(0, writtenD), restoredDoubles);
        allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void AllNaNs_DiversePayloads_FloatAndDouble_BitExact()
    {
        // Construct 64 NaNs where EVERY NaN has a distinct payload
        float[] nanFloats = new float[64];
        for (int i = 0; i < nanFloats.Length; i++)
        {
            uint payloadBits = 0x7F800001u + (uint)i; // signaling and quiet NaNs
            nanFloats[i] = BitConverter.UInt32BitsToSingle(payloadBits);
        }

        byte[] bufF = new byte[AlpCompressor.GetMaxCompressedLength(nanFloats.Length, sizeof(float))];
        int writtenF = AlpCompressor.CompressFloat(nanFloats, bufF, out int exF);

        Assert.Equal(nanFloats.Length, exF); // All 64 are non-finite exceptions

        float[] restoredFloats = new float[nanFloats.Length];
        AlpCompressor.DecompressFloat(bufF.AsSpan(0, writtenF), restoredFloats);

        for (int i = 0; i < nanFloats.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(nanFloats[i]),
                BitConverter.SingleToUInt32Bits(restoredFloats[i]));
        }

        // Double precision
        double[] nanDoubles = new double[64];
        for (int i = 0; i < nanDoubles.Length; i++)
        {
            ulong payloadBits = 0x7FF0000000000001UL + (ulong)i;
            nanDoubles[i] = BitConverter.UInt64BitsToDouble(payloadBits);
        }

        byte[] bufD = new byte[AlpCompressor.GetMaxCompressedLength(nanDoubles.Length, sizeof(double))];
        int writtenD = AlpCompressor.CompressDouble(nanDoubles, bufD, out int exD);

        Assert.Equal(nanDoubles.Length, exD);

        double[] restoredDoubles = new double[nanDoubles.Length];
        AlpCompressor.DecompressDouble(bufD.AsSpan(0, writtenD), restoredDoubles);

        for (int i = 0; i < nanDoubles.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(nanDoubles[i]),
                BitConverter.DoubleToUInt64Bits(restoredDoubles[i]));
        }
    }

    [Fact]
    public void HeaderCorruptMode_ThrowsInvalidDataException()
    {
        byte[] corruptedMode = new byte[AlpCompressor.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(corruptedMode.AsSpan(0, 4), AlpCompressor.Magic);
        corruptedMode[4] = 99; // Invalid mode
        BinaryPrimitives.WriteUInt16LittleEndian(corruptedMode.AsSpan(0x0A, 2), 10);

        float[] destF = new float[10];
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(corruptedMode, destF));

        double[] destD = new double[10];
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressDouble(corruptedMode, destD));
    }

    [Fact]
    public void LargeBlock_Near65535_CompressesAndRoundtrips()
    {
        // Test large block: 4096 elements
        int count = 4096;
        float[] data = new float[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (float)Math.Round(50.0 + (i * 0.01), 2);
        }

        byte[] dest = new byte[AlpCompressor.GetMaxCompressedLength(count, sizeof(float))];
        int written = AlpCompressor.CompressFloat(data, dest, out _);

        float[] restored = new float[count];
        AlpCompressor.DecompressFloat(dest.AsSpan(0, written), restored);

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(data[i]),
                BitConverter.SingleToUInt32Bits(restored[i]));
        }
    }
}
