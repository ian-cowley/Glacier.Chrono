using System;

namespace Glacier.Chrono.Compression;

public ref struct BitWriter
{
    private readonly Span<byte> _destination;
    private int _byteIndex;
    private ulong _accumulator;
    private int _bitCount; // Number of bits in the accumulator (0 to 7)

    public int BytesWritten => _byteIndex + (_bitCount > 0 ? 1 : 0);
    public int BitsWritten => _byteIndex * 8 + _bitCount;

    public BitWriter(Span<byte> destination)
    {
        _destination = destination;
        _byteIndex = 0;
        _accumulator = 0;
        _bitCount = 0;
    }

    public void WriteBit(int bit)
    {
        WriteBits((ulong)(bit & 1), 1);
    }

    public void WriteBits(ulong value, int count)
    {
        if (count == 0) return;

        // Mask value to ensure no junk bits above the requested count
        ulong mask = count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
        value &= mask;

        _accumulator |= value << (64 - _bitCount - count);
        _bitCount += count;

        while (_bitCount >= 8)
        {
            if (_byteIndex >= _destination.Length)
            {
                throw new InvalidOperationException("Buffer overflow: Destination span is too small.");
            }
            _destination[_byteIndex++] = (byte)(_accumulator >> 56);
            _accumulator <<= 8;
            _bitCount -= 8;
        }
    }

    public void Flush()
    {
        if (_bitCount > 0)
        {
            if (_byteIndex >= _destination.Length)
            {
                throw new InvalidOperationException("Buffer overflow: Destination span is too small during flush.");
            }
            _destination[_byteIndex++] = (byte)(_accumulator >> 56);
            _accumulator = 0;
            _bitCount = 0;
        }
    }
}

public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _source;
    private int _byteIndex;
    private ulong _accumulator;
    private int _bitsLeft; // Number of bits remaining in the accumulator

    public int BitsRead => _byteIndex * 8 - _bitsLeft;

    public BitReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        _byteIndex = 0;
        _accumulator = 0;
        _bitsLeft = 0;
    }

    private void Refill()
    {
        int bytesToLoad = Math.Min(8, _source.Length - _byteIndex);
        if (bytesToLoad == 0) return;

        ulong val = 0;
        for (int i = 0; i < bytesToLoad; i++)
        {
            val = (val << 8) | _source[_byteIndex + i];
        }
        _byteIndex += bytesToLoad;

        _accumulator = val << (64 - bytesToLoad * 8);
        _bitsLeft = bytesToLoad * 8;
    }

    public int ReadBit()
    {
        return (int)ReadBits(1);
    }

    public ulong ReadBits(int count)
    {
        if (count == 0) return 0;

        ulong result = 0;
        int remaining = count;

        while (remaining > 0)
        {
            if (_bitsLeft == 0)
            {
                Refill();
                if (_bitsLeft == 0)
                {
                    throw new InvalidOperationException("Read overflow: Attempted to read past the end of the source span.");
                }
            }

            int bitsToTake = Math.Min(remaining, _bitsLeft);
            
            // Extract the top 'bitsToTake' from the accumulator
            ulong val = _accumulator >> (64 - bitsToTake);
            result = (result << bitsToTake) | val;

            _accumulator <<= bitsToTake;
            _bitsLeft -= bitsToTake;
            remaining -= bitsToTake;
        }

        return result;
    }
}
