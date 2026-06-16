using System;
using System.Runtime.InteropServices;

namespace Glacier.Chrono.Storage;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TelemetryRow : IComparable<TelemetryRow>
{
    public long Timestamp; // 8 bytes
    public float CpuUsage; // 4 bytes
    public float MemUsage; // 4 bytes
    public int EntityId;   // 4 bytes

    public int CompareTo(TelemetryRow other)
    {
        return Timestamp.CompareTo(other.Timestamp);
    }
}
