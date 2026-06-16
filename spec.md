# **Glacier.Chrono: Zero-Allocation Time-Series Compression Engine**

## **1\. Architectural Philosophy**

Glacier.Chrono is an embedded, in-process time-series database designed for .NET 10\. It mirrors the hybrid row-to-columnar architecture of TimescaleDB but eliminates the need for an external PostgreSQL dependency.

**Core Constraints:**

* **Zero-Allocation Ingest:** The "hot" ingestion path must allocate absolutely zero memory on the managed heap.  
* **Unmanaged Structs:** All telemetry payloads must be defined as unmanaged structs with \[StructLayout(LayoutKind.Sequential)\] to allow raw pointer manipulation.  
* **SIMD First:** Decompression and analytical aggregations must leverage .NET 10 hardware intrinsics (Vector256\<T\> or Vector512\<T\>).  
* **No ORMs:** Storage interactions use raw memory mapping and byte arrays, avoiding heavy abstraction layers.

## **2\. Phase 1: The "Hot" Row-Store (Ingest Layer)**

To guarantee millions of writes per second with zero latency, incoming data is not immediately compressed. It is written to a pre-allocated Ring Buffer.

### **Data Structure:**

\[StructLayout(LayoutKind.Sequential, Pack \= 1)\]  
public struct TelemetryRow  
{  
    public long Timestamp; // 8 bytes  
    public float CpuUsage; // 4 bytes  
    public float MemUsage; // 4 bytes  
    public int EntityId;   // 4 bytes  
} // Total: 20 bytes per row

### **Ingestion Mechanics:**

* A contiguous block of memory is allocated upfront (e.g., TelemetryRow\[\] or native memory via NativeMemory.Alloc).  
* An Interlocked.Increment is used as a lock-free write cursor.  
* Multiple threads can blast data into this array simultaneously without locks, mutexes, or GC pressure.

## **3\. Phase 2: The Pivot (Background Compaction)**

Once the Hot Buffer reaches a specific threshold (e.g., 10,000 rows or a 5-minute time boundary), a background compaction thread takes ownership of that block.

It performs a **Matrix Transpose**, converting the Array of Structs (AoS) into a Struct of Arrays (SoA).

* Span\<long\> timestamps  
* Span\<float\> cpuUsages  
* Span\<int\> entityIds

This aligns the data sequentially by type in memory, preparing it for column-specific compression.

## **4\. Phase 3: The Compression Engine**

Different data types require different algorithms to achieve the 90%+ compression ratios seen in TimescaleDB. We implement these directly on the Span\<T\> buffers.

### **A. Timestamps: Delta-of-Delta (DoD)**

Instead of storing full 64-bit UNIX epochs, we store the variance in the interval.

1. Store the first timestamp (![][image1]).  
2. Store the first delta (![][image2]).  
3. For subsequent timestamps, calculate the Delta-of-Delta: ![][image3].  
   *Implementation Note:* For telemetry logged at exact 1-second intervals, the DoD is 0\. We use bit-packing to encode a 0 value in a single bit, effectively compressing thousands of timestamps into a few bytes.

### **B. Floating Point Metrics: Gorilla XOR**

Based on Facebook's Gorilla TSDB paper.

1. XOR the current float against the previous float.  
2. In slowly changing metrics (e.g., CPU changes from 45.1 to 45.2), the XOR produces a value with many leading and trailing zero bits.  
3. We strip the zeros and bit-pack the remaining "meaningful bits" alongside a tiny header indicating the offset.

### **C. Integers / Enums: Run-Length Encoding (RLE) & Simple-8b**

For state indicators (e.g., StatusId \= 1 for "Running"):

* If a server is running for 5,000 consecutive ticks, we don't store 5,000 1s.  
* We store a tuple: \[Value: 1, Count: 5000\].

## **5\. Phase 4: Cold Storage & SIMD Querying**

### **Storage:**

The compressed byte arrays are flushed to disk in isolated "Chunk" files (e.g., chunk\_20260616\_1200.glacier).

### **Execution:**

When an analytical query is executed (e.g., "Average CPU usage for Entity 5"):

1. **Columnar Projection:** Glacier.Chrono maps *only* the cpuUsages and entityIds files into memory via MemoryMappedFile.CreateFromFile(). The timestamp files are completely ignored, saving massive I/O.  
2. **Vectorized Scans:** The query engine uses Vector256\<float\> to decompress and sum the floats simultaneously, processing 8 rows per CPU clock cycle.

