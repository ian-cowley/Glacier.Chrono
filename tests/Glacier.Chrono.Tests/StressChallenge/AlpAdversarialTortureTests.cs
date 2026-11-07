using System;
using System.Buffers.Binary;
using System.IO;
using Glacier.Chrono.Compression;
using Glacier.Chrono.Query;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.StressChallenge;

public class AlpAdversarialTortureTests : IDisposable
{
    private readonly string _scratchDir;

    public AlpAdversarialTortureTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "AlpTorture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    // =========================================================================
    // 1. SUBNORMALS AND DENORMALS
    // =========================================================================

    [Fact]
    public void SubnormalsAndDenormals_FloatAndDouble_100PercentBitExactRoundtrip()
    {
        float[] adversarialFloats =
        [
            1e-40f,
            -1e-40f,
            float.Epsilon,                                // 0x00000001
            -float.Epsilon,                               // 0x80000001
            BitConverter.UInt32BitsToSingle(0x007FFFFF), // Largest positive subnormal
            BitConverter.UInt32BitsToSingle(0x807FFFFF), // Largest negative subnormal
            BitConverter.UInt32BitsToSingle(0x00800000), // Smallest positive normal
            BitConverter.UInt32BitsToSingle(0x80800000), // Smallest negative normal
            1e-38f,
            -1e-38f
        ];

        byte[] destFloat = new byte[AlpCompressor.GetMaxCompressedLength(adversarialFloats.Length, sizeof(float))];
        int writtenF = AlpCompressor.CompressFloat(adversarialFloats, destFloat, out int exCountF);
        Assert.True(writtenF > 0);
        Assert.True(exCountF > 0, "Subnormals must be recorded in the exception dictionary.");

        float[] restoredFloats = new float[adversarialFloats.Length];
        int countF = AlpCompressor.DecompressFloat(destFloat.AsSpan(0, writtenF), restoredFloats);
        Assert.Equal(adversarialFloats.Length, countF);

        for (int i = 0; i < adversarialFloats.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(adversarialFloats[i]),
                BitConverter.SingleToUInt32Bits(restoredFloats[i]));
        }

        double[] adversarialDoubles =
        [
            1e-300,
            -1e-300,
            double.Epsilon,                                 // 0x0000000000000001
            -double.Epsilon,                                // 0x8000000000000001
            BitConverter.UInt64BitsToDouble(0x000FFFFFFFFFFFFFUL), // Largest positive subnormal double
            BitConverter.UInt64BitsToDouble(0x800FFFFFFFFFFFFFUL), // Largest negative subnormal double
            BitConverter.UInt64BitsToDouble(0x0010000000000000UL), // Smallest positive normal double
            BitConverter.UInt64BitsToDouble(0x8010000000000000UL), // Smallest negative normal double
            4.94e-324,
            -4.94e-324
        ];

        byte[] destDouble = new byte[AlpCompressor.GetMaxCompressedLength(adversarialDoubles.Length, sizeof(double))];
        int writtenD = AlpCompressor.CompressDouble(adversarialDoubles, destDouble, out int exCountD);
        Assert.True(writtenD > 0);
        Assert.True(exCountD > 0, "Subnormal doubles must be recorded in the exception dictionary.");

        double[] restoredDoubles = new double[adversarialDoubles.Length];
        int countD = AlpCompressor.DecompressDouble(destDouble.AsSpan(0, writtenD), restoredDoubles);
        Assert.Equal(adversarialDoubles.Length, countD);

