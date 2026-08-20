using System.Globalization;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;
using Monitor.Windows.Native;

namespace Monitor.Windows.Providers;

/// <summary>
/// GPU 使用率 (PDH "\GPU Engine(*)\Utilization Percentage") と VRAM
/// (PDH "\GPU Adapter Memory(*)\Dedicated/Shared Usage" + DXGI の総容量) を供給する。
/// PDH の GPU カウンターが存在しない環境 (古い Windows / RDP セッションなど) では
/// IsAvailable=false のまま GpuSnapshot.Empty を返し続ける。
/// </summary>
public sealed class GpuProvider : IMetricProvider<GpuSnapshot>
{
    private readonly IGpuVendorSensors? _vendorSensors;

    private PdhQuery? _query;
    private PdhMultiCounter? _engineCounter;
    private PdhMultiCounter? _dedicatedMemoryCounter;
    private PdhMultiCounter? _sharedMemoryCounter;
    private IReadOnlyList<DxgiAdapterInfo> _adapters = Array.Empty<DxgiAdapterInfo>();
    private bool _disposed;

    /// <summary>
    /// <paramref name="vendorSensors"/> はベンダー固有 API（NVAPI 等）由来の追加センサー（温度/ファン/
    /// 電力/クロック）の供給元。層を逆転させないため、この層は具体的なベンダー実装を知らない。
    /// null なら該当項目は常に null のままになる（NVIDIA 以外の GPU / ドライバ無し等）。
    /// </summary>
    public GpuProvider(IGpuVendorSensors? vendorSensors = null)
    {
        _vendorSensors = vendorSensors;
    }

    public string Name => "GPU";

    public bool IsAvailable { get; private set; }

