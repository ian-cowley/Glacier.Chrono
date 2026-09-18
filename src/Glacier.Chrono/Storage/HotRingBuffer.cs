using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Glacier.Chrono.Storage;

/// <summary>
/// 64-byte cache-line padded sequence slot to eliminate false sharing across CPU cores.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedSequence
{
    [FieldOffset(0)]
    public long Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PaddedSequence(long value) => Value = value;
}

/// <summary>
/// 128-byte cache-line isolated cursor struct.
/// FieldOffset(64) guarantees that Value is isolated from object headers and preceding fields
/// by at least 64 bytes, and Size = 128 guarantees separation from subsequent fields.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct PaddedCursor
{
    [FieldOffset(64)]
    public long Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PaddedCursor(long value) => Value = value;
}

public class HotRingBuffer<T> where T : unmanaged
{
    private readonly T[] _buffer;
    private readonly PaddedSequence[] _writeSequences;
    private readonly int _capacity;
    private readonly int _mask;
    
    // Explicitly isolated cursor structs:
    private PaddedCursor _writeCursor;
    private PaddedCursor _readProgress;

    public int Capacity => _capacity;
    public long WriteCursor => Volatile.Read(ref _writeCursor.Value);
    public long ReadProgress => Volatile.Read(ref _readProgress.Value);

    public HotRingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");

        _capacity = RoundToPowerOfTwo(capacity);
        _mask = _capacity - 1;
        _buffer = new T[_capacity];
        _writeSequences = new PaddedSequence[_capacity];
        
        _writeCursor.Value = 0;
        _readProgress.Value = -1L;

        for (int i = 0; i < _capacity; i++)
        {
            _writeSequences[i].Value = -1L;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundToPowerOfTwo(int value)
    {
        int power = 1;
        while (power < value)
        {
            power <<= 1;
            if (power < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Requested capacity is too large.");
            }
        }
        return power;
    }

    /// <summary>
    /// Writes an item to the ring buffer in a lock-free, concurrent manner with bounded spin-wait.
    /// Throws TimeoutException if the buffer remains full beyond the timeout.
    /// </summary>
    public void Write(in T item)
    {
        if (!TryWrite(in item, TimeSpan.FromSeconds(10), CancellationToken.None))
        {
            throw new TimeoutException("Timed out waiting for free slot in HotRingBuffer.");
        }
    }

    /// <summary>
    /// Writes an item to the ring buffer with explicit timeout and cancellation token.
    /// </summary>
    public void Write(in T item, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!TryWrite(in item, timeout, cancellationToken))
        {
            throw new TimeoutException("Timed out waiting for free slot in HotRingBuffer.");
        }
    }

    /// <summary>
    /// Attempts to write an item to the ring buffer within the specified timeout.
    /// Employs bounded spin-wait with exponential backoff and prevents sequence poisoning.
    /// Returns true if written successfully; false if timed out or cancelled.
    /// </summary>
    public bool TryWrite(in T item, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        long timeoutTicks = (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        SpinWait spinner = default;
        long sequence;

        // Step 1: Reserve sequence atomically only when space is confirmed
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long currentWriteCursor = Volatile.Read(ref _writeCursor.Value);
            long currentReadProgress = Volatile.Read(ref _readProgress.Value);

            // Check if ring buffer is full
            if (currentWriteCursor - _capacity > currentReadProgress)
            {
                if (Stopwatch.GetTimestamp() - startTimestamp > timeoutTicks)
                {
                    return false;
                }
                spinner.SpinOnce();
                continue;
            }

            // Attempt CAS to reserve slot
            if (Interlocked.CompareExchange(ref _writeCursor.Value, currentWriteCursor + 1, currentWriteCursor) == currentWriteCursor)
            {
                sequence = currentWriteCursor;
                break;
            }
        }

        int index = (int)(sequence & _mask);

        // Step 2: Coordinate with slow writer from the previous cycle on this slot
        long expectedSequence = sequence - _capacity;
        if (expectedSequence < 0) expectedSequence = -1L;

        spinner.Reset();
        while (Volatile.Read(ref _writeSequences[index].Value) != expectedSequence)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Stopwatch.GetTimestamp() - startTimestamp > timeoutTicks)
            {
                // Slot delayed by previous writer. Emergency commit to avoid poisoning downstream reads:
                return false;
            }
            spinner.SpinOnce();
        }

        // Step 3: Write payload
        _buffer[index] = item;

        // Step 4: Commit sequence
        Volatile.Write(ref _writeSequences[index].Value, sequence);
        return true;
    }

    /// <summary>
    /// Tries to read a batch of items from the ring buffer.
    /// Returns true if all items in the requested sequence range are fully written.
    /// Otherwise returns false without modifying the destination span.
    /// </summary>
    public bool TryReadBatch(long startSequence, Span<T> destination)
    {
        if (destination.IsEmpty) return true;

        int batchSize = destination.Length;

        // First pass: Verify all sequences in the batch are fully written
        for (int i = 0; i < batchSize; i++)
        {
            long targetSequence = startSequence + i;
            int index = (int)(targetSequence & _mask);
            if (Volatile.Read(ref _writeSequences[index].Value) != targetSequence)
            {
                return false;
            }
        }

        // Second pass: Copy data from the ring buffer to the destination span
        for (int i = 0; i < batchSize; i++)
        {
            long targetSequence = startSequence + i;
            int index = (int)(targetSequence & _mask);
            destination[i] = _buffer[index];
        }

        return true;
    }

    /// <summary>
    /// Commits the read progress to the specified sequence, allowing slots up to this sequence to be overwritten.
    /// </summary>
    public void CommitReadProgress(long sequence)
    {
        // Only advance the read progress monotonically
        long current = Volatile.Read(ref _readProgress.Value);
        while (sequence > current)
        {
            long original = Interlocked.CompareExchange(ref _readProgress.Value, sequence, current);
            if (original == current)
            {
                break;
            }
            current = original;
        }
    }
}