        for (int i = 0; i < adversarialDoubles.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(adversarialDoubles[i]),
                BitConverter.DoubleToUInt64Bits(restoredDoubles[i]));
        }
    }

    // =========================================================================
    // 2. SIGNED ZERO (+0.0 vs -0.0) SIGN BIT PRESERVATION
    // =========================================================================

    [Fact]
    public void SignedZero_PreservesExactSignBit_FloatAndDouble()
    {
        // 1. All -0.0f (Constant mode)
        float[] allNegZeroF = [-0.0f, -0.0f, -0.0f, -0.0f];
        byte[] bufF = new byte[AlpCompressor.GetMaxCompressedLength(allNegZeroF.Length, sizeof(float))];
        int w1 = AlpCompressor.CompressFloat(allNegZeroF, bufF, out _);
        float[] r1 = new float[allNegZeroF.Length];
        AlpCompressor.DecompressFloat(bufF.AsSpan(0, w1), r1);
        for (int i = 0; i < allNegZeroF.Length; i++)
        {
            Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(r1[i]));
        }

        // 2. All +0.0f (Constant mode)
        float[] allPosZeroF = [0.0f, 0.0f, 0.0f, 0.0f];
        int w2 = AlpCompressor.CompressFloat(allPosZeroF, bufF, out _);
        float[] r2 = new float[allPosZeroF.Length];
        AlpCompressor.DecompressFloat(bufF.AsSpan(0, w2), r2);
        for (int i = 0; i < allPosZeroF.Length; i++)
        {
            Assert.Equal(0x00000000u, BitConverter.SingleToUInt32Bits(r2[i]));
        }

        // 3. Strictly alternating +0.0f and -0.0f
        float[] altZerosF = [0.0f, -0.0f, 0.0f, -0.0f, 0.0f, -0.0f];
        byte[] bufF6 = new byte[AlpCompressor.GetMaxCompressedLength(altZerosF.Length, sizeof(float))];
        int w3 = AlpCompressor.CompressFloat(altZerosF, bufF6, out int ex3);
        Assert.True(ex3 > 0, "Negative zeros must be routed to exception dictionary when mixed with positive.");
        float[] r3 = new float[altZerosF.Length];
        AlpCompressor.DecompressFloat(bufF6.AsSpan(0, w3), r3);
        for (int i = 0; i < altZerosF.Length; i++)
        {
            uint expected = (i % 2 == 0) ? 0x00000000u : 0x80000000u;
            Assert.Equal(expected, BitConverter.SingleToUInt32Bits(r3[i]));
        }

        // 4. Double precision -0.0 vs +0.0
        double[] allNegZeroD = [-0.0, -0.0, -0.0, -0.0];
        byte[] bufD = new byte[AlpCompressor.GetMaxCompressedLength(allNegZeroD.Length, sizeof(double))];
        int wd1 = AlpCompressor.CompressDouble(allNegZeroD, bufD, out _);
        double[] rd1 = new double[allNegZeroD.Length];
        AlpCompressor.DecompressDouble(bufD.AsSpan(0, wd1), rd1);
        for (int i = 0; i < allNegZeroD.Length; i++)
        {
            Assert.Equal(0x8000000000000000UL, BitConverter.DoubleToUInt64Bits(rd1[i]));
        }

        double[] altZerosD = [0.0, -0.0, 0.0, -0.0, 10.5, -0.0];
        byte[] bufD6 = new byte[AlpCompressor.GetMaxCompressedLength(altZerosD.Length, sizeof(double))];
        int wd2 = AlpCompressor.CompressDouble(altZerosD, bufD6, out int exd2);
        Assert.True(exd2 > 0);
        double[] rd2 = new double[altZerosD.Length];
        AlpCompressor.DecompressDouble(bufD6.AsSpan(0, wd2), rd2);
        for (int i = 0; i < altZerosD.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(altZerosD[i]),
                BitConverter.DoubleToUInt64Bits(rd2[i]));
        }
    }

    // =========================================================================
    // 3. QUIET AND SIGNALING NANS & INFINITIES
    // =========================================================================

    [Fact]
    public void NaNsAndInfinities_QuietAndSignaling_PayloadPreservation()
    {
        float[] specialFloats =
        [
            float.NaN,                                    // Default quiet NaN
            BitConverter.UInt32BitsToSingle(0x7FC00000), // Standard quiet NaN
            BitConverter.UInt32BitsToSingle(0x7F800001), // Signaling NaN #1
            BitConverter.UInt32BitsToSingle(0x7F800002), // Signaling NaN #2
            BitConverter.UInt32BitsToSingle(0x7FC01234), // Custom quiet NaN payload
            BitConverter.UInt32BitsToSingle(0xFFC00000), // Negative quiet NaN
            BitConverter.UInt32BitsToSingle(0xFF800001), // Negative signaling NaN
            float.PositiveInfinity,
            float.NegativeInfinity,
            123.456f // Interleaved finite value
        ];

        byte[] destF = new byte[AlpCompressor.GetMaxCompressedLength(specialFloats.Length, sizeof(float))];
        int writtenF = AlpCompressor.CompressFloat(specialFloats, destF, out int exF);
        Assert.True(writtenF > 0);
        Assert.Equal(specialFloats.Length - 1, exF); // 9 non-finite values are exceptions

        float[] restoredF = new float[specialFloats.Length];
        AlpCompressor.DecompressFloat(destF.AsSpan(0, writtenF), restoredF);

        for (int i = 0; i < specialFloats.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(specialFloats[i]),
                BitConverter.SingleToUInt32Bits(restoredF[i]));
        }

        double[] specialDoubles =
        [
            double.NaN,
            BitConverter.UInt64BitsToDouble(0x7FF8000000000000UL), // Default quiet NaN
            BitConverter.UInt64BitsToDouble(0x7FF0000000000001UL), // Signaling NaN
            BitConverter.UInt64BitsToDouble(0x7FF00000DEADBEEFUL), // Custom signaling NaN
            BitConverter.UInt64BitsToDouble(0xFFF8000000000000UL), // Negative quiet NaN
            BitConverter.UInt64BitsToDouble(0xFFF0000000000001UL), // Negative signaling NaN
            double.PositiveInfinity,
            double.NegativeInfinity,
            98765.4321
        ];

        byte[] destD = new byte[AlpCompressor.GetMaxCompressedLength(specialDoubles.Length, sizeof(double))];
        int writtenD = AlpCompressor.CompressDouble(specialDoubles, destD, out int exD);
        Assert.True(writtenD > 0);
        Assert.Equal(specialDoubles.Length - 1, exD);

        double[] restoredD = new double[specialDoubles.Length];
        AlpCompressor.DecompressDouble(destD.AsSpan(0, writtenD), restoredD);

        for (int i = 0; i < specialDoubles.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(specialDoubles[i]),
                BitConverter.DoubleToUInt64Bits(restoredD[i]));
        }
    }

    // =========================================================================
    // 4. IRRATIONAL FRACTIONS AND TRANSCENDENTAL NUMBERS
    // =========================================================================

    [Fact]
    public void IrrationalFractionsAndConstants_BitExactRoundtrip()
    {
        float[] irrationalsF =
        [
            1.0f / 3.0f,
            1.0f / 7.0f,
            1.0f / 11.0f,
            1.0f / 13.0f,
            MathF.PI,
            MathF.E,
            MathF.Sqrt(2.0f),
            (1.0f + MathF.Sqrt(5.0f)) / 2.0f // Golden ratio
        ];

        byte[] destF = new byte[AlpCompressor.GetMaxCompressedLength(irrationalsF.Length, sizeof(float))];
        int wF = AlpCompressor.CompressFloat(irrationalsF, destF, out _);
        float[] resF = new float[irrationalsF.Length];
        AlpCompressor.DecompressFloat(destF.AsSpan(0, wF), resF);

        for (int i = 0; i < irrationalsF.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(irrationalsF[i]),
                BitConverter.SingleToUInt32Bits(resF[i]));
        }

        double[] irrationalsD =
        [
            1.0 / 3.0,
            1.0 / 7.0,
            1.0 / 11.0,
            1.0 / 13.0,
            Math.PI,
            Math.E,
            Math.Sqrt(2.0),
            (1.0 + Math.Sqrt(5.0)) / 2.0
        ];

        byte[] destD = new byte[AlpCompressor.GetMaxCompressedLength(irrationalsD.Length, sizeof(double))];
        int wD = AlpCompressor.CompressDouble(irrationalsD, destD, out _);
        double[] resD = new double[irrationalsD.Length];
        AlpCompressor.DecompressDouble(destD.AsSpan(0, wD), resD);

        for (int i = 0; i < irrationalsD.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(irrationalsD[i]),
                BitConverter.DoubleToUInt64Bits(resD[i]));
        }
    }

    // =========================================================================
    // 5. BOUNDARY BUFFER SIZES, W=0, W=1, SPIKES, HIGH ENTROPY
    // =========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(1023)]
    [InlineData(1024)]
    public void BoundaryBufferSizes_FloatAndDouble_LosslessFidelity(int size)
    {
        float[] floats = new float[size];
        for (int i = 0; i < size; i++)
        {
            floats[i] = (float)Math.Round(10.0 + (i * 0.05), 2);
        }

        byte[] destF = new byte[AlpCompressor.GetMaxCompressedLength(size, sizeof(float))];
        int wF = AlpCompressor.CompressFloat(floats, destF, out _);
        if (size == 0)
        {
            Assert.Equal(0, wF);
            return;
        }

        float[] resF = new float[size];
        int cF = AlpCompressor.DecompressFloat(destF.AsSpan(0, wF), resF);
        Assert.Equal(size, cF);

        for (int i = 0; i < size; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(floats[i]),
                BitConverter.SingleToUInt32Bits(resF[i]));
        }

        double[] doubles = new double[size];
        for (int i = 0; i < size; i++)
        {
            doubles[i] = Math.Round(100.0 + (i * 0.025), 3);
        }

        byte[] destD = new byte[AlpCompressor.GetMaxCompressedLength(size, sizeof(double))];
        int wD = AlpCompressor.CompressDouble(doubles, destD, out _);
        double[] resD = new double[size];
        int cD = AlpCompressor.DecompressDouble(destD.AsSpan(0, wD), resD);
        Assert.Equal(size, cD);

        for (int i = 0; i < size; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(doubles[i]),
                BitConverter.DoubleToUInt64Bits(resD[i]));
        }
    }

    [Fact]
    public void StrictlyAlternatingValues_W1_BitExactFidelity()
    {
        // 1. Length 128: stride = 1, both phases are sampled, e=1 chosen, W=1, 0 exceptions
        float[] alt128 = new float[128];
        for (int i = 0; i < alt128.Length; i++)
        {
            alt128[i] = (i % 2 == 0) ? 10.0f : 10.1f;
        }

        byte[] dest128 = new byte[AlpCompressor.GetMaxCompressedLength(alt128.Length, sizeof(float))];
        int written128 = AlpCompressor.CompressFloat(alt128, dest128, out int ex128);
        Assert.Equal(0, ex128);

        float[] restored128 = new float[alt128.Length];
        AlpCompressor.DecompressFloat(dest128.AsSpan(0, written128), restored128);

        for (int i = 0; i < alt128.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(alt128[i]),
                BitConverter.SingleToUInt32Bits(restored128[i]));
        }

        // 2. Length 512: stride = 4 (even stride aliasing). Even indices (10.0f) sample perfectly at e=0,
        // and odd indices (10.1f) are safely caught by the exception dictionary with 100% bit-exact fidelity.
        float[] alt512 = new float[512];
        for (int i = 0; i < alt512.Length; i++)
        {
            alt512[i] = (i % 2 == 0) ? 10.0f : 10.1f;
        }

        byte[] dest512 = new byte[AlpCompressor.GetMaxCompressedLength(alt512.Length, sizeof(float))];
        int written512 = AlpCompressor.CompressFloat(alt512, dest512, out int ex512);
        Assert.True(written512 > 0);

        float[] restored512 = new float[alt512.Length];
        AlpCompressor.DecompressFloat(dest512.AsSpan(0, written512), restored512);

        for (int i = 0; i < alt512.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(alt512[i]),
                BitConverter.SingleToUInt32Bits(restored512[i]));
        }
    }

    [Fact]
    public void MassiveSpikes_FloatAndDouble_LosslessFidelity()
    {
        // Spikes: small decimal mixed with extreme 1e30f values
        float[] spikedF = new float[256];
        for (int i = 0; i < spikedF.Length; i++)
        {
            spikedF[i] = (i % 10 == 0) ? 1e30f : (float)Math.Round(20.0 + (i * 0.1), 1);
        }

        byte[] destF = new byte[AlpCompressor.GetMaxCompressedLength(spikedF.Length, sizeof(float))];
        int writtenF = AlpCompressor.CompressFloat(spikedF, destF, out _);

        float[] resF = new float[spikedF.Length];
        AlpCompressor.DecompressFloat(destF.AsSpan(0, writtenF), resF);

        for (int i = 0; i < spikedF.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(spikedF[i]),
                BitConverter.SingleToUInt32Bits(resF[i]));
        }

        // Double spikes with 1e200
        double[] spikedD = new double[256];
        for (int i = 0; i < spikedD.Length; i++)
        {
            spikedD[i] = (i % 10 == 0) ? 1e200 : Math.Round(50.0 + (i * 0.05), 2);
        }

        byte[] destD = new byte[AlpCompressor.GetMaxCompressedLength(spikedD.Length, sizeof(double))];
        int writtenD = AlpCompressor.CompressDouble(spikedD, destD, out _);

        double[] resD = new double[spikedD.Length];
        AlpCompressor.DecompressDouble(destD.AsSpan(0, writtenD), resD);

        for (int i = 0; i < spikedD.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(spikedD[i]),
                BitConverter.DoubleToUInt64Bits(resD[i]));
        }
    }

    [Fact]
    public void HighEntropyPseudoRandom_FidelityAndExceptionResilience()
    {
        // High entropy data where most values are incompressible
        var rng = new Random(42);
        float[] randomFloats = new float[256];
        for (int i = 0; i < randomFloats.Length; i++)
        {
            byte[] bytes = new byte[4];
            rng.NextBytes(bytes);
            randomFloats[i] = BitConverter.ToSingle(bytes);
        }

        byte[] destF = new byte[AlpCompressor.GetMaxCompressedLength(randomFloats.Length, sizeof(float))];
        int writtenF = AlpCompressor.CompressFloat(randomFloats, destF, out _);

        float[] resF = new float[randomFloats.Length];
        AlpCompressor.DecompressFloat(destF.AsSpan(0, writtenF), resF);

        for (int i = 0; i < randomFloats.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(randomFloats[i]),
                BitConverter.SingleToUInt32Bits(resF[i]));
        }
    }

    // =========================================================================
    // 6. ALP_RD DOUBLE SPLITTING & NEGATIVE VALUES
    // =========================================================================

    [Fact]
    public void AlpRd_NegativeAndCrossZeroDoubles_LosslessFidelity()
    {
        // Negative high-precision doubles with wide integer and decimal components
        double[] negDoubles = new double[128];
        for (int i = 0; i < negDoubles.Length; i++)
        {
            negDoubles[i] = -50000.0 - (i * 0.001234);
        }

        byte[] dest = new byte[AlpCompressor.GetMaxCompressedLength(negDoubles.Length, sizeof(double))];
        int written = AlpCompressor.CompressDouble(negDoubles, dest, out _);

        double[] restored = new double[negDoubles.Length];
        AlpCompressor.DecompressDouble(dest.AsSpan(0, written), restored);

        for (int i = 0; i < negDoubles.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(negDoubles[i]),
                BitConverter.DoubleToUInt64Bits(restored[i]));
        }

        // Values crossing zero: [-200.5 .. +200.5]
        double[] crossZero = new double[200];
        for (int i = 0; i < crossZero.Length; i++)
        {
            crossZero[i] = -100.0 + (i * 1.005);
        }

        byte[] destCross = new byte[AlpCompressor.GetMaxCompressedLength(crossZero.Length, sizeof(double))];
        int writtenCross = AlpCompressor.CompressDouble(crossZero, destCross, out _);
        double[] restoredCross = new double[crossZero.Length];
        AlpCompressor.DecompressDouble(destCross.AsSpan(0, writtenCross), restoredCross);

        for (int i = 0; i < crossZero.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(crossZero[i]),
                BitConverter.DoubleToUInt64Bits(restoredCross[i]));
        }
    }

    // =========================================================================
    // 7. COMPACTOR V3 METRIC ZONE MAPS
    // =========================================================================

    [Fact]
    public void CompactorV3_MetricZoneMaps_SingleRowBatchAndConstantMetrics()
    {
        string dir = Path.Combine(_scratchDir, "v3_single");
        Directory.CreateDirectory(dir);

        // Batch of 1 row
        var ring = new HotRingBuffer<TelemetryRow>(16);
        ring.Write(new TelemetryRow
        {
            Timestamp = 42000L,
            CpuUsage = 77.5f,
            MemUsage = 1024.0f,
            EntityId = 999
        });

        var buffers = new CompactorBuffers(1);
        long nextSeq = 0;
        bool compacted = Compactor.CompactBatch(ring, ref nextSeq, 1, dir, buffers);
        Assert.True(compacted);

        string chunk = Path.Combine(dir, "chunk_0.glacier");
        Assert.True(File.Exists(chunk));

        bool ok = Compactor.TryReadMetricZoneMap(chunk,
            out double minCpu, out double maxCpu,
            out double minMem, out double maxMem,
            out int minEnt, out int maxEnt);

        Assert.True(ok);
        Assert.Equal(77.5, minCpu, 1e-4);
        Assert.Equal(77.5, maxCpu, 1e-4);
        Assert.Equal(1024.0, minMem, 1e-4);
        Assert.Equal(1024.0, maxMem, 1e-4);
        Assert.Equal(999, minEnt);
        Assert.Equal(999, maxEnt);
    }

    [Fact]
    public void CompactorV3_TryReadMetricZoneMap_RejectsV2ChunksAndCorruptedFiles()
    {
        // 1. Create a dummy v2 chunk file (header version = 2)
        string v2File = Path.Combine(_scratchDir, "dummy_v2.glacier");
        byte[] v2Header = new byte[Compactor.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(v2Header.AsSpan(0, 4), Compactor.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(v2Header.AsSpan(4, 4), 2); // Version 2
        File.WriteAllBytes(v2File, v2Header);

        bool v2Result = Compactor.TryReadMetricZoneMap(v2File,
            out _, out _, out _, out _, out _, out _);
        Assert.False(v2Result, "FormatVersion 2 chunk must return false for metric zone map parsing.");

        // 2. Truncated file (< HeaderSize)
        string truncFile = Path.Combine(_scratchDir, "trunc.glacier");
        File.WriteAllBytes(truncFile, new byte[64]);
        bool truncResult = Compactor.TryReadMetricZoneMap(truncFile,
            out _, out _, out _, out _, out _, out _);
        Assert.False(truncResult);

        // 3. Invalid magic bytes
        string badMagicFile = Path.Combine(_scratchDir, "badmagic.glacier");
        byte[] badHeader = new byte[Compactor.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(badHeader.AsSpan(0, 4), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(badHeader.AsSpan(4, 4), 3);
        File.WriteAllBytes(badMagicFile, badHeader);
        bool badResult = Compactor.TryReadMetricZoneMap(badMagicFile,
            out _, out _, out _, out _, out _, out _);
        Assert.False(badResult);
    }

    // =========================================================================
    // 8. QUERY ENGINE TRANSPARENT EXECUTION ACROSS V2 AND V3 CHUNKS
    // =========================================================================

    [Fact]
    public void QueryEngine_TransparentExecution_HeterogeneousChunkDirectory()
    {
        string dir = Path.Combine(_scratchDir, "heterogeneous_chunks");
        Directory.CreateDirectory(dir);

        int batchSize = 64;

        // Chunk 0: v2 file with Gorilla compression
        // Chunk 1: v3 file with Gorilla compression (Compactor.CompactBatch output)
        // Chunk 2: v3 file with ALP compression

        float[] cpuChunk0 = new float[batchSize];
        float[] cpuChunk1 = new float[batchSize];
        float[] cpuChunk2 = new float[batchSize];

        double grandSum = 0.0;
        long totalRows = batchSize * 3;

        for (int i = 0; i < batchSize; i++)
        {
            cpuChunk0[i] = 10.0f + (i * 0.2f);
            cpuChunk1[i] = 30.0f + (i * 0.1f);
            cpuChunk2[i] = 50.0f + (i * 0.3f);

            grandSum += cpuChunk0[i];
            grandSum += cpuChunk1[i];
            grandSum += cpuChunk2[i];
        }

        double expectedGrandAverage = grandSum / totalRows;

        // Build Chunk 0 (v2 format with Gorilla)
        CreateChunkFile(
            Path.Combine(dir, "chunk_0.glacier"),
            version: 2,
            baseTs: 1000L,
            batchSize: batchSize,
            cpuValues: cpuChunk0,
            useAlpForCpu: false);

        // Build Chunk 1 (v3 format with Gorilla)
        CreateChunkFile(
            Path.Combine(dir, "chunk_1.glacier"),
            version: 3,
            baseTs: 2000L,
            batchSize: batchSize,
            cpuValues: cpuChunk1,
            useAlpForCpu: false);

        // Build Chunk 2 (v3 format with ALP)
        CreateChunkFile(
            Path.Combine(dir, "chunk_2.glacier"),
            version: 3,
            baseTs: 3000L,
            batchSize: batchSize,
            cpuValues: cpuChunk2,
            useAlpForCpu: true);

        var queryBuffers = new QueryBuffers(batchSize);

        // Execute scan across entire heterogeneous directory
        double actualAvg = QueryEngine.GetAverageCpuUsageInDirectory(
            dir,
            startTimestamp: long.MinValue,
            endTimestamp: long.MaxValue,
            queryBuffers);

        Assert.Equal(expectedGrandAverage, actualAvg, 1e-4);

        // Test O(1) zone map timestamp pruning: Query completely disjoint window [9000 .. 10000]
        double prunedAvg = QueryEngine.GetAverageCpuUsageInDirectory(
            dir,
            startTimestamp: 9000L,
            endTimestamp: 10000L,
            queryBuffers);

        Assert.Equal(0.0, prunedAvg);
    }

    private static void CreateChunkFile(
        string path,
        uint version,
        long baseTs,
        int batchSize,
        float[] cpuValues,
        bool useAlpForCpu)
    {
        long[] timestamps = new long[batchSize];
        int[] entityIds = new int[batchSize];
        for (int i = 0; i < batchSize; i++)
        {
            timestamps[i] = baseTs + i;
            entityIds[i] = 1;
        }

        byte[] compTs = new byte[batchSize * 9 + 64];
        int tsLen = TimestampCompressor.Compress(timestamps, compTs);

        byte[] compCpu;
        int cpuLen;
        if (useAlpForCpu)
        {
            compCpu = new byte[AlpCompressor.GetMaxCompressedLength(batchSize, sizeof(float))];
            cpuLen = AlpCompressor.CompressFloat(cpuValues, compCpu);
        }
        else
        {
            compCpu = new byte[batchSize * 5 + 64];
            cpuLen = GorillaCompressor.Compress(cpuValues, compCpu);
        }

        byte[] compEntity = new byte[batchSize * 5 + 64];
        int entityLen = IntegerRleCompressor.Compress(entityIds, compEntity);

        byte[] header = new byte[Compactor.HeaderSize];
        long tsOffset = Compactor.HeaderSize;
        long cpuOffset = tsOffset + tsLen;
        long memOffset = cpuOffset + cpuLen;
        long entityOffset = memOffset + 4; // dummy mem

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Compactor.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), batchSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12, 8), timestamps[0]);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(20, 8), timestamps[batchSize - 1]);

        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(28, 8), tsOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36, 4), tsLen);

        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40, 8), cpuOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(48, 4), cpuLen);

        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(52, 8), memOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60, 4), 4);

        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(64, 8), entityOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(72, 4), entityLen);

        if (version >= 3)
        {
            float minC = float.MaxValue, maxC = float.MinValue;
            for (int i = 0; i < batchSize; i++)
            {
                if (cpuValues[i] < minC) minC = cpuValues[i];
                if (cpuValues[i] > maxC) maxC = cpuValues[i];
            }
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(76, 8), minC);
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(84, 8), maxC);
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(92, 8), 0.0);
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(100, 8), 0.0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(108, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(112, 4), 1);
        }

        using var fs = File.Create(path);
        fs.Write(header);
        fs.Write(compTs.AsSpan(0, tsLen));
        fs.Write(compCpu.AsSpan(0, cpuLen));
        fs.Write(new byte[4]); // dummy mem
        fs.Write(compEntity.AsSpan(0, entityLen));
    }

    // =========================================================================
    // 9. CORRUPTION AND TRUNCATION FUZZING
    // =========================================================================

    [Fact]
    public void CorruptedAndTruncatedBuffers_SafeExceptionHandling()
    {
        // 1. Buffer too small for header
        byte[] tiny = new byte[32];
        float[] destF = new float[10];
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(tiny, destF));

        // 2. Invalid magic bytes
        byte[] badMagic = new byte[AlpCompressor.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(badMagic.AsSpan(0, 4), 0x12345678);
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(badMagic, destF));

        // 3. Truncated payload: header claims 100 elements with 16-bit packed width, but payload is truncated
        byte[] truncatedPayload = new byte[AlpCompressor.HeaderSize + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedPayload.AsSpan(0, 4), AlpCompressor.Magic);
        truncatedPayload[4] = (byte)AlpCompressionMode.AlpFloat;
        truncatedPayload[5] = 2; // exponent
        truncatedPayload[6] = 16; // bitWidth
        BinaryPrimitives.WriteUInt16LittleEndian(truncatedPayload.AsSpan(0x0A, 2), 100); // count = 100 -> 200 bytes needed
        float[] largeDest = new float[100];
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(truncatedPayload, largeDest));

        // 4. Destination too small
        float[] tooSmall = new float[5];
        byte[] validBlock = new byte[AlpCompressor.GetMaxCompressedLength(10, sizeof(float))];
        int w = AlpCompressor.CompressFloat(new float[10], validBlock);
        Assert.Throws<ArgumentException>(() => AlpCompressor.DecompressFloat(validBlock.AsSpan(0, w), tooSmall));
    }
}
