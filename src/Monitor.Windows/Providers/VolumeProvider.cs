using System.Runtime.InteropServices;
using System.Text;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;
using Monitor.Windows.Native;

namespace Monitor.Windows.Providers;

/// <summary>Supplies <see cref="VolumeSnapshot"/> for every logical drive on the machine (drive letters
/// C: through Z:), independent of whether the drive maps to a local physical disk. Unlike
/// <see cref="DiskProvider"/>'s per-physical-disk volumes, this includes network drives, which never
/// resolve to a physical disk.
///
/// Capacity queries (<c>GetDiskFreeSpaceExW</c> in particular) can block for tens of seconds on an
/// unreachable network drive. To keep <see cref="Sample"/> non-blocking - it is called from
/// <c>MetricsHub</c>'s single sampling thread, and a slow Sample() there stalls every other metric too -
/// all actual drive I/O happens on a background <see cref="Task"/> that refreshes a cached snapshot list
/// roughly every <see cref="RefreshInterval"/>. Sample() only ever reads that cache and never awaits
/// anything.</summary>
public sealed partial class VolumeProvider : IMetricProvider<IReadOnlyList<VolumeSnapshot>>
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _cts;
    private Task? _refreshTask;
    private DateTime _lastRefreshStartUtc = DateTime.MinValue;
    private volatile IReadOnlyList<VolumeSnapshot> _cache = Array.Empty<VolumeSnapshot>();

    public string Name => "Volumes";

    public bool IsAvailable { get; private set; }

    public void Initialize()
    {
        _cts = new CancellationTokenSource();
        IsAvailable = true;

        // Kick off the first refresh but never wait on it here: Initialize() runs sequentially with
        // every other provider's Initialize() on MetricsHub's startup path, and blocking here would
        // delay CPU/memory/etc. from ever producing a first sample if a network drive is unresponsive.
        TriggerRefreshIfDue(force: true);
    }

    public IReadOnlyList<VolumeSnapshot> Sample(TimeSpan elapsed)
    {
        TriggerRefreshIfDue(force: false);
        return _cache;
    }

    public void Dispose()
    {
        if (_cts is not null)
        {
            try
            {
                _cts.Cancel();
            }
            catch
            {
                // ignore
            }

            try
            {
                _refreshTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Background task may still be blocked in a native call it cannot be interrupted out
                // of (e.g. an in-flight GetDiskFreeSpaceExW); dropping the reference is the best we can
                // do, matching this codebase's "Dispose 中の例外は無視する" convention elsewhere.
            }

            _cts.Dispose();
            _cts = null;
        }

        _refreshTask = null;
        _cache = Array.Empty<VolumeSnapshot>();
        IsAvailable = false;
    }

    /// <summary>Starts a background refresh if none is currently running and either <paramref
    /// name="force"/> is set or <see cref="RefreshInterval"/> has elapsed since the last one started.
    /// Never blocks the caller.</summary>
    private void TriggerRefreshIfDue(bool force)
    {
        CancellationTokenSource? cts = _cts;
        if (cts is null || cts.IsCancellationRequested)
        {
            return;
        }

        if (_refreshTask is { IsCompleted: false })
        {
            return;
        }

        if (!force && DateTime.UtcNow - _lastRefreshStartUtc < RefreshInterval)
        {
            return;
        }

        _lastRefreshStartUtc = DateTime.UtcNow;
        CancellationToken token = cts.Token;
        _refreshTask = Task.Run(() => RefreshAsync(token), token);
    }

    /// <summary>Enumerates every logical drive and queries each one's capacity/label/network-path/
    /// physical-disk-number in parallel, publishing each drive's result into <see cref="_cache"/> as
    /// soon as it resolves (rather than waiting for the slowest one) so one unresponsive network drive
    /// does not delay every other drive's data from showing up.</summary>
    private async Task RefreshAsync(CancellationToken token)
    {
        string[] driveNames;
        try
        {
            driveNames = DriveInfo.GetDrives().Select(d => d.Name).ToArray();
        }
        catch
        {
            return; // keep whatever was cached before
        }

        if (driveNames.Length == 0 || token.IsCancellationRequested)
        {
            return;
        }

        var working = new Dictionary<string, VolumeSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (VolumeSnapshot existing in _cache)
        {
            working[existing.DriveLetter] = existing;
        }

        var pending = new List<Task<VolumeSnapshot>>(driveNames.Length);
        foreach (string driveName in driveNames)
        {
            pending.Add(Task.Run(() => ReadVolumeSafe(driveName), token));
        }

        while (pending.Count > 0)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            Task<VolumeSnapshot> completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);

            try
            {
                VolumeSnapshot snapshot = await completed.ConfigureAwait(false);
                working[snapshot.DriveLetter] = snapshot;
                _cache = working.Values.OrderBy(v => v.DriveLetter, StringComparer.Ordinal).ToArray();
            }
            catch
            {
                // ReadVolumeSafe never throws, but guard defensively; skip this drive for this round.
            }
        }
    }

    /// <summary>Reads everything for one drive letter. Every failure path returns a non-null snapshot
    /// with <see cref="VolumeSnapshot.IsReady"/> == false and zeroed capacity rather than throwing or
    /// omitting the drive, per this codebase's "provider never throws, missing data is null/0" rule.</summary>
    private static VolumeSnapshot ReadVolumeSafe(string driveNamePath)
    {
        string letter = driveNamePath.TrimEnd('\\');

        try
        {
            VolumeKind kind = MapDriveType(GetDriveTypeW(driveNamePath));

            string? label = null;
            string? fileSystem = null;
            try
            {
                var labelBuffer = new StringBuilder(261);
                var fsBuffer = new StringBuilder(261);
                bool ok = GetVolumeInformationW(
                    driveNamePath,
                    labelBuffer,
                    (uint)labelBuffer.Capacity,
                    out _,
                    out _,
                    out _,
                    fsBuffer,
                    (uint)fsBuffer.Capacity);

                if (ok)
                {
                    label = labelBuffer.Length > 0 ? labelBuffer.ToString() : null;
                    fileSystem = fsBuffer.Length > 0 ? fsBuffer.ToString() : null;
                }
            }
            catch
            {
                // leave label/fileSystem null
            }

            ulong total = 0, free = 0, used = 0;
            double usedPercent = 0.0;
            bool isReady = false;
            try
            {
                if (GetDiskFreeSpaceExW(driveNamePath, out _, out ulong totalBytes, out ulong totalFreeBytes))
                {
                    total = totalBytes;
                    free = totalFreeBytes;
                    used = total >= free ? total - free : 0;
                    usedPercent = total > 0 ? ClampPercent(100.0 * used / total) : 0.0;
                    isReady = true;
                }
            }
            catch
            {
                // leave capacity fields at their zeroed defaults, IsReady = false
            }

            string? networkPath = null;
            int? physicalDriveNumber = null;
            if (kind == VolumeKind.Network)
            {
                networkPath = TryGetNetworkPath(letter);
            }
            else if (kind == VolumeKind.Fixed)
            {
                physicalDriveNumber = StorageApi.TryGetPhysicalDriveNumber(letter);
            }

            return new VolumeSnapshot
            {
                DriveLetter = letter,
                Label = label,
                FileSystem = fileSystem,
                Kind = kind,
                NetworkPath = networkPath,
                TotalBytes = total,
                FreeBytes = free,
                UsedBytes = used,
                UsedPercent = usedPercent,
                PhysicalDriveNumber = physicalDriveNumber,
                IsReady = isReady,
            };
        }
        catch
        {
            return new VolumeSnapshot
            {
                DriveLetter = letter,
                Kind = VolumeKind.Unknown,
                IsReady = false,
            };
        }
    }

    /// <summary>Resolves a network drive's UNC target via WNetGetConnectionW, retrying once with a
    /// larger buffer on ERROR_MORE_DATA. Returns null (never throws) if the drive is not actually a
    /// mapped network connection or the call fails for any other reason.</summary>
    private static string? TryGetNetworkPath(string letter)
    {
        const uint NoError = 0;
        const uint ErrorMoreData = 234;

        uint length = 512;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
            try
            {
                uint result = WNetGetConnectionW(letter, buffer, ref length);
                if (result == NoError)
                {
                    string? path = Marshal.PtrToStringUni(buffer);
                    return string.IsNullOrWhiteSpace(path) ? null : path;
                }

                if (result != ErrorMoreData)
                {
                    return null;
                }

                // length now holds the required size in chars; loop once more with a bigger buffer.
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static VolumeKind MapDriveType(uint driveType) => driveType switch
    {
        2 => VolumeKind.Removable,
        3 => VolumeKind.Fixed,
        4 => VolumeKind.Network,
        5 => VolumeKind.CdRom,
        6 => VolumeKind.Ram,
        _ => VolumeKind.Unknown,
    };

    private static double ClampPercent(double value) => Math.Clamp(value, 0.0, 100.0);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string lpRootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [LibraryImport("mpr.dll", EntryPoint = "WNetGetConnectionW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WNetGetConnectionW(string lpLocalName, IntPtr lpRemoteName, ref uint lpnLength);

    // GetVolumeInformationW takes two caller-allocated StringBuilder buffers; the LibraryImport source
    // generator does not support marshalling System.Text.StringBuilder, so this one stays on DllImport
    // per this codebase's "LibraryImport in principle, DllImport only where marshalling doesn't fit" rule.
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        uint nFileSystemNameSize);
}
