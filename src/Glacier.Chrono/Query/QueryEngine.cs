using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using Glacier.Chrono.Compression;
using Glacier.Chrono.Storage;
using Microsoft.Win32.SafeHandles;

namespace Glacier.Chrono.Query;

public class QueryBuffers
{
    public readonly long[] Timestamps;
    public readonly float[] CpuUsages;
    public readonly float[] MemUsages;
    public readonly int[] EntityIds;

    public readonly byte[] CompressedTimestampBuffer;
    public readonly byte[] CompressedCpuBuffer;
    public readonly byte[] CompressedMemBuffer;
    public readonly byte[] CompressedEntityIdBuffer;

    public QueryBuffers(int maxBatchSize)
    {
        if (maxBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize), "Max batch size must be greater than zero.");
        }

        Timestamps = new long[maxBatchSize];
        CpuUsages = new float[maxBatchSize];
        MemUsages = new float[maxBatchSize];
        EntityIds = new int[maxBatchSize];

        // Safe worst-case pre-allocated buffers (9B/row DoD, 6B/row Gorilla floats, 5B/row RLE ints)
        CompressedTimestampBuffer = new byte[maxBatchSize * 9 + 64];
        CompressedCpuBuffer = new byte[maxBatchSize * 6 + 64];
        CompressedMemBuffer = new byte[maxBatchSize * 6 + 64];
        CompressedEntityIdBuffer = new byte[maxBatchSize * 5 + 64];
    }
}

public static class QueryEngine
{
    public const int HeaderSize = Compactor.HeaderSize;

    private const int CpuDescriptorPos = 40;
    private const int MemDescriptorPos = 52;
    private const int EntityDescriptorPos = 64;

