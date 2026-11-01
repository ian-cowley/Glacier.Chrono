# Glacier.Chrono Architecture

`Glacier.Chrono` is a Native AOT-compatible, zero-allocation embedded time-series storage and query engine for **.NET 10**. It combines a lock-free row-oriented ingestion buffer (Array of Structs) with background columnar compaction (Struct of Arrays), domain-specific bit-level codecs, zone-map metadata pruning, and hardware-accelerated SIMD scans over memory-mapped chunk files.

---

## 1. Ingestion Layer: `HotRingBuffer<T>`

`HotRingBuffer<T>` (`where T : unmanaged`) provides multi-producer / batch-consumer lock-free ingestion without heap allocations:

- **False-Sharing Elimination**:
  - `PaddedCursor` (`Size = 128`, `[FieldOffset(64)]`) isolates `_writeCursor` and `_readProgress` from adjacent object headers and fields across 64-byte and 128-byte spatial prefetcher boundaries.
  - `PaddedSequence` (`Size = 64`, `[FieldOffset(0)]`) pads every per-slot commit sequence entry to its own cache line.
- **Poison-Free Slot Reservation**:
  - Before attempting `Interlocked.CompareExchange` on `_writeCursor`, `TryWrite` verifies **both**:
    1. Free buffer capacity (`currentWriteCursor - _capacity <= currentReadProgress`), and
    2. Prior-lap slot completion (`Volatile.Read(ref _writeSequences[index].Value) == expectedSequence`).
  - Timeout (`return false`) and cancellation (`cancellationToken.ThrowIfCancellationRequested()`) occur **strictly before** `_writeCursor` is incremented. Once the CAS wins a slot, the payload is written and published immediately (`Volatile.Write`), guaranteeing that neither timeouts nor cancellations can ever orphan a sequence or deadlock downstream readers.
- **Batch Reads**:
  - `TryReadBatch(long startSequence, Span<T> destination)` validates `destination.Length <= Capacity`, verifies in a first pass that every sequence in `[startSequence, startSequence + batchSize)` is published, and copies payloads in a second pass. `CommitReadProgress` advances `_readProgress` monotonically via CAS.

---

## 2. Background Pivot & `.glacier` v2 Chunk Format

During compaction (`Compactor.CompactBatch` or source-generated `{Struct}Compactor.CompactBatch`):

1. **Partial-Batch Safe Slicing**: Reads `buffers.RowBuffer.AsSpan(0, batchSize)` (`1 <= batchSize <= buffers.RowBuffer.Length`).
2. **In-Place Timestamp Sort**: Sorts the `[0..batchSize)` slice ascending by timestamp with zero heap allocations, establishing exact monotonic timestamp order (`MinTimestamp = Timestamps[0]`, `MaxTimestamp = Timestamps[batchSize - 1]`).
3. **AoS-to-SoA Transpose**: Pivots row fields into contiguous typed column slices (`Timestamps`, `float` metric arrays, `int` category arrays).
4. **Columnar Bit-Level Compression**: Compresses each `[0..batchSize)` column slice into pre-allocated worst-case buffers.
5. **Dynamic 64-Byte-Aligned v2 Header**:
   Let $K = |\text{Metrics}| + |\text{Categories}|$ be the number of non-timestamp columns. The raw header size is $40 + 12K$ bytes, rounded up to a 64-byte cache/page alignment boundary:

   $$\text{HeaderSize} = (40 + 12K + 63) \ \& \ \sim 63$$

   | Byte Range | Type | Field | Description |
   | :--- | :--- | :--- | :--- |
   | `0..4` | `uint32` | `Magic` | `0x48434C47` (`'GLCH'` little-endian) |
   | `4..8` | `uint32` | `FormatVersion` | `2` |
   | `8..12` | `int32` | `RowCount` | Number of rows stored in chunk |
   | `12..20` | `int64` | `MinTimestamp` | Minimum timestamp in chunk (zone-map lower bound) |
   | `20..28` | `int64` | `MaxTimestamp` | Maximum timestamp in chunk (zone-map upper bound) |
   | `28..36` | `int64` | `TimestampOffset` | Byte offset of compressed timestamp column (`== HeaderSize`) |
   | `36..40` | `int32` | `TimestampLength` | Compressed byte length of timestamp column |
   | `40 + 12i .. 48 + 12i` | `int64` | `ColOffset[i]` | Byte offset of metric/category column $i$ ($0 \le i < K$) |
   | `48 + 12i .. 52 + 12i` | `int32` | `ColLength[i]` | Compressed byte length of metric/category column $i$ |
   | `40 + 12K .. HeaderSize` | `byte[]` | `Padding` | Zero-filled alignment padding |

---

## 3. Compression Codecs

- **`BitWriter` / `BitReader` (`ref struct`)**:
  - 64-bit `ulong` register accumulator supporting any bit width `0..64` at **any** bit alignment (`0..7`).
  - When `count > 64 - _bitCount`, `BitWriter.WriteBits` splits cleanly across the 64-bit boundary, flushing 8 big-endian bytes via `BinaryPrimitives.WriteUInt64BigEndian` and retaining the remaining `1..7` bits without shift overflow or truncation.
  - `BitReader.ReadBits` guards `bitsToTake == 64` to avoid C#'s `(x << 64) == (x << (64 & 63))` shift masking semantics.
- **`TimestampCompressor` (Delta-of-Delta)**:
  - Encodes `t0` (64b), `d0 = t1 - t0` (64b), and variable-length prefix buckets (`'0'` -> 1b, `'10'` -> 9b, `'110'` -> 12b, `'1110'` -> 16b, `'1111'` -> 68b) using explicit `unchecked` two's-complement modular arithmetic so arbitrary `int64` ranges round-trip without overflow.
- **`GorillaCompressor` (IEEE-754 XOR)**:
  - Stores raw 32-bit IEEE-754 bit patterns (`BitConverter.SingleToUInt32Bits`) preserving `-0.0f`, subnormals, infinities, and distinct `NaN` payloads bit-for-bit, while validating window bounds (`hasPrevWindow`, `len > 0`, `lz + len <= 32`) on decompression to reject corrupt streams with `InvalidDataException`.
- **`IntegerRleCompressor`**:
  - Run-length encodes low-cardinality category/entity identifiers (`[flag=0, value]` for singletons; `[flag=1, value, runLength]` for runs) with bounds-checked `Span<int>.Fill` decoding.

---

## 4. Query Execution & Zone-Map Pruning

Both `QueryEngine` and source-generated `{Struct}QueryEngine` execute a three-tier pruning and projection pipeline:

1. **Stack-Allocated Header Read & O(1) Zone-Map Pruning**:
   - Reads the fixed `HeaderSize` bytes into `Span<byte> header = stackalloc byte[HeaderSize]` via `RandomAccess.Read` (zero `byte[]` heap allocation).
   - If `maxTimestamp < startTimestamp || minTimestamp > endTimestamp`, the chunk is pruned immediately in $O(1)$ without creating a `MemoryMappedFile` or reading any column bytes.
2. **Full-Containment Timestamp Bypass vs. Partial-Overlap Binary Search**:
   - If `minTimestamp >= startTimestamp && maxTimestamp <= endTimestamp`, every row in `[0, rowCount)` matches the time window — the compressed timestamp column is **never read or decompressed**.
   - On partial overlap, `Timestamps` is decompressed and narrowed to `[startIndex, endIndex)` via `LowerBound(startTimestamp)` and `UpperBound(endTimestamp)` in $O(\log N)$ time (handling duplicate timestamps deterministically). If `endIndex <= startIndex`, metric and category columns are skipped as well.
3. **Columnar Projection & Double-Precision SIMD Aggregation**:
   - Projects only the required metric (and optional category) columns via `MemoryMappedFile`.
   - Uses `System.Numerics.Vector<T>` masked selection (`Vector.Equals`, `Vector.ConditionalSelect`) paired with **`Vector.Widen` into `Vector<double>` accumulators** (`sumLow`, `sumHigh`) so large batches retain full IEEE-754 double-precision accuracy without float drift.

---

## 5. Roslyn Incremental Source Generator & Packaging

`Glacier.Chrono.Generator` (`IIncrementalGenerator`) uses `SyntaxProvider.ForAttributeWithMetadataName("Glacier.Chrono.Storage.ChronoTableAttribute", ...)` with a value-equatable `ChronoTableModel` so IDE and build incremental caching only regenerates when a `[ChronoTable]` schema structurally changes.

- Supports arbitrary numbers of `[Metric]` and `[Category]` columns (including 0-category pure-metric schemas and wide tables exceeding 64-byte headers).
- Bundled automatically inside `Glacier.Chrono.nupkg` under `analyzers/dotnet/cs/Glacier.Chrono.Generator.dll` — installing `Glacier.Chrono` automatically activates `[ChronoTable]` code generation with zero reflection and full `<IsAotCompatible>true</IsAotCompatible>` support.
