using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;
using VNotch.Models;

namespace VNotch.Services;

internal readonly record struct StorageMonitorSnapshot(
    IReadOnlyList<StorageDriveInfo> StorageDrives,
    double? DiskReadBytesPerSec,
    double? DiskWriteBytesPerSec,
    double? DiskActivePercent)
{
    internal static StorageMonitorSnapshot Unavailable { get; } =
        new(Array.Empty<StorageDriveInfo>(), null, null, null);
}

/// <summary>
/// Single-worker storage sampler. Query counters share a baseline and remain open until disposal;
/// only capacity enumeration is cached. No methods on this type should run on the UI thread.
/// </summary>
internal sealed partial class StorageMonitorService : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private static readonly long CapacityRefreshTicks = 30 * Stopwatch.Frequency;

    // PDH_FMT_COUNTERVALUE contains a DWORD followed by an eight-byte-aligned union.
    // In particular, packing the double at offset four corrupts readings on Windows x64.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFormattedCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double DoubleValue;
    }

    private sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePdhQueryHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, nuint userData, out SafePdhQueryHandle query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(SafePdhQueryHandle query, string counterPath, nuint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(SafePdhQueryHandle query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint counterType, out PdhFormattedCounterValue value);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);

    private SafePdhQueryHandle? _query;
    private nint _readCounter;
    private nint _writeCounter;
    private nint _idleCounter;
    private bool _queryInitialized;
    private bool _hasBaseline;
    private bool _disposed;
    private long _nextCapacityRefresh;
    private IReadOnlyList<StorageDriveInfo> _drives = Array.Empty<StorageDriveInfo>();

    internal StorageMonitorSnapshot Sample(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (Stopwatch.GetTimestamp() >= _nextCapacityRefresh)
        {
            _drives = ReadDriveCapacity(cancellationToken);
            _nextCapacityRefresh = Stopwatch.GetTimestamp() + CapacityRefreshTicks;
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureQuery();
        if (_query == null || PdhCollectQueryData(_query) != 0)
        {
            _hasBaseline = false;
            return new(_drives, null, null, null);
        }

        // Rate and timer counters need two successful collections, including after a gap.
        if (!_hasBaseline)
        {
            _hasBaseline = true;
            return new(_drives, null, null, null);
        }

        double? read = ReadCounter(_readCounter);
        double? write = ReadCounter(_writeCounter);
        double? idle = ReadCounter(_idleCounter);
        return new(_drives,
            read is >= 0 ? read : null,
            write is >= 0 ? write : null,
            idle.HasValue ? Math.Clamp(100 - idle.Value, 0, 100) : null);
    }

    private void EnsureQuery()
    {
        if (_queryInitialized) return;
        _queryInitialized = true;

        if (PdhOpenQuery(null, 0, out var query) != 0)
        {
            query?.Dispose();
            return;
        }

        _query = query;
        _readCounter = AddCounter(query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _writeCounter = AddCounter(query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _idleCounter = AddCounter(query, @"\PhysicalDisk(_Total)\% Idle Time");
    }

    private static nint AddCounter(SafePdhQueryHandle query, string path) =>
        PdhAddEnglishCounter(query, path, 0, out var counter) == 0 ? counter : 0;

    private static double? ReadCounter(nint counter)
    {
        if (counter == 0 ||
            PdhGetFormattedCounterValue(counter, PdhFmtDouble | PdhFmtNoCap100, out _, out var value) != 0 ||
            (value.Status != 0 && value.Status != 1) ||
            !double.IsFinite(value.DoubleValue))
        {
            return null;
        }

        return value.DoubleValue;
    }

    private static IReadOnlyList<StorageDriveInfo> ReadDriveCapacity(CancellationToken cancellationToken)
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Array.Empty<StorageDriveInfo>();
        }

        var capacities = new List<StorageDriveInfo>(drives.Length);
        foreach (var drive in drives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                long total = drive.TotalSize;
                long free = drive.TotalFreeSpace;
                if (total <= 0 || free < 0 || free > total) continue;
                capacities.Add(new StorageDriveInfo(drive.Name, (ulong)total, (ulong)free));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // A missing, locked or inaccessible volume must not hide other drives.
            }
        }

        return capacities.Count == 0 ? Array.Empty<StorageDriveInfo>() : capacities.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _query?.Dispose();
        _query = null;
        _readCounter = _writeCounter = _idleCounter = 0;
        _hasBaseline = false;
        _drives = Array.Empty<StorageDriveInfo>();
    }
}