    public void Initialize()
    {
        try
        {
            _adapters = Dxgi.EnumerateAdapters();
        }
        catch
        {
            _adapters = Array.Empty<DxgiAdapterInfo>();
        }

        try
        {
            _query = PdhQuery.TryCreate();
            if (_query is null)
            {
                IsAvailable = false;
                return;
            }

            // "GPU Engine" is the counter that must exist for GPU usage reporting to make sense at
            // all; "GPU Adapter Memory" is nice-to-have (VRAM totals still work without it, just as
            // zero) so its absence does not affect IsAvailable.
            _engineCounter = _query.AddMultiCounter(@"\GPU Engine(*)\Utilization Percentage");
            _dedicatedMemoryCounter = _query.AddMultiCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _sharedMemoryCounter = _query.AddMultiCounter(@"\GPU Adapter Memory(*)\Shared Usage");

            IsAvailable = _engineCounter is not null;

            if (IsAvailable)
            {
                // Prime the query so the first real Sample() call already has a collected sample to
                // format instead of hitting PDH's "not enough data yet" on every counter.
                _query.Collect();
            }
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public GpuSnapshot Sample(TimeSpan elapsed)
    {
        if (!IsAvailable || _query is null || _engineCounter is null)
        {
            return GpuSnapshot.Empty;
        }

        try
        {
            _query.Collect();

            var perAdapter = new Dictionary<long, AdapterAccumulator>();

            foreach (PdhCounterItem item in _engineCounter.GetValues())
            {
                if (!TryParseEngineInstance(item.InstanceName, out long luid, out ReadOnlySpan<char> engineType))
                {
                    continue;
                }

                GetOrAddAccumulator(perAdapter, luid).AddEngine(engineType, item.Value);
            }

            if (_dedicatedMemoryCounter is not null)
            {
                foreach (PdhCounterItem item in _dedicatedMemoryCounter.GetValues())
                {
                    if (!TryParseMemoryInstance(item.InstanceName, out long luid))
                    {
                        continue;
                    }

                    GetOrAddAccumulator(perAdapter, luid).DedicatedUsedBytes += ClampToBytes(item.Value);
                }
            }

            if (_sharedMemoryCounter is not null)
            {
                foreach (PdhCounterItem item in _sharedMemoryCounter.GetValues())
                {
                    if (!TryParseMemoryInstance(item.InstanceName, out long luid))
                    {
                        continue;
                    }

                    GetOrAddAccumulator(perAdapter, luid).SharedUsedBytes += ClampToBytes(item.Value);
                }
            }

            if (perAdapter.Count == 0)
            {
                // Nothing recognizable in this sample (e.g. transient PDH hiccup); avoid reporting a
                // phantom zero-adapter snapshot vs. genuinely no adapters by falling back to Empty.
                return GpuSnapshot.Empty;
            }

            var adapters = new List<GpuAdapterSnapshot>(perAdapter.Count);
            double totalUsage = 0.0;
            int fallbackIndex = 0;

            foreach (KeyValuePair<long, AdapterAccumulator> kvp in perAdapter)
            {
                long luid = kvp.Key;
                AdapterAccumulator acc = kvp.Value;

                string name = $"GPU {fallbackIndex}";
                ulong dedicatedTotal = 0;

                foreach (DxgiAdapterInfo info in _adapters)
                {
                    if (info.Luid == luid)
                    {
                        if (!string.IsNullOrEmpty(info.Description))
                        {
                            name = info.Description;
                        }

                        dedicatedTotal = info.DedicatedVideoMemory;
                        break;
                    }
                }

                double usage = Math.Clamp(acc.MaxCategoryTotal(), 0.0, 100.0);
                totalUsage = Math.Max(totalUsage, usage);

                adapters.Add(new GpuAdapterSnapshot
                {
                    Name = name,
                    Luid = luid,
                    UsagePercent = usage,
                    Engine3DPercent = Math.Clamp(acc.Engine3D, 0.0, 100.0),
                    EngineCopyPercent = Math.Clamp(acc.EngineCopy, 0.0, 100.0),
                    EngineVideoPercent = Math.Clamp(acc.MaxVideoTotal(), 0.0, 100.0),
                    EngineComputePercent = Math.Clamp(acc.EngineCompute, 0.0, 100.0),
                    DedicatedUsedBytes = acc.DedicatedUsedBytes,
                    DedicatedTotalBytes = dedicatedTotal,
                    SharedUsedBytes = acc.SharedUsedBytes,
                });

                fallbackIndex++;
            }

            MergeVendorSensors(adapters);

            return new GpuSnapshot
            {
                Adapters = adapters,
                TotalUsagePercent = Math.Clamp(totalUsage, 0.0, 100.0),
            };
        }
        catch
        {
            return GpuSnapshot.Empty;
        }
    }

    /// <summary>
    /// PDH/DXGI から組み立てた各アダプタへ、ベンダーセンサー（温度/ファン/電力/クロック等）を
    /// 突き合わせて上書きする。突き合わせは LUID 一致を最優先、駄目なら名前の部分一致、
    /// それも駄目なら列挙順で対応付ける。1件も突き合わせられなくても例外は投げない
    /// （このメソッド自体が失敗しても Sample() 全体を失敗させてはならない）。
    /// </summary>
    private void MergeVendorSensors(List<GpuAdapterSnapshot> adapters)
    {
        if (_vendorSensors is null || adapters.Count == 0)
        {
            return;
        }

        IReadOnlyList<GpuVendorReading> vendorReadings;
        try
        {
            vendorReadings = _vendorSensors.Read();
        }
        catch
        {
            return;
        }

        if (vendorReadings.Count == 0)
        {
            return;
        }

        var usedVendor = new bool[vendorReadings.Count];
        var matched = new GpuVendorReading?[adapters.Count];

        // Pass 1: LUID 一致（最も信頼できる）。
        for (int i = 0; i < adapters.Count; i++)
        {
            for (int j = 0; j < vendorReadings.Count; j++)
            {
                if (usedVendor[j] || vendorReadings[j].Luid is not long vLuid || vLuid != adapters[i].Luid)
                {
                    continue;
                }

                matched[i] = vendorReadings[j];
                usedVendor[j] = true;
                break;
            }
        }

        // Pass 2: アダプタ名の部分一致（LUID を提供できないベンダー実装 [NVAPI 等] 向け）。
        for (int i = 0; i < adapters.Count; i++)
        {
            if (matched[i] is not null)
            {
                continue;
            }

            for (int j = 0; j < vendorReadings.Count; j++)
            {
                if (usedVendor[j])
                {
                    continue;
                }

                string vName = vendorReadings[j].Name;
                string aName = adapters[i].Name;
                if (string.IsNullOrEmpty(vName) || string.IsNullOrEmpty(aName))
                {
                    continue;
                }

                if (aName.Contains(vName, StringComparison.OrdinalIgnoreCase)
                    || vName.Contains(aName, StringComparison.OrdinalIgnoreCase))
                {
                    matched[i] = vendorReadings[j];
                    usedVendor[j] = true;
                    break;
                }
            }
        }

        // Pass 3: 残りは列挙順で対応付ける（GPU が1台だけの構成が大半のため、これで十分実用的）。
        int vendorIndex = 0;
        for (int i = 0; i < adapters.Count; i++)
        {
            if (matched[i] is not null)
            {
                continue;
            }

            while (vendorIndex < vendorReadings.Count && usedVendor[vendorIndex])
            {
                vendorIndex++;
            }

            if (vendorIndex >= vendorReadings.Count)
            {
                break;
            }

            matched[i] = vendorReadings[vendorIndex];
            usedVendor[vendorIndex] = true;
            vendorIndex++;
        }

        for (int i = 0; i < adapters.Count; i++)
        {
            if (matched[i] is not GpuVendorReading v)
            {
                continue;
            }

            // VRAM 総容量は NVAPI のほうが正確なので、値が取れていれば DXGI 由来の値より優先する。
            ulong dedicatedTotal = v.DedicatedTotalBytes is ulong vendorTotal && vendorTotal > 0
                ? vendorTotal
                : adapters[i].DedicatedTotalBytes;

            adapters[i] = adapters[i] with
            {
                TemperatureC = v.TemperatureC,
                HotspotTemperatureC = v.HotspotTemperatureC,
                MemoryTemperatureC = v.MemoryTemperatureC,
                FanPercent = v.FanPercent,
                FanRpm = v.FanRpm,
                PowerWatts = v.PowerWatts,
                PowerLimitWatts = v.PowerLimitWatts,
                CoreClockMhz = v.CoreClockMhz,
                MemoryClockMhz = v.MemoryClockMhz,
                DriverVersion = v.DriverVersion,
                DedicatedTotalBytes = dedicatedTotal,
            };
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _query?.Dispose();

        try
        {
            _vendorSensors?.Dispose();
        }
        catch
        {
            // Dispose 中の例外は無視する。
        }
    }

    private static AdapterAccumulator GetOrAddAccumulator(Dictionary<long, AdapterAccumulator> map, long luid)
    {
        if (!map.TryGetValue(luid, out AdapterAccumulator? acc))
        {
            acc = new AdapterAccumulator();
            map[luid] = acc;
        }

        return acc;
    }

    private static ulong ClampToBytes(double value)
        => value <= 0.0 ? 0UL : (ulong)value;

    /// <summary>
    /// Parses a "\GPU Engine(*)" instance name of the form
    /// "pid_1234_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D", extracting the adapter LUID
    /// and the engtype suffix (e.g. "3D", "Copy", "VideoDecode", ...). Span-based: no substrings are
    /// allocated for the (very hot, up to hundreds-per-second) common engine-type comparisons.
    /// </summary>
    private static bool TryParseEngineInstance(ReadOnlySpan<char> instanceName, out long luid, out ReadOnlySpan<char> engineType)
    {
        engineType = default;

        if (!TryParseLuid(instanceName, out luid, out int afterLuid))
        {
            return false;
        }

        const string marker = "engtype_";
        int relativeIdx = instanceName[afterLuid..].LastIndexOf(marker);
        if (relativeIdx < 0)
        {
            return false;
        }

        int start = afterLuid + relativeIdx + marker.Length;
        engineType = instanceName[start..];
        return !engineType.IsEmpty;
    }

    /// <summary>
    /// Parses a "\GPU Adapter Memory(*)" instance name of the form
    /// "luid_0x00000000_0x0000ABCD_phys_0", extracting only the adapter LUID.
    /// </summary>
    private static bool TryParseMemoryInstance(ReadOnlySpan<char> instanceName, out long luid)
        => TryParseLuid(instanceName, out luid, out _);

    /// <summary>
    /// Parses the "luid_0xHHHHHHHH_0xLLLLLLLL" segment common to both counter families. The first hex
    /// group is HighPart, the second is LowPart (matches Windows' own LUID field order). Combines them
    /// exactly the way Dxgi.EnumerateAdapters() does, so the two can be matched by equality.
    /// </summary>
    private static bool TryParseLuid(ReadOnlySpan<char> instanceName, out long luid, out int afterIndex)
    {
        luid = 0;
        afterIndex = -1;

        const string marker = "luid_0x";
        int idx = instanceName.IndexOf(marker);
        if (idx < 0)
        {
            return false;
        }

        int pos = idx + marker.Length;

        if (!TryReadHex(instanceName, ref pos, out uint high))
        {
            return false;
        }

        if (pos + 3 > instanceName.Length || instanceName[pos] != '_' || instanceName[pos + 1] != '0' || instanceName[pos + 2] != 'x')
        {
            return false;
        }

        pos += 3;

        if (!TryReadHex(instanceName, ref pos, out uint low))
        {
            return false;
        }

        // Matches Dxgi.EnumerateAdapters(): ((long)HighPart << 32) | (uint)LowPart, where HighPart is
        // a signed 32-bit field reinterpreted from the same hex bit pattern PDH prints.
        luid = ((long)unchecked((int)high) << 32) | low;
        afterIndex = pos;
        return true;
    }

    private static bool TryReadHex(ReadOnlySpan<char> s, ref int pos, out uint value)
    {
        int start = pos;
        while (pos < s.Length && Uri.IsHexDigit(s[pos]))
        {
            pos++;
        }

        if (pos == start)
        {
            value = 0;
            return false;
        }

        return uint.TryParse(s[start..pos], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Per-adapter accumulator for one Sample() pass. Named engine-type totals (3D/Copy/VideoDecode/
    /// VideoEncode/VideoProcessing/Compute) are summed in fixed fields to avoid any dictionary/string
    /// allocation on the hot path; any other engtype (Security, Sc, Crypto, ...) falls back to a
    /// lazily-created dictionary since it is only needed to compute the overall max-category total.
    /// </summary>
    private sealed class AdapterAccumulator
    {
        public double Engine3D;
        public double EngineCopy;
        public double VideoDecode;
        public double VideoEncode;
        public double VideoProcessing;
        public double EngineCompute;
        public ulong DedicatedUsedBytes;
        public ulong SharedUsedBytes;
        private Dictionary<string, double>? _otherEngines;

        public void AddEngine(ReadOnlySpan<char> engineType, double value)
        {
            if (value < 0.0)
            {
                value = 0.0;
            }

            if (engineType.SequenceEqual("3D"))
            {
                Engine3D += value;
            }
            else if (engineType.SequenceEqual("Copy"))
            {
                EngineCopy += value;
            }
            else if (engineType.SequenceEqual("VideoDecode"))
            {
                VideoDecode += value;
            }
            else if (engineType.SequenceEqual("VideoEncode"))
            {
                VideoEncode += value;
            }
            else if (engineType.SequenceEqual("VideoProcessing"))
            {
                VideoProcessing += value;
            }
            else if (engineType.SequenceEqual("Compute"))
            {
                EngineCompute += value;
            }
            else
            {
                _otherEngines ??= new Dictionary<string, double>();
                string key = engineType.ToString();
                _otherEngines[key] = _otherEngines.TryGetValue(key, out double existing) ? existing + value : value;
            }
        }

        public double MaxVideoTotal() => Math.Max(VideoDecode, Math.Max(VideoEncode, VideoProcessing));

        /// <summary>Max across every distinct engtype category summed for this adapter (Task Manager's
        /// definition of overall GPU usage), not just the four broken out on GpuAdapterSnapshot.</summary>
        public double MaxCategoryTotal()
        {
            double max = Math.Max(Engine3D, Math.Max(EngineCopy, Math.Max(VideoDecode, Math.Max(VideoEncode, Math.Max(VideoProcessing, EngineCompute)))));

            if (_otherEngines is not null)
            {
                foreach (double v in _otherEngines.Values)
                {
                    if (v > max)
                    {
                        max = v;
                    }
                }
            }

            return max;
        }
    }
}