## **6\. Prompt for Antigravity IDE**

*Copy and paste this into the Antigravity prompt to bootstrap the engine:*

"I want to build Glacier.Chrono, a zero-allocation time-series compression engine in C\# 10\. Based on the provided architectural specification, please generate the HotRingBuffer\<T\> class for the lock-free ingest layer, ensuring T is constrained to unmanaged. Then, generate the stub for the GorillaCompressor static class, implementing the XOR bit-packing logic for floating-point Span\<float\> inputs as described by the Facebook Gorilla paper. Avoid all heap allocations during the compression cycle."

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAaCAYAAAC3g3x9AAAAaUlEQVR4Xu2QQQ4AIQgD/Qn/f+VuPLgxhaIoe9JJOLTWRizlkoKIPJHB+woWRs08BQsxfwnrxVuwMuYPWb5o8cu6aYWtzCucyXyMwsyneGUVPEOtSCns18Sxsp4OgwWow2wXWHhfcjmBF9ENUu0KJ/tMAAAAAElFTkSuQmCC>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEEAAAAaCAYAAADovjFxAAAAw0lEQVR4Xu3SUQrDMAwD0N4k9z/lxj4ygrBjtbW8DvwgH3VsI0KPo7XWwBjjdebgfDXMEx2cN3kD+O3VqnlZsTbrWDN5jVbdqlWzMtx+BItkqZAk763hH5DklSwVSs/r/VpPJckrWSqUnnU+wG5xdI/WnczB+Z1o5tJudiC6rxJlXe+i3i+2kempwOb9oHvZRqZHbWY9k8XtXZfhwd5pd6eGGZm8E9NDS11WhH0oWuoyoTVn2iOsv17aUqF/ytpaa+1J3jh8uOFhy+tTAAAAAElFTkSuQmCC>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAASYAAAAaCAYAAAAaC/G4AAAC4ElEQVR4Xu2UW24sMQhEs5PZ/yoT+cMKKoHB7Qf0qI6ErgxlUzCd+/NDCCGEEEIIIYTc5fP5/GKOrMO9klnaN3Psu+mPjwLvVAF9eoH3s0Bfo8C7J8noaYE+cC9eyLvfAM7nBd4/ybF+o2FGtZtoHjRveO45LZ+F5gfPVu40GT0jzOxMy78dba5o7jTH+o2G6TWrfgOrfzTXsPIZaF60XAbWrm+i9Y/mGlb+zWgzWb+VljuJ5WMZ72GvfhKrt5XT8g0rfxvNx8h3BpletF3guee0fMPKvxVtnj6/VcPcabb3HA3Y8eonsXpbOSuPuSw0L9V8Z/VtaLvAc89Zecy9HW2mavNv79sHHD3s1U8y03tGW4lqvjO9RHcR1X0r1ebf7iUyYETT6LqZwDeQiKYTfbMSM7u4SZafaN+KO7tJxfm3+vEG7PWR5iQzfWe0Vcjc7YgMTzO7iOq+lYrzb/XkfQy9PtKcJNr3hEc5eyTwfoSVuzPM9vD0OPso8K5FVB/V7eBJH5zfC7zv8fTeLLM+I5ow3mNRU6eI9s72+ZSqvjM8RXcR1e3gVp8ZbsyPPSL9IpoQ2Bzx6kjXzwS+gUQ0DU/n1bOo6iuLyD48jVefYedbu/A8ybqntcC/z6fvPAKbS0a1m0R8eJpe6zpPfwvPB/r19Lu40cPCmzFS7//u2NnK3RN48+D8MreC94ZXDyF/MC1Qn4nlCT17/mV+pDsNepWB2obMazp8Q4bUzbBydxXNO84lQ+o6Mq/p8A0ZUte1mLsNesTQ9N7ZCqnrWHlJRPN17BhavjH6EaqR4ftGjxGr/XfubOVuFugZzye40aMcqx9XAz9WzL2BG7537HqV1f47f+un9zLRPGu5CJF7Fb6ZNFYGx8XhuSro8anvfk8Gahqj2m2e+sAZ8DyD3NfTN26jecVzBJx99MaoRsgylT6wSl4IGeL9z02eU3GvFT2Rf/j3SAghhBBCCCGEEEIq8QcFy9djWSfJ1QAAAABJRU5ErkJggg==>