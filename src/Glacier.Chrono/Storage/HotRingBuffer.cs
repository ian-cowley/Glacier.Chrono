using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Glacier.Chrono.Storage;

public class HotRingBuffer<T> where T : unmanaged
{
    private readonly T[] _buffer;
    private readonly long[] _writeSequences;
    private readonly int _capacity;
    private readonly int _mask;
    
    private long _writeCursor = 0;
    private long _readProgress = -1L;

    public int Capacity => _capacity;
    public long WriteCursor => Volatile.Read(ref _writeCursor);
    public long ReadProgress => Volatile.Read(ref _readProgress);

    public HotRingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");

        _capacity = RoundToPowerOfTwo(capacity);
        _mask = _capacity - 1;
        _buffer = new T[_capacity];
        _writeSequences = new long[_capacity];
        
        Array.Fill(_writeSequences, -1L);
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
    /// Writes an item to the ring buffer in a lock-free, concurrent manner.
    /// Blocks (spin-waits) if the buffer is full (i.e. writers wrapping around unread slots)
    /// or if the target slot is currently being written to by a slow thread.
    /// </summary>
    public void Write(in T item)
    {
        long sequence = Interlocked.Increment(ref _writeCursor) - 1;
        int index = (int)(sequence & _mask);

        // 1. Coordinate with reader: prevent overwriting unread slots
        while (sequence - _capacity > Volatile.Read(ref _readProgress))
        {
            Thread.SpinWait(1);
        }

        // 2. Coordinate with concurrent writers: wait for slot to finish its previous cycle's write
        long expectedSequence = sequence - _capacity;
        if (expectedSequence < 0) expectedSequence = -1L;

        while (Volatile.Read(ref _writeSequences[index]) != expectedSequence)
        {
            Thread.SpinWait(1);
        }

        _buffer[index] = item;

        // Commit the write by setting the sequence flag to the current sequence
        Volatile.Write(ref _writeSequences[index], sequence);
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
            if (Volatile.Read(ref _writeSequences[index]) != targetSequence)
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
        long current = Volatile.Read(ref _readProgress);
        while (sequence > current)
        {
            long original = Interlocked.CompareExchange(ref _readProgress, sequence, current);
            if (original == current)
            {
                break;
            }
            current = original;
        }
    }
}
