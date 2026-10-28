using System;
using System.Runtime.InteropServices;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Modules;

/// <summary>
/// Tick scheduler for the system monitor. CPU, RAM and network sampling (including the smoothing
/// and delta state) run in VNotch.Native. Storage sampling runs on a separate serialized worker.
/// </summary>
public sealed partial class SystemMonitorModule : NotchModuleBase
{
    private const string NativeDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSystemMonitorState
    {
        public ulong LastIdle;
        public ulong LastKernel;
        public ulong LastUser;
        public long LastCpuQpc;
        public double SmoothedCpu;
        public byte HasCpuMeasurement;
        public ulong LastRecvBytes;
        public ulong LastSentBytes;
        public long LastNetQpc;
        public double DownBytesPerSec;
        public double UpBytesPerSec;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSystemMemory
    {
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public uint MemoryLoadPercent;
        public ulong InstalledPhysical;
    }

    [LibraryImport(NativeDll, EntryPoint = "vn_sysmon_reset")]
    private static partial int NativeReset(ref VnSystemMonitorState state);

    [LibraryImport(NativeDll, EntryPoint = "vn_sysmon_sample_cpu")]
    private static partial int NativeSampleCpu(ref VnSystemMonitorState state, out double cpuPercent);

    [LibraryImport(NativeDll, EntryPoint = "vn_sysmon_sample_network")]
    private static partial int NativeSampleNetwork(ref VnSystemMonitorState state, out double downBytesPerSec, out double upBytesPerSec);

    [LibraryImport(NativeDll, EntryPoint = "vn_sysmon_read_memory")]
    private static partial int NativeReadMemory(int includeInstalled, out VnSystemMemory memory);

    public override string ModuleName => "SystemMonitor";

    public override TimeSpan? TickInterval => TimeSpan.FromSeconds(1);

    private VnSystemMonitorState _cpuState;
    private VnSystemMonitorState _netState;

    private int _isNetworkSampling = 0;
    private double _latestDownBytesPerSec = 0;
    private double _latestUpBytesPerSec = 0;
    private readonly object _netStatsLock = new();

    private ulong _usablePhysicalBytes;
    private ulong _installedPhysicalBytes;

    private readonly object _storageLifetimeLock = new();
    private CancellationTokenSource? _storageCancellation;
    private Task _storageTask = Task.CompletedTask;
    private int _storageGeneration;
    private bool _storageDisposed;
    private StorageMonitorSnapshot _latestStorage = StorageMonitorSnapshot.Unavailable;

    public event EventHandler<SystemMonitorInfo>? StatsUpdated;

    protected override void OnInitialize()
    {
        NativeReset(ref _cpuState);
        NativeReset(ref _netState);

        if (NativeReadMemory(1, out var memory) == 0)
        {
            _usablePhysicalBytes = memory.TotalPhysical;
            _installedPhysicalBytes = memory.InstalledPhysical;
        }
        if (_installedPhysicalBytes == 0) _installedPhysicalBytes = _usablePhysicalBytes;

        // Initialize baselines
        SampleCpuUsage();
        if (Interlocked.CompareExchange(ref _isNetworkSampling, 1, 0) == 0)
        {
            Task.Run(SampleNetworkUsageBackground);
        }
    }

    protected override void OnStart()
    {
        lock (_storageLifetimeLock)
        {
            if (_storageDisposed) return;
            var cancellation = new CancellationTokenSource();
            _storageCancellation = cancellation;
            int generation = ++_storageGeneration;
            _latestStorage = StorageMonitorSnapshot.Unavailable;
            Task previous = _storageTask;
            // A rapid stop/start waits off-thread for any slow previous drive access to finish.
            _storageTask = Task.Run(() => SampleStorageBackgroundAsync(previous, cancellation, generation));
        }
    }

    protected override void OnStop() => StopStorageSampling(disposing: false);

    protected override void OnDispose()
    {
        StopStorageSampling(disposing: true);
        base.OnDispose();
    }

    private void StopStorageSampling(bool disposing)
    {
        lock (_storageLifetimeLock)
        {
            _storageDisposed |= disposing;
            ++_storageGeneration;
            _latestStorage = StorageMonitorSnapshot.Unavailable;
            _storageCancellation?.Cancel();
            _storageCancellation = null;
        }
        // The worker owns its query and disposes it after the current native call finishes.
        // Never wait here: DriveInfo may be blocked in Windows while this is the UI thread.
    }

    private async Task SampleStorageBackgroundAsync(Task previous, CancellationTokenSource cancellation, int generation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            await previous.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using var storage = new StorageMonitorService();
            while (!token.IsCancellationRequested)
            {
                StorageMonitorSnapshot snapshot = storage.Sample(token);
                lock (_storageLifetimeLock)
                {
                    if (_storageDisposed || token.IsCancellationRequested || generation != _storageGeneration) return;
                    _latestStorage = snapshot;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stop/restart invalidates pending work without publishing a late sample.
        }
        catch (Exception ex)
        {
            lock (_storageLifetimeLock)
            {
                if (generation == _storageGeneration)
                    _latestStorage = StorageMonitorSnapshot.Unavailable;
            }
            RuntimeLog.Log("SYSTEM-MONITOR-STORAGE", ex.Message);
        }
        finally
        {
            lock (_storageLifetimeLock)
            {
                if (ReferenceEquals(_storageCancellation, cancellation))
                    _storageCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    protected override void OnTick()
    {
        double cpu = SampleCpuUsage();

        ulong used = 0;
        double ramPercent = 0;
        if (NativeReadMemory(0, out var memory) == 0)
        {
            ulong usable = memory.TotalPhysical > 0 ? memory.TotalPhysical : _usablePhysicalBytes;
            ulong available = memory.AvailablePhysical;
            used = available >= usable ? usable : usable - available;
            ramPercent = memory.MemoryLoadPercent;
        }

        if (Interlocked.CompareExchange(ref _isNetworkSampling, 1, 0) == 0)
        {
            Task.Run(SampleNetworkUsageBackground);
        }

        double netDown, netUp;
        lock (_netStatsLock)
        {
            netDown = _latestDownBytesPerSec;
            netUp = _latestUpBytesPerSec;
        }

        StorageMonitorSnapshot storage;
        lock (_storageLifetimeLock)
        {
            storage = _latestStorage;
        }

        StatsUpdated?.Invoke(this, new SystemMonitorInfo
        {
            CpuPercent = cpu,
            RamUsedBytes = used,
            RamTotalBytes = _installedPhysicalBytes > 0 ? _installedPhysicalBytes : _usablePhysicalBytes,
            RamPercent = ramPercent,
            NetDownBytesPerSec = netDown,
            NetUpBytesPerSec = netUp,
            StorageDrives = storage.StorageDrives,
            DiskReadBytesPerSec = storage.DiskReadBytesPerSec,
            DiskWriteBytesPerSec = storage.DiskWriteBytesPerSec,
            DiskActivePercent = storage.DiskActivePercent
        });
    }

    private double SampleCpuUsage() =>
        NativeSampleCpu(ref _cpuState, out double cpu) == 0 ? cpu : 0;

    private void SampleNetworkUsageBackground()
    {
        try
        {
            int status = NativeSampleNetwork(ref _netState, out double down, out double up);
            lock (_netStatsLock)
            {
                _latestDownBytesPerSec = down;
                _latestUpBytesPerSec = up;
            }
            if (status != 0) RuntimeLog.Log("SYSTEM-MONITOR-NET", $"native network sampling failed ({status})");
        }
        catch (Exception ex)
        {
            lock (_netStatsLock)
            {
                _latestDownBytesPerSec = 0;
                _latestUpBytesPerSec = 0;
            }
            RuntimeLog.Log("SYSTEM-MONITOR-NET", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _isNetworkSampling, 0);
        }
    }
}
