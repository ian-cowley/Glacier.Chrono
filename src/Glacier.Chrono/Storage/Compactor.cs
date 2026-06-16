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

        FileHeaderBuffer = new byte[64];
    }
}

public static class Compactor
{
    /// <summary>
    /// Compacts a batch of rows from the HotRingBuffer, converting them to compressed columnar format,
    /// and writing them as a single .glacier chunk file to disk.
    /// Returns true if a batch was successfully compacted; false if there was insufficient data.
    /// </summary>
    public static bool CompactBatch(
        HotRingBuffer<TelemetryRow> ringBuffer,
        ref long nextSequenceToRead,
        int batchSize,
        string outputDirectory,
        CompactorBuffers buffers)
    {
        // 1. Try to read a complete batch from the ring buffer
        if (!ringBuffer.TryReadBatch(nextSequenceToRead, buffers.RowBuffer))
        {
            return false;
        }

        // 2. Sort the batch by Timestamp (zero-allocation using struct IComparable)
        Array.Sort(buffers.RowBuffer, 0, batchSize);

        // 3. Matrix Transpose (AoS to SoA)
        for (int i = 0; i < batchSize; i++)
        {
            buffers.Timestamps[i] = buffers.RowBuffer[i].Timestamp;
            buffers.CpuUsages[i] = buffers.RowBuffer[i].CpuUsage;
            buffers.MemUsages[i] = buffers.RowBuffer[i].MemUsage;
            buffers.EntityIds[i] = buffers.RowBuffer[i].EntityId;
        }

        // 4. Compress each column
        int tsLen = TimestampCompressor.Compress(buffers.Timestamps, buffers.CompressedTimestamps);
        int cpuLen = GorillaCompressor.Compress(buffers.CpuUsages, buffers.CompressedCpuUsages);
        int memLen = GorillaCompressor.Compress(buffers.MemUsages, buffers.CompressedMemUsages);
        int entityLen = IntegerRleCompressor.Compress(buffers.EntityIds, buffers.CompressedEntityIds);

        // 5. Calculate Column Offsets
        long tsOffset = 64;
        long cpuOffset = tsOffset + tsLen;
        long memOffset = cpuOffset + cpuLen;
        long entityOffset = memOffset + memLen;

        // 6. Populate 64-byte Header
        Span<byte> header = buffers.FileHeaderBuffer;
        header.Clear(); // Ensure empty bytes for padding

        // Magic bytes: 'GLCH' -> 0x48434C47
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, 4), 0x48434C47);
        // Version: 1
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), 1);
        // Row count
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), batchSize);

        // Timestamps offset & length
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(12, 8), tsOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(20, 4), tsLen);

        // CPU usages offset & length
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(24, 8), cpuOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(32, 4), cpuLen);

        // Mem usages offset & length
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(36, 8), memOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(44, 4), memLen);

        // Entity IDs offset & length
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(48, 8), entityOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(56, 4), entityLen);

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
