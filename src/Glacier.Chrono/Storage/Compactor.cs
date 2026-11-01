using System;
using System.Buffers.Binary;
using System.IO;
using Glacier.Chrono.Compression;

namespace Glacier.Chrono.Storage;

public class CompactorBuffers
{
    public readonly TelemetryRow[] RowBuffer;
    public readonly long[] Timestamps;
    public readonly float[] CpuUsages;
    public readonly float[] MemUsages;
    public readonly int[] EntityIds;

    public readonly byte[] CompressedTimestamps;
    public readonly byte[] CompressedCpuUsages;
    public readonly byte[] CompressedMemUsages;
    public readonly byte[] CompressedEntityIds;

    public readonly byte[] FileHeaderBuffer;

    public CompactorBuffers(int batchSize)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be greater than zero.");
        }

        RowBuffer = new TelemetryRow[batchSize];
        Timestamps = new long[batchSize];
        CpuUsages = new float[batchSize];
        MemUsages = new float[batchSize];
        EntityIds = new int[batchSize];

        // Safe theoretical worst-case sizes to prevent overflow under random/unsorted data:
        // - Timestamps (DoD): 9 bytes per row max
        // - Floats (Gorilla): 6 bytes per row max
        // - Integers (RLE): 5 bytes per row max
        CompressedTimestamps = new byte[batchSize * 9 + 64];
        CompressedCpuUsages = new byte[batchSize * 6 + 64];
        CompressedMemUsages = new byte[batchSize * 6 + 64];
        CompressedEntityIds = new byte[batchSize * 5 + 64];

        FileHeaderBuffer = new byte[Compactor.HeaderSize];
    }
}

public static class Compactor
{
    /// <summary>
    /// Magic header identifier ('GLCH' in little-endian).
    /// </summary>
    public const uint Magic = 0x48434C47;

    /// <summary>
    /// Current .glacier chunk header format version (includes zone-map [MinTimestamp, MaxTimestamp] bounds).
    /// </summary>
    public const uint FormatVersion = 2;

    /// <summary>
    /// 64-byte aligned header size for <see cref="TelemetryRow"/> chunks:
    /// Magic(4) + Version(4) + RowCount(4) + MinTs(8) + MaxTs(8) + Ts(12) + Cpu(12) + Mem(12) + Entity(12) = 76 -> 128 bytes.
    /// </summary>
    public const int HeaderSize = (40 + 3 * 12 + 63) & ~63;

    /// <summary>
    /// Compacts a batch of rows from the HotRingBuffer, converting them to compressed columnar format,
    /// and writing them as a single .glacier chunk file to disk.
    /// Returns <see langword="true"/> if a batch was successfully compacted; <see langword="false"/> if there was insufficient data.
    /// </summary>
    public static bool CompactBatch(
        HotRingBuffer<TelemetryRow> ringBuffer,
        ref long nextSequenceToRead,
        int batchSize,
        string outputDirectory,
        CompactorBuffers buffers)
    {
        ArgumentNullException.ThrowIfNull(ringBuffer);
        ArgumentNullException.ThrowIfNull(buffers);

        if (batchSize <= 0 || batchSize > buffers.RowBuffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be between 1 and buffers capacity.");
        }

        // 1. Try to read the requested batch slice (supports partial batches < buffers.RowBuffer.Length)
        Span<TelemetryRow> rowSpan = buffers.RowBuffer.AsSpan(0, batchSize);
        if (!ringBuffer.TryReadBatch(nextSequenceToRead, rowSpan))
        {
            return false;
        }

        // 2. Sort the batch ascending by Timestamp (zero-allocation using struct IComparable)
        Array.Sort(buffers.RowBuffer, 0, batchSize);

        // 3. Matrix Transpose (AoS to SoA)
        for (int i = 0; i < batchSize; i++)
        {
            buffers.Timestamps[i] = buffers.RowBuffer[i].Timestamp;
            buffers.CpuUsages[i] = buffers.RowBuffer[i].CpuUsage;
            buffers.MemUsages[i] = buffers.RowBuffer[i].MemUsage;
            buffers.EntityIds[i] = buffers.RowBuffer[i].EntityId;
        }

        long minTimestamp = buffers.Timestamps[0];
        long maxTimestamp = buffers.Timestamps[batchSize - 1];

        // 4. Compress each column over the exact [0..batchSize) slice
        int tsLen = TimestampCompressor.Compress(buffers.Timestamps.AsSpan(0, batchSize), buffers.CompressedTimestamps);
        int cpuLen = GorillaCompressor.Compress(buffers.CpuUsages.AsSpan(0, batchSize), buffers.CompressedCpuUsages);
        int memLen = GorillaCompressor.Compress(buffers.MemUsages.AsSpan(0, batchSize), buffers.CompressedMemUsages);
        int entityLen = IntegerRleCompressor.Compress(buffers.EntityIds.AsSpan(0, batchSize), buffers.CompressedEntityIds);

        // 5. Calculate Column Offsets
        long tsOffset = HeaderSize;
        long cpuOffset = tsOffset + tsLen;
        long memOffset = cpuOffset + cpuLen;
        long entityOffset = memOffset + memLen;

        // 6. Populate 64-byte-aligned v2 Header
        Span<byte> header = buffers.FileHeaderBuffer.AsSpan(0, HeaderSize);
        header.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, 4), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), batchSize);

        // Zone-map timestamp bounds
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(12, 8), minTimestamp);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(20, 8), maxTimestamp);

        // Timestamps offset & length
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(28, 8), tsOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(36, 4), tsLen);

        // CPU usages offset & length (slot 0)
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(40, 8), cpuOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(48, 4), cpuLen);

        // Mem usages offset & length (slot 1)
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(52, 8), memOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(60, 4), memLen);

        // Entity IDs offset & length (slot 2)
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(64, 8), entityOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(72, 4), entityLen);

        // 7. Write to File
        Directory.CreateDirectory(outputDirectory);
        string fileName = Path.Combine(outputDirectory, $"chunk_{nextSequenceToRead}.glacier");

        using (var fs = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(header);
            fs.Write(buffers.CompressedTimestamps.AsSpan(0, tsLen));
            fs.Write(buffers.CompressedCpuUsages.AsSpan(0, cpuLen));
            fs.Write(buffers.CompressedMemUsages.AsSpan(0, memLen));
            fs.Write(buffers.CompressedEntityIds.AsSpan(0, entityLen));
        }

        // 8. Commit Read Progress to Ring Buffer
        long lastReadSequence = nextSequenceToRead + batchSize - 1;
        ringBuffer.CommitReadProgress(lastReadSequence);

        nextSequenceToRead += batchSize;
        return true;
    }
}