    /// <summary>
    /// Reads the O(1) header metadata and [MinTimestamp, MaxTimestamp] zone-map bounds from a .glacier chunk file
    /// using stack-allocated memory without touching any compressed column pages.
    /// </summary>
    public static (int RowCount, long MinTimestamp, long MaxTimestamp) ReadChunkZoneMap(string chunkFilePath)
    {
        using SafeFileHandle handle = File.OpenHandle(chunkFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[HeaderSize];
        ValidateAndReadHeader(handle, header, out int rowCount, out long minTimestamp, out long maxTimestamp);
        return (rowCount, minTimestamp, maxTimestamp);
    }

    public static double GetAverageCpuUsageForEntity(string chunkFilePath, int targetEntityId, QueryBuffers buffers)
    {
        return GetAverageCpuUsageForEntity(chunkFilePath, targetEntityId, long.MinValue, long.MaxValue, buffers);
    }

    public static double GetAverageCpuUsageForEntity(
        string chunkFilePath,
        int targetEntityId,
        long startTimestamp,
        long endTimestamp,
        QueryBuffers buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        var (sum, count) = ScanChunk(
            chunkFilePath,
            CpuDescriptorPos,
            buffers.CpuUsages,
            buffers.CompressedCpuBuffer,
            hasCategoryFilter: true,
            targetEntityId,
            buffers,
            startTimestamp,
            endTimestamp);
        return count == 0 ? 0.0 : sum / count;
    }

    public static double GetAverageCpuUsageForEntityInDirectory(
        string chunksDirectory,
        int targetEntityId,
        long startTimestamp,
        long endTimestamp,
        QueryBuffers buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        double totalSum = 0.0;
        long totalCount = 0;

        foreach (string chunkFile in Directory.EnumerateFiles(chunksDirectory, "*.glacier"))
        {
            var (sum, count) = ScanChunk(
                chunkFile,
                CpuDescriptorPos,
                buffers.CpuUsages,
                buffers.CompressedCpuBuffer,
                hasCategoryFilter: true,
                targetEntityId,
                buffers,
                startTimestamp,
                endTimestamp);
            totalSum += sum;
            totalCount += count;
        }

        return totalCount == 0 ? 0.0 : totalSum / totalCount;
    }

    public static double GetAverageCpuUsage(string chunkFilePath, QueryBuffers buffers)
    {
        return GetAverageCpuUsage(chunkFilePath, long.MinValue, long.MaxValue, buffers);
    }

    public static double GetAverageCpuUsage(
        string chunkFilePath,
        long startTimestamp,
        long endTimestamp,
        QueryBuffers buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        var (sum, count) = ScanChunk(
            chunkFilePath,
            CpuDescriptorPos,
            buffers.CpuUsages,
            buffers.CompressedCpuBuffer,
            hasCategoryFilter: false,
            targetCategory: 0,
            buffers,
            startTimestamp,
            endTimestamp);
        return count == 0 ? 0.0 : sum / count;
    }

    public static double GetAverageCpuUsageInDirectory(
        string chunksDirectory,
        long startTimestamp,
        long endTimestamp,
        QueryBuffers buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        double totalSum = 0.0;
        long totalCount = 0;

        foreach (string chunkFile in Directory.EnumerateFiles(chunksDirectory, "*.glacier"))
        {
            var (sum, count) = ScanChunk(
                chunkFile,
                CpuDescriptorPos,
                buffers.CpuUsages,
                buffers.CompressedCpuBuffer,
                hasCategoryFilter: false,
                targetCategory: 0,
                buffers,
                startTimestamp,
                endTimestamp);
            totalSum += sum;
            totalCount += count;
        }

        return totalCount == 0 ? 0.0 : totalSum / totalCount;
    }

    public static double GetAverageMemUsageForEntity(string chunkFilePath, int targetEntityId, QueryBuffers buffers)
    {
        return GetAverageMemUsageForEntity(chunkFilePath, targetEntityId, long.MinValue, long.MaxValue, buffers);
    }

    public static double GetAverageMemUsageForEntity(
        string chunkFilePath,
        int targetEntityId,
        long startTimestamp,
        long endTimestamp,
        QueryBuffers buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        var (sum, count) = ScanChunk(
            chunkFilePath,
            MemDescriptorPos,
            buffers.MemUsages,
            buffers.CompressedMemBuffer,
            hasCategoryFilter: true,
            targetEntityId,
            buffers,
            startTimestamp,
            endTimestamp);
        return count == 0 ? 0.0 : sum / count;
    }

    public static double GetAverageMemUsageForEntityInDirectory(
        string chunksDirectory,
        int targetEntityId,
        long startTimestamp,
        long endTimestamp,
        QueryBuffers buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        double totalSum = 0.0;
        long totalCount = 0;

        foreach (string chunkFile in Directory.EnumerateFiles(chunksDirectory, "*.glacier"))
        {
            var (sum, count) = ScanChunk(
                chunkFile,
                MemDescriptorPos,
                buffers.MemUsages,
                buffers.CompressedMemBuffer,
                hasCategoryFilter: true,
                targetEntityId,
                buffers,
                startTimestamp,
                endTimestamp);
            totalSum += sum;
            totalCount += count;
        }

        return totalCount == 0 ? 0.0 : totalSum / totalCount;
    }

    public static double GetAverageMemUsage(string chunkFilePath, QueryBuffers buffers)
    {
        return GetAverageMemUsage(chunkFilePath, long.MinValue, long.MaxValue, buffers);
    }

    public static double GetAverageMemUsage(
        string chunkFilePath,
        long startTimestamp,
        long endTimestamp,
        QueryBuffers buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        var (sum, count) = ScanChunk(
            chunkFilePath,
            MemDescriptorPos,
            buffers.MemUsages,
            buffers.CompressedMemBuffer,
            hasCategoryFilter: false,
            targetCategory: 0,
            buffers,
            startTimestamp,
            endTimestamp);
        return count == 0 ? 0.0 : sum / count;
    }

    public static double GetAverageMemUsageInDirectory(
        string chunksDirectory,
        long startTimestamp,
        long endTimestamp,
        QueryBuffers buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        double totalSum = 0.0;
        long totalCount = 0;

        foreach (string chunkFile in Directory.EnumerateFiles(chunksDirectory, "*.glacier"))
        {
            var (sum, count) = ScanChunk(
                chunkFile,
                MemDescriptorPos,
                buffers.MemUsages,
                buffers.CompressedMemBuffer,
                hasCategoryFilter: false,
                targetCategory: 0,
                buffers,
                startTimestamp,
                endTimestamp);
            totalSum += sum;
            totalCount += count;
        }

        return totalCount == 0 ? 0.0 : totalSum / totalCount;
    }

    private static void ValidateAndReadHeader(
        SafeFileHandle handle,
        Span<byte> header,
        out int rowCount,
        out long minTimestamp,
        out long maxTimestamp)
    {
        int bytesRead = RandomAccess.Read(handle, header, 0);
        if (bytesRead < 12)
        {
            throw new InvalidDataException("Invalid file format: Truncated header.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4));
        if (magic != Compactor.Magic)
        {
            throw new InvalidDataException("Invalid file format: Magic bytes mismatch.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
        if (version != Compactor.FormatVersion || bytesRead < HeaderSize)
        {
            throw new InvalidDataException($"Unsupported or truncated .glacier header version: {version}.");
        }

        rowCount = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        minTimestamp = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(12, 8));
        maxTimestamp = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(20, 8));
    }

    private static (double Sum, long Count) ScanChunk(
        string chunkFilePath,
        int metricDescriptorPos,
        float[] metricArray,
        byte[] compressedMetricBuffer,
        bool hasCategoryFilter,
        int targetCategory,
        QueryBuffers buffers,
        long startTimestamp,
        long endTimestamp)
    {
        using SafeFileHandle handle = File.OpenHandle(chunkFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Span<byte> header = stackalloc byte[HeaderSize];
        ValidateAndReadHeader(handle, header, out int rowCount, out long minTimestamp, out long maxTimestamp);

        if (rowCount <= 0 || startTimestamp > endTimestamp)
        {
            return (0.0, 0);
        }

        // O(1) Zone-Map Pruning: skip chunk completely without memory-mapping if disjoint from [startTimestamp, endTimestamp]
        if (maxTimestamp < startTimestamp || minTimestamp > endTimestamp)
        {
            return (0.0, 0);
        }

        if (rowCount > metricArray.Length ||
            rowCount > buffers.Timestamps.Length ||
            (hasCategoryFilter && rowCount > buffers.EntityIds.Length))
        {
            throw new ArgumentException("Buffers size is smaller than the row count in chunk file.", nameof(buffers));
        }

        long tsOffset = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(28, 8));
        int tsLen = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(36, 4));
        long metricOffset = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(metricDescriptorPos, 8));
        int metricLen = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(metricDescriptorPos + 8, 4));
        long entityOffset = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(EntityDescriptorPos, 8));
        int entityLen = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(EntityDescriptorPos + 8, 4));

        using var mmf = MemoryMappedFile.CreateFromFile(
            handle,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read,
            HandleInheritability.None,
            leaveOpen: true);

        int startIndex = 0;
        int endIndex = rowCount;

        // Skip timestamp column I/O & decompression when [minTimestamp, maxTimestamp] lies wholly inside [startTimestamp, endTimestamp]
        bool whollyContained = minTimestamp >= startTimestamp && maxTimestamp <= endTimestamp;
        if (!whollyContained)
        {
            if ((uint)tsLen > (uint)buffers.CompressedTimestampBuffer.Length)
            {
                throw new InvalidDataException("Invalid compressed timestamp length in header.");
            }

            using (var tsAccessor = mmf.CreateViewAccessor(tsOffset, tsLen, MemoryMappedFileAccess.Read))
            {
                tsAccessor.ReadArray(0, buffers.CompressedTimestampBuffer, 0, tsLen);
            }

            Span<long> timestamps = buffers.Timestamps.AsSpan(0, rowCount);
            TimestampCompressor.Decompress(buffers.CompressedTimestampBuffer.AsSpan(0, tsLen), timestamps);

            startIndex = LowerBound(timestamps, startTimestamp);
            endIndex = UpperBound(timestamps, endTimestamp);
            if (endIndex <= startIndex)
            {
                return (0.0, 0);
            }
        }

        if ((uint)metricLen > (uint)compressedMetricBuffer.Length)
        {
            throw new InvalidDataException("Invalid compressed metric length in header.");
        }

        using (var metricAccessor = mmf.CreateViewAccessor(metricOffset, metricLen, MemoryMappedFileAccess.Read))
        {
            metricAccessor.ReadArray(0, compressedMetricBuffer, 0, metricLen);
        }

        Span<float> metricSpan = metricArray.AsSpan(0, rowCount);
        GorillaCompressor.Decompress(compressedMetricBuffer.AsSpan(0, metricLen), metricSpan);
        ReadOnlySpan<float> metricSlice = metricSpan.Slice(startIndex, endIndex - startIndex);

        if (!hasCategoryFilter)
        {
            return AggregateUnfilteredSimd(metricSlice);
        }

        if ((uint)entityLen > (uint)buffers.CompressedEntityIdBuffer.Length)
        {
            throw new InvalidDataException("Invalid compressed category length in header.");
        }

        using (var entityAccessor = mmf.CreateViewAccessor(entityOffset, entityLen, MemoryMappedFileAccess.Read))
        {
            entityAccessor.ReadArray(0, buffers.CompressedEntityIdBuffer, 0, entityLen);
        }

        Span<int> entitySpan = buffers.EntityIds.AsSpan(0, rowCount);
        IntegerRleCompressor.Decompress(buffers.CompressedEntityIdBuffer.AsSpan(0, entityLen), entitySpan);
        ReadOnlySpan<int> entitySlice = entitySpan.Slice(startIndex, endIndex - startIndex);

        return AggregateFilteredSimd(metricSlice, entitySlice, targetCategory);
    }

    private static (double Sum, long Count) AggregateUnfilteredSimd(ReadOnlySpan<float> metricSlice)
    {
        int len = metricSlice.Length;
        if (len == 0) return (0.0, 0);

        double sum = 0.0;
        int simdLength = Vector<float>.Count;
        int i = 0;

        if (Vector.IsHardwareAccelerated && len >= simdLength)
        {
            var sumLow = Vector<double>.Zero;
            var sumHigh = Vector<double>.Zero;

            for (; i <= len - simdLength; i += simdLength)
            {
                var metricVec = new Vector<float>(metricSlice.Slice(i, simdLength));
                Vector.Widen(metricVec, out Vector<double> low, out Vector<double> high);
                sumLow += low;
                sumHigh += high;
            }

            sum += Vector.Sum(sumLow) + Vector.Sum(sumHigh);
        }

        for (; i < len; i++)
        {
            sum += metricSlice[i];
        }

        return (sum, len);
    }

    private static (double Sum, long Count) AggregateFilteredSimd(
        ReadOnlySpan<float> metricSlice,
        ReadOnlySpan<int> catSlice,
        int targetCategory)
    {
        int len = metricSlice.Length;
        if (len == 0) return (0.0, 0);

        double sum = 0.0;
        long count = 0;
        int simdLength = Vector<float>.Count;
        int i = 0;

        if (Vector.IsHardwareAccelerated && len >= simdLength)
        {
            var targetVec = new Vector<int>(targetCategory);
            var zeroVec = Vector<float>.Zero;
            var oneIntVec = Vector<int>.One;
            var zeroIntVec = Vector<int>.Zero;

            var sumLow = Vector<double>.Zero;
            var sumHigh = Vector<double>.Zero;
            var countAccumulator = Vector<int>.Zero;

            for (; i <= len - simdLength; i += simdLength)
            {
                var catVec = new Vector<int>(catSlice.Slice(i, simdLength));
                var metricVec = new Vector<float>(metricSlice.Slice(i, simdLength));

                Vector<int> maskInt = Vector.Equals(catVec, targetVec);
                Vector<float> maskFloat = Vector.AsVectorSingle(maskInt);

                Vector<float> selectedMetric = Vector.ConditionalSelect(maskFloat, metricVec, zeroVec);
                Vector.Widen(selectedMetric, out Vector<double> low, out Vector<double> high);
                sumLow += low;
                sumHigh += high;

                Vector<int> selectedCount = Vector.ConditionalSelect(maskInt, oneIntVec, zeroIntVec);
                countAccumulator += selectedCount;
            }

            sum += Vector.Sum(sumLow) + Vector.Sum(sumHigh);
            count += Vector.Sum(countAccumulator);
        }

        for (; i < len; i++)
        {
            if (catSlice[i] == targetCategory)
            {
                sum += metricSlice[i];
                count++;
            }
        }

        return (sum, count);
    }

    private static int LowerBound(ReadOnlySpan<long> span, long value)
    {
        int lo = 0;
        int hi = span.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (span[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int UpperBound(ReadOnlySpan<long> span, long value)
    {
        int lo = 0;
        int hi = span.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (span[mid] <= value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
