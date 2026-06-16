using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using Glacier.Chrono.Compression;

namespace Glacier.Chrono.Query;

public class QueryBuffers
{
    public readonly float[] CpuUsages;
    public readonly int[] EntityIds;
    public readonly byte[] CompressedCpuBuffer;
    public readonly byte[] CompressedEntityIdBuffer;

    public QueryBuffers(int maxBatchSize)
    {
        CpuUsages = new float[maxBatchSize];
        EntityIds = new int[maxBatchSize];
        
        // Safe worst-case pre-allocated buffers (6 bytes/row for floats, 5 bytes/row for RLE ints)
        CompressedCpuBuffer = new byte[maxBatchSize * 6 + 64];
        CompressedEntityIdBuffer = new byte[maxBatchSize * 5 + 64];
    }
}

public static class QueryEngine
{
    /// <summary>
    /// Computes the average CPU usage for a specific Entity ID in the given chunk file using SIMD vectorized scans.
    /// Memory-maps the file and projects only the necessary columns (CPU usages and Entity IDs).
    /// </summary>
    public static double GetAverageCpuUsageForEntity(string chunkFilePath, int targetEntityId, QueryBuffers buffers)
    {
        using (var mmf = MemoryMappedFile.CreateFromFile(chunkFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        {
            // 1. Read metadata header (first 64 bytes)
            int rowCount;
            long cpuOffset, entityOffset;
            int cpuLen, entityLen;

            using (var accessor = mmf.CreateViewAccessor(0, 64, MemoryMappedFileAccess.Read))
            {
                byte[] header = new byte[64];
                accessor.ReadArray(0, header, 0, 64);

                // Verify magic bytes 'GLCH' (0x48434C47)
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
                if (magic != 0x48434C47)
                {
                    throw new InvalidDataException("Invalid file format: Magic bytes mismatch.");
                }

                rowCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
                cpuOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24, 8));
                cpuLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(32, 4));
                entityOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48, 8));
                entityLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(56, 4));
            }

            if (rowCount <= 0) return 0.0;
            if (rowCount > buffers.CpuUsages.Length)
            {
                throw new ArgumentException("Buffers size is smaller than the row count in chunk file.", nameof(buffers));
            }

            // 2. Project Column: Memory-Map only CPU Usages and Entity IDs columns
            // Copy directly into the pre-allocated buffers
            using (var cpuAccessor = mmf.CreateViewAccessor(cpuOffset, cpuLen, MemoryMappedFileAccess.Read))
            {
                cpuAccessor.ReadArray(0, buffers.CompressedCpuBuffer, 0, cpuLen);
            }

            using (var entityAccessor = mmf.CreateViewAccessor(entityOffset, entityLen, MemoryMappedFileAccess.Read))
            {
                entityAccessor.ReadArray(0, buffers.CompressedEntityIdBuffer, 0, entityLen);
            }

            // 3. Decompress columns into preallocated buffers
            Span<float> cpuUsages = buffers.CpuUsages.AsSpan(0, rowCount);
            Span<int> entityIds = buffers.EntityIds.AsSpan(0, rowCount);

            GorillaCompressor.Decompress(buffers.CompressedCpuBuffer.AsSpan(0, cpuLen), cpuUsages);
            IntegerRleCompressor.Decompress(buffers.CompressedEntityIdBuffer.AsSpan(0, entityLen), entityIds);

            // 4. SIMD Vectorized Filter Scan
            double sum = 0.0;
            int count = 0;

            int simdLength = Vector<float>.Count;
            int i = 0;

            if (Vector.IsHardwareAccelerated && rowCount >= simdLength)
            {
                var targetVec = new Vector<int>(targetEntityId);
                var zeroVec = Vector<float>.Zero;
                var oneIntVec = Vector<int>.One;
                var zeroIntVec = Vector<int>.Zero;

                var sumAccumulator = Vector<float>.Zero;
                var countAccumulator = Vector<int>.Zero;

                for (; i <= rowCount - simdLength; i += simdLength)
                {
                    var entityVec = new Vector<int>(entityIds.Slice(i, simdLength));
                    var cpuVec = new Vector<float>(cpuUsages.Slice(i, simdLength));

                    Vector<int> maskInt = Vector.Equals(entityVec, targetVec);
                    Vector<float> maskFloat = Vector.AsVectorSingle(maskInt);

                    Vector<float> selectedCpu = Vector.ConditionalSelect(maskFloat, cpuVec, zeroVec);
                    sumAccumulator += selectedCpu;

                    Vector<int> selectedCount = Vector.ConditionalSelect(maskInt, oneIntVec, zeroIntVec);
                    countAccumulator += selectedCount;
                }

                for (int j = 0; j < simdLength; j++)
                {
                    sum += sumAccumulator[j];
                    count += countAccumulator[j];
                }
            }

            // Process remainder
            for (; i < rowCount; i++)
            {
                if (entityIds[i] == targetEntityId)
                {
                    sum += cpuUsages[i];
                    count++;
                }
            }

            return count == 0 ? 0.0 : sum / count;
        }
    }
}
