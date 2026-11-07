using System;
using System.IO;
using System.Security.Cryptography;
using Glacier.Chrono.Compression;
using Glacier.Chrono.Storage;
using Xunit;

namespace Glacier.Chrono.Tests.Compression;

public class AlpCompressorTests
{
    [Fact]
    public void CompressFloat_DecimalTelemetry_BitExactRoundtrip()
    {
        float[] original = [12.34f, 45.67f, 89.01f, 23.45f, 67.89f, 0.12f, 9.99f, 100.50f];
        byte[] compressed = new byte[AlpCompressor.GetMaxCompressedLength(original.Length, sizeof(float))];

        int written = AlpCompressor.CompressFloat(original, compressed, out int exCount);
        Assert.True(written > 0);
        Assert.Equal(0, exCount);

        float[] restored = new float[original.Length];
        int count = AlpCompressor.DecompressFloat(compressed.AsSpan(0, written), restored);
        Assert.Equal(original.Length, count);

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(original[i]), BitConverter.SingleToUInt32Bits(restored[i]));
        }
    }

    [Fact]
    public void CompressFloat_NonDecimalExceptions_BitExactRoundtrip()
    {
        float[] original = [10.50f, 1.0f / 3.0f, 20.25f, 1.0f / 7.0f, 30.75f];
        byte[] compressed = new byte[AlpCompressor.GetMaxCompressedLength(original.Length, sizeof(float))];

        int written = AlpCompressor.CompressFloat(original, compressed, out int exCount);
        Assert.True(written > 0);
        Assert.True(exCount > 0, "Non-decimal fractions should be recorded in exception dictionary.");

        float[] restored = new float[original.Length];
        int count = AlpCompressor.DecompressFloat(compressed.AsSpan(0, written), restored);
        Assert.Equal(original.Length, count);

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(original[i]), BitConverter.SingleToUInt32Bits(restored[i]));
        }
    }

    [Fact]
    public void CompressFloat_SpecialIeee754Values_BitExactRoundtrip()
    {
        float[] original =
        [
            0.0f,
            -0.0f,
            float.PositiveInfinity,
            float.NegativeInfinity,
            float.NaN,
            float.Epsilon,
            float.MinValue,
            float.MaxValue,
            12.34f
        ];

        byte[] compressed = new byte[AlpCompressor.GetMaxCompressedLength(original.Length, sizeof(float))];
        int written = AlpCompressor.CompressFloat(original, compressed, out _);

        float[] restored = new float[original.Length];
        AlpCompressor.DecompressFloat(compressed.AsSpan(0, written), restored);

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(original[i]), BitConverter.SingleToUInt32Bits(restored[i]));
        }
    }

    [Fact]
    public void CompressFloat_ConstantValues_ProducesMinimalHeader()
    {
        float[] original = new float[1024];
        Array.Fill(original, 42.0f);

        byte[] compressed = new byte[AlpCompressor.GetMaxCompressedLength(original.Length, sizeof(float))];
        int written = AlpCompressor.CompressFloat(original, compressed, out int exCount);

        Assert.Equal(AlpCompressor.HeaderSize, written);
        Assert.Equal(0, exCount);

        float[] restored = new float[original.Length];
        int count = AlpCompressor.DecompressFloat(compressed.AsSpan(0, written), restored);

        Assert.Equal(original.Length, count);
        Assert.All(restored, v => Assert.Equal(42.0f, v));
    }

    [Fact]
    public void CompressFloat_NegativeNumbers_LosslessRoundtrip()
    {
        float[] original = [-10.25f, -0.50f, -99.99f, 0.0f, 15.75f];
        byte[] compressed = new byte[AlpCompressor.GetMaxCompressedLength(original.Length, sizeof(float))];

        int written = AlpCompressor.CompressFloat(original, compressed, out _);
        float[] restored = new float[original.Length];
        AlpCompressor.DecompressFloat(compressed.AsSpan(0, written), restored);

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(original[i]), BitConverter.SingleToUInt32Bits(restored[i]));
        }
    }

    [Fact]
    public void CompressDouble_StandardAlp_BitExactRoundtrip()
    {
        double[] original = [123.45, 678.90, 0.05, 9999.99, -42.50];
        byte[] compressed = new byte[AlpCompressor.GetMaxCompressedLength(original.Length, sizeof(double))];

        int written = AlpCompressor.CompressDouble(original, compressed, out int exCount);
        Assert.True(written > 0);

        double[] restored = new double[original.Length];
        int count = AlpCompressor.DecompressDouble(compressed.AsSpan(0, written), restored);
        Assert.Equal(original.Length, count);

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToUInt64Bits(original[i]), BitConverter.DoubleToUInt64Bits(restored[i]));
        }
    }

    [Fact]
    public void CompressDouble_AlpRd_HighPrecisionFinancial_BitExactRoundtrip()
    {
        // Values with wide integer and fraction components
        double[] original = new double[100];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = 100000.0 + (i * 0.123456);
        }

        byte[] compressed = new byte[AlpCompressor.GetMaxCompressedLength(original.Length, sizeof(double))];
        int written = AlpCompressor.CompressDouble(original, compressed, out _);

        double[] restored = new double[original.Length];
        AlpCompressor.DecompressDouble(compressed.AsSpan(0, written), restored);

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToUInt64Bits(original[i]), BitConverter.DoubleToUInt64Bits(restored[i]));
        }
    }

    [Fact]
    public void CompressDouble_SpecialIeee754Values_BitExactRoundtrip()
    {
        double[] original =
        [
            0.0,
            -0.0,
            double.PositiveInfinity,
            double.NegativeInfinity,
            double.NaN,
            double.Epsilon,
            double.MinValue,
            double.MaxValue,
            3.141592653589793
        ];

        byte[] compressed = new byte[AlpCompressor.GetMaxCompressedLength(original.Length, sizeof(double))];
        int written = AlpCompressor.CompressDouble(original, compressed, out _);

        double[] restored = new double[original.Length];
        AlpCompressor.DecompressDouble(compressed.AsSpan(0, written), restored);

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToUInt64Bits(original[i]), BitConverter.DoubleToUInt64Bits(restored[i]));
        }
    }

    [Fact]
    public void CompressionRatio_SmoothDecimalTelemetry_ExceedsThreshold()
    {
        // 1024 smooth sensor temperatures: 20.00 to 25.115 with 0.005 increments
        float[] data = new float[1024];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)Math.Round(20.0 + i * 0.01, 2);
        }

        int uncompressedBytes = data.Length * sizeof(float); // 4096 bytes
        byte[] compressed = new byte[AlpCompressor.GetMaxCompressedLength(data.Length, sizeof(float))];
        int written = AlpCompressor.CompressFloat(data, compressed, out int exCount);

        Assert.Equal(0, exCount);
        // Expect compressed size <= 35% of uncompressed size
        double ratio = (double)uncompressedBytes / written;
        Assert.True(ratio >= 2.5, $"Expected compression ratio >= 2.5x, actual: {ratio:F2}x ({written} bytes vs {uncompressedBytes} bytes)");
    }

    [Fact]
    public void CodecAdapter_ImplementsIAlpCompressionCodec()
    {
        IAlpCompressionCodec codec = new AlpCompressionCodec();
        float[] floats = [1.25f, 2.50f, 3.75f, 4.00f];
        byte[] compFloats = codec.Compress(floats, out int fEx);
        Assert.Equal(0, fEx);
        float[] decFloats = codec.Decompress(compFloats, floats.Length);
        Assert.Equal(floats.Length, decFloats.Length);
        for (int i = 0; i < floats.Length; i++)
        {
            Assert.Equal(floats[i], decFloats[i]);
        }

        double[] doubles = [10.125, 20.250, 30.375];
        byte[] compDoubles = codec.CompressDouble(doubles, out int dEx);
        Assert.Equal(0, dEx);
        double[] decDoubles = codec.DecompressDouble(compDoubles, doubles.Length);
        Assert.Equal(doubles.Length, decDoubles.Length);
        for (int i = 0; i < doubles.Length; i++)
        {
            Assert.Equal(doubles[i], decDoubles[i]);
        }
    }

    [Fact]
    public void Compactor_FormatVersion3_WritesAndReadsMetricZoneMaps()
    {
        string testDir = Path.Combine(Path.GetTempPath(), "glacier_chrono_m3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        try
        {
            int batchSize = 100;
            var ring = new HotRingBuffer<TelemetryRow>(256);
            for (int i = 0; i < batchSize; i++)
            {
                ring.Write(new TelemetryRow
                {
                    Timestamp = 1000L + i,
                    CpuUsage = 20.0f + (i * 0.5f), // [20.0f, 69.5f]
                    MemUsage = 50.0f + (i * 0.2f), // [50.0f, 69.8f]
                    EntityId = 10 + i
                });
            }

            var buffers = new CompactorBuffers(batchSize);
            long nextSeq = 0;
            bool ok = Compactor.CompactBatch(ring, ref nextSeq, batchSize, testDir, buffers);
            Assert.True(ok);

            string chunkPath = Path.Combine(testDir, "chunk_0.glacier");
            Assert.True(File.Exists(chunkPath));

            bool readOk = Compactor.TryReadMetricZoneMap(chunkPath,
                out double minCpu, out double maxCpu,
                out double minMem, out double maxMem,
                out int minEntity, out int maxEntity);

            Assert.True(readOk);
            Assert.Equal(20.0, minCpu, 1e-4);
            Assert.Equal(69.5, maxCpu, 1e-4);
            Assert.Equal(50.0, minMem, 1e-4);
            Assert.Equal(69.8, maxMem, 1e-4);
            Assert.Equal(10, minEntity);
            Assert.Equal(10 + batchSize - 1, maxEntity);
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CorruptedData_ThrowsInvalidDataException()
    {
        byte[] badMagic = new byte[AlpCompressor.HeaderSize];
        Array.Fill(badMagic, (byte)0xFF);
        float[] dest = new float[10];

        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(badMagic, dest));
    }
}
