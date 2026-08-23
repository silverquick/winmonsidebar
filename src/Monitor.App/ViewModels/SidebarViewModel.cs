using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Monitor.App.Settings;
using Monitor.Core;
using Monitor.Core.Formatting;
using Monitor.Core.Models;

namespace Monitor.App.ViewModels;

/// <summary>
/// サイドバー全体の表示状態。<see cref="MetricsHub.SnapshotAvailable"/> はバックグラウンドスレッドで
/// 発火するため、<see cref="Dispatcher.BeginInvoke(System.Delegate)"/> で UI スレッドへマーシャリングしてから
/// プロパティを更新する。
/// </summary>
public sealed class SidebarViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MetricsHub _hub;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clockTimer;
    private readonly AppSettings _settings;
    private readonly int _topProcessCount;
    private bool _disposed;

    public SidebarViewModel(MetricsHub hub, Dispatcher dispatcher, AppSettings settings)
    {
        _hub = hub;
        _dispatcher = dispatcher;
        _settings = settings;
        _topProcessCount = Math.Max(1, settings.TopProcessCount);

        Processes = new ObservableCollection<ProcessRowViewModel>();
        StorageRows = new ObservableCollection<StorageRowViewModel>();

        // 展開状態を設定から復元する。setter を経由すると保存を誘発するため、フィールドへ直接代入する。
        _isCpuExpanded = GetExpanded("cpu");
        _isGpuExpanded = GetExpanded("gpu");
        _isMemoryExpanded = GetExpanded("memory");
        _isMemoryModulesExpanded = GetExpanded("memory-modules", defaultValue: false);
        _isStorageExpanded = GetExpanded("storage");
        _isNetworkExpanded = GetExpanded("network");
        _isNetworkAllExpanded = GetExpanded("network-all", defaultValue: false);
        _isThermalExpanded = GetExpanded("thermal");
        _isProcessExpanded = GetExpanded("process");

        ProcessSummary = string.Create(CultureInfo.InvariantCulture, $"上位 {_topProcessCount} 件");

        _hub.SnapshotAvailable += OnSnapshotAvailable;

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += (_, _) => HeaderTime = DateTime.Now.ToString("HH:mm:ss");
        _clockTimer.Start();
        HeaderTime = DateTime.Now.ToString("HH:mm:ss");

        if (_hub.Latest is MetricsSnapshot latest)
        {
            Apply(latest);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProcessRowViewModel> Processes { get; }

    public ObservableCollection<StorageRowViewModel> StorageRows { get; }

    // ----- ヘッダー -----
    private string _headerTime = string.Empty;
    public string HeaderTime
    {
        get => _headerTime;
        private set => SetProperty(ref _headerTime, value);
    }

    // ----- セクション展開状態（SettingsStore へ永続化） -----
    private bool _isCpuExpanded;
    public bool IsCpuExpanded { get => _isCpuExpanded; set => SetExpanded("cpu", value, ref _isCpuExpanded); }

    private bool _isGpuExpanded;
    public bool IsGpuExpanded { get => _isGpuExpanded; set => SetExpanded("gpu", value, ref _isGpuExpanded); }

    private bool _isMemoryExpanded;
    public bool IsMemoryExpanded { get => _isMemoryExpanded; set => SetExpanded("memory", value, ref _isMemoryExpanded); }

    private bool _isMemoryModulesExpanded;
    public bool IsMemoryModulesExpanded { get => _isMemoryModulesExpanded; set => SetExpanded("memory-modules", value, ref _isMemoryModulesExpanded); }

    private bool _isStorageExpanded;
    public bool IsStorageExpanded { get => _isStorageExpanded; set => SetExpanded("storage", value, ref _isStorageExpanded); }

    private bool _isNetworkExpanded;
    public bool IsNetworkExpanded { get => _isNetworkExpanded; set => SetExpanded("network", value, ref _isNetworkExpanded); }

    private bool _isNetworkAllExpanded;
    public bool IsNetworkAllExpanded { get => _isNetworkAllExpanded; set => SetExpanded("network-all", value, ref _isNetworkAllExpanded); }

    private bool _isThermalExpanded;
    public bool IsThermalExpanded { get => _isThermalExpanded; set => SetExpanded("thermal", value, ref _isThermalExpanded); }

    private bool _isProcessExpanded;
    public bool IsProcessExpanded { get => _isProcessExpanded; set => SetExpanded("process", value, ref _isProcessExpanded); }

    // ----- セクション要約（折りたたみ時表示） -----
    private string _cpuSummary = string.Empty;
    public string CpuSummary { get => _cpuSummary; private set => SetProperty(ref _cpuSummary, value); }

    private string _gpuSummary = string.Empty;
    public string GpuSummary { get => _gpuSummary; private set => SetProperty(ref _gpuSummary, value); }

    private string _memorySummary = string.Empty;
    public string MemorySummary { get => _memorySummary; private set => SetProperty(ref _memorySummary, value); }

    private string _storageSummary = string.Empty;
    public string StorageSummary { get => _storageSummary; private set => SetProperty(ref _storageSummary, value); }

    private string _memoryModulesSummary = string.Empty;
    public string MemoryModulesSummary { get => _memoryModulesSummary; private set => SetProperty(ref _memoryModulesSummary, value); }

    private string _networkAllSummary = string.Empty;
    public string NetworkAllSummary { get => _networkAllSummary; private set => SetProperty(ref _networkAllSummary, value); }

    private string _networkSummary = string.Empty;
    public string NetworkSummary { get => _networkSummary; private set => SetProperty(ref _networkSummary, value); }

    private string _thermalSummary = string.Empty;
    public string ThermalSummary { get => _thermalSummary; private set => SetProperty(ref _thermalSummary, value); }

    private string _processSummary = string.Empty;
    public string ProcessSummary { get => _processSummary; private set => SetProperty(ref _processSummary, value); }

    // ----- CPU -----
    private string _cpuModelText = string.Empty;
    public string CpuModelText { get => _cpuModelText; private set => SetProperty(ref _cpuModelText, value); }

    private string _cpuUsageText = "0%";
    public string CpuUsageText { get => _cpuUsageText; private set => SetProperty(ref _cpuUsageText, value); }

    private string _cpuCurrentClockText = string.Empty;
    public string CpuCurrentClockText { get => _cpuCurrentClockText; private set => SetProperty(ref _cpuCurrentClockText, value); }

    private double _cpuGaugeValue;
    public double CpuGaugeValue { get => _cpuGaugeValue; private set => SetProperty(ref _cpuGaugeValue, value); }

    private float[] _cpuSparkline = Array.Empty<float>();
    public float[] CpuSparkline { get => _cpuSparkline; private set => SetProperty(ref _cpuSparkline, value); }

    private IReadOnlyList<double> _cpuCoreValues = Array.Empty<double>();
    public IReadOnlyList<double> CpuCoreValues { get => _cpuCoreValues; private set => SetProperty(ref _cpuCoreValues, value); }

    private string _cpuCoreCountText = string.Empty;
    public string CpuCoreCountText { get => _cpuCoreCountText; private set => SetProperty(ref _cpuCoreCountText, value); }

    private string _cpuTemperatureText = "—";
    public string CpuTemperatureText { get => _cpuTemperatureText; private set => SetProperty(ref _cpuTemperatureText, value); }

    private string _cpuPowerText = "—";
    public string CpuPowerText { get => _cpuPowerText; private set => SetProperty(ref _cpuPowerText, value); }

    // ----- GPU -----
    private string _gpuAdapterNameText = string.Empty;
    public string GpuAdapterNameText { get => _gpuAdapterNameText; private set => SetProperty(ref _gpuAdapterNameText, value); }

    private string _gpuUsageText = "0%";
    public string GpuUsageText { get => _gpuUsageText; private set => SetProperty(ref _gpuUsageText, value); }

    private string _gpuCoreClockText = "—";
    public string GpuCoreClockText { get => _gpuCoreClockText; private set => SetProperty(ref _gpuCoreClockText, value); }

    private double _gpuGaugeValue;
    public double GpuGaugeValue { get => _gpuGaugeValue; private set => SetProperty(ref _gpuGaugeValue, value); }

    private float[] _gpuSparkline = Array.Empty<float>();
    public float[] GpuSparkline { get => _gpuSparkline; private set => SetProperty(ref _gpuSparkline, value); }

    private double _gpu3DValue;
    public double Gpu3DValue { get => _gpu3DValue; private set => SetProperty(ref _gpu3DValue, value); }

    private double _gpuCopyValue;
    public double GpuCopyValue { get => _gpuCopyValue; private set => SetProperty(ref _gpuCopyValue, value); }

    private double _gpuVideoValue;
    public double GpuVideoValue { get => _gpuVideoValue; private set => SetProperty(ref _gpuVideoValue, value); }

    private double _gpuComputeValue;
    public double GpuComputeValue { get => _gpuComputeValue; private set => SetProperty(ref _gpuComputeValue, value); }

    private string _gpuTemperatureText = "—";
    public string GpuTemperatureText { get => _gpuTemperatureText; private set => SetProperty(ref _gpuTemperatureText, value); }

    private string _gpuHotspotTemperatureText = "—";
    public string GpuHotspotTemperatureText { get => _gpuHotspotTemperatureText; private set => SetProperty(ref _gpuHotspotTemperatureText, value); }

    private string _gpuFanText = "—";
    public string GpuFanText { get => _gpuFanText; private set => SetProperty(ref _gpuFanText, value); }

    private string _gpuPowerText = "—";
    public string GpuPowerText { get => _gpuPowerText; private set => SetProperty(ref _gpuPowerText, value); }

    private string _gpuMemoryClockText = "—";
    public string GpuMemoryClockText { get => _gpuMemoryClockText; private set => SetProperty(ref _gpuMemoryClockText, value); }

    private string _gpuVramText = string.Empty;
    public string GpuVramText { get => _gpuVramText; private set => SetProperty(ref _gpuVramText, value); }

    private double _gpuVramGaugeValue;
    public double GpuVramGaugeValue { get => _gpuVramGaugeValue; private set => SetProperty(ref _gpuVramGaugeValue, value); }

    private float[] _gpuTemperatureSparkline = Array.Empty<float>();
    public float[] GpuTemperatureSparkline { get => _gpuTemperatureSparkline; private set => SetProperty(ref _gpuTemperatureSparkline, value); }

    // ----- メモリ -----
    private string _memoryUsageText = string.Empty;
    public string MemoryUsageText { get => _memoryUsageText; private set => SetProperty(ref _memoryUsageText, value); }

    private string _memoryUsagePercentText = string.Empty;
    public string MemoryUsagePercentText { get => _memoryUsagePercentText; private set => SetProperty(ref _memoryUsagePercentText, value); }

    private double _memoryGaugeValue;
    public double MemoryGaugeValue { get => _memoryGaugeValue; private set => SetProperty(ref _memoryGaugeValue, value); }

    private float[] _memorySparkline = Array.Empty<float>();
    public float[] MemorySparkline { get => _memorySparkline; private set => SetProperty(ref _memorySparkline, value); }

    // 積み上げバー: [使用中, 変更済み, スタンバイ, 空き]。圧縮済みは使用中セグメントへの重ね塗り (MemoryCompressedBytes) として渡す。
    private IReadOnlyList<double> _memoryStackedValues = Array.Empty<double>();
    public IReadOnlyList<double> MemoryStackedValues { get => _memoryStackedValues; private set => SetProperty(ref _memoryStackedValues, value); }

    private double _memoryCompressedBytes;
    public double MemoryCompressedBytes { get => _memoryCompressedBytes; private set => SetProperty(ref _memoryCompressedBytes, value); }

    private string _memoryCachedText = "—";
    public string MemoryCachedText { get => _memoryCachedText; private set => SetProperty(ref _memoryCachedText, value); }

    private string _memoryStandbyText = "—";
    public string MemoryStandbyText { get => _memoryStandbyText; private set => SetProperty(ref _memoryStandbyText, value); }

    private string _memoryModifiedText = "—";
    public string MemoryModifiedText { get => _memoryModifiedText; private set => SetProperty(ref _memoryModifiedText, value); }

    private string _memoryFreeText = "—";
    public string MemoryFreeText { get => _memoryFreeText; private set => SetProperty(ref _memoryFreeText, value); }

    private string _memoryCompressedText = "—";
    public string MemoryCompressedText { get => _memoryCompressedText; private set => SetProperty(ref _memoryCompressedText, value); }

    private string _memoryPoolPagedText = "—";
    public string MemoryPoolPagedText { get => _memoryPoolPagedText; private set => SetProperty(ref _memoryPoolPagedText, value); }

    private string _memoryPoolNonPagedText = "—";
    public string MemoryPoolNonPagedText { get => _memoryPoolNonPagedText; private set => SetProperty(ref _memoryPoolNonPagedText, value); }

    private string _memorySystemCacheText = "—";
    public string MemorySystemCacheText { get => _memorySystemCacheText; private set => SetProperty(ref _memorySystemCacheText, value); }

    private string _memoryCommitText = string.Empty;
    public string MemoryCommitText { get => _memoryCommitText; private set => SetProperty(ref _memoryCommitText, value); }

    private string _memoryCommitPeakText = "—";
    public string MemoryCommitPeakText { get => _memoryCommitPeakText; private set => SetProperty(ref _memoryCommitPeakText, value); }

    private string _memoryHandlesLine = string.Empty;
    public string MemoryHandlesLine { get => _memoryHandlesLine; private set => SetProperty(ref _memoryHandlesLine, value); }

    private bool _memoryHasHardwareReserved;
    public bool MemoryHasHardwareReserved { get => _memoryHasHardwareReserved; private set => SetProperty(ref _memoryHasHardwareReserved, value); }

    private string _memoryHardwareReservedText = string.Empty;
    public string MemoryHardwareReservedText { get => _memoryHardwareReservedText; private set => SetProperty(ref _memoryHardwareReservedText, value); }

    private string _memorySlotSummaryText = "—";
    public string MemorySlotSummaryText { get => _memorySlotSummaryText; private set => SetProperty(ref _memorySlotSummaryText, value); }

    private IReadOnlyList<MemoryModuleRowViewModel> _memoryModules = Array.Empty<MemoryModuleRowViewModel>();
    public IReadOnlyList<MemoryModuleRowViewModel> MemoryModules { get => _memoryModules; private set => SetProperty(ref _memoryModules, value); }

    private IReadOnlyList<PageFileRowViewModel> _pageFileRows = Array.Empty<PageFileRowViewModel>();
    public IReadOnlyList<PageFileRowViewModel> PageFileRows { get => _pageFileRows; private set => SetProperty(ref _pageFileRows, value); }

    // ----- ストレージ -----
    // 読み込みと書き込みは 1 つのグラフに 2 色で重ねて描いているので、値も系列ごとに分けて持ち、
    // XAML 側でグラフの線と同じ色を付けて凡例を兼ねさせる。
    private string _diskReadLineText = string.Empty;
    public string DiskReadLineText { get => _diskReadLineText; private set => SetProperty(ref _diskReadLineText, value); }

    private string _diskWriteLineText = string.Empty;
    public string DiskWriteLineText { get => _diskWriteLineText; private set => SetProperty(ref _diskWriteLineText, value); }

    private float[] _diskReadSparkline = Array.Empty<float>();
    public float[] DiskReadSparkline { get => _diskReadSparkline; private set => SetProperty(ref _diskReadSparkline, value); }

    private float[] _diskWriteSparkline = Array.Empty<float>();
    public float[] DiskWriteSparkline { get => _diskWriteSparkline; private set => SetProperty(ref _diskWriteSparkline, value); }

    // ----- ネットワーク -----
    private string _networkNicName = string.Empty;
    public string NetworkNicName { get => _networkNicName; private set => SetProperty(ref _networkNicName, value); }

    private string _networkDownText = string.Empty;
    public string NetworkDownText { get => _networkDownText; private set => SetProperty(ref _networkDownText, value); }

    private string _networkUpText = string.Empty;
    public string NetworkUpText { get => _networkUpText; private set => SetProperty(ref _networkUpText, value); }

    private float[] _networkDownSparkline = Array.Empty<float>();
    public float[] NetworkDownSparkline { get => _networkDownSparkline; private set => SetProperty(ref _networkDownSparkline, value); }

    private float[] _networkUpSparkline = Array.Empty<float>();
    public float[] NetworkUpSparkline { get => _networkUpSparkline; private set => SetProperty(ref _networkUpSparkline, value); }

    /// <summary>主表示 NIC（受信+送信が最大）を除いた残りの NIC 一覧。仮想 NIC が多い環境向けの折りたたみ表示用。</summary>
    private IReadOnlyList<NetworkInterfaceRowViewModel> _networkAllRows = Array.Empty<NetworkInterfaceRowViewModel>();
    public IReadOnlyList<NetworkInterfaceRowViewModel> NetworkAllRows { get => _networkAllRows; private set => SetProperty(ref _networkAllRows, value); }

    // ----- 温度・ファン -----
    private bool _isThermalElevated;
    public bool IsThermalElevated { get => _isThermalElevated; private set => SetProperty(ref _isThermalElevated, value); }

    private bool _isThermalDataAvailable;
    public bool IsThermalDataAvailable { get => _isThermalDataAvailable; private set => SetProperty(ref _isThermalDataAvailable, value); }

    private string _thermalCpuPackageText = "—";
    public string ThermalCpuPackageText { get => _thermalCpuPackageText; private set => SetProperty(ref _thermalCpuPackageText, value); }

    private string _thermalCpuPowerText = "—";
    public string ThermalCpuPowerText { get => _thermalCpuPowerText; private set => SetProperty(ref _thermalCpuPowerText, value); }

    private string _thermalMotherboardText = "—";
    public string ThermalMotherboardText { get => _thermalMotherboardText; private set => SetProperty(ref _thermalMotherboardText, value); }

    private string _thermalVrmText = "—";
    public string ThermalVrmText { get => _thermalVrmText; private set => SetProperty(ref _thermalVrmText, value); }

    private IReadOnlyList<SensorRowViewModel> _thermalCoreTemperatures = Array.Empty<SensorRowViewModel>();
    public IReadOnlyList<SensorRowViewModel> ThermalCoreTemperatures { get => _thermalCoreTemperatures; private set => SetProperty(ref _thermalCoreTemperatures, value); }

    private IReadOnlyList<SensorRowViewModel> _thermalOtherTemperatures = Array.Empty<SensorRowViewModel>();
    public IReadOnlyList<SensorRowViewModel> ThermalOtherTemperatures { get => _thermalOtherTemperatures; private set => SetProperty(ref _thermalOtherTemperatures, value); }

    private IReadOnlyList<SensorRowViewModel> _thermalFans = Array.Empty<SensorRowViewModel>();
    public IReadOnlyList<SensorRowViewModel> ThermalFans { get => _thermalFans; private set => SetProperty(ref _thermalFans, value); }

    private void OnSnapshotAvailable(MetricsSnapshot snapshot)
    {
        // MetricsHub のサンプリングループ（バックグラウンドスレッド）から呼ばれるため、
        // UI スレッドへマーシャリングしてから状態を反映する。
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
            {
                Apply(snapshot);
            }
        }));
    }

    private void Apply(MetricsSnapshot s)
    {
        ApplyCpu(s.Cpu, s.Thermal);
        ApplyGpu(s.Gpu);
        ApplyMemory(s.Memory);
        ApplyStorage(s.Disk, s.Volumes, s.Thermal);
        ApplyNetwork(s.Network);
        ApplyThermal(s.Thermal);
        UpdateProcesses(s.Processes.Processes);
    }

    private void ApplyCpu(CpuSnapshot cpu, ThermalSnapshot thermal)
    {
        CpuModelText = string.IsNullOrWhiteSpace(cpu.ModelName) ? "CPU" : cpu.ModelName;
        CpuUsageText = ByteFormatter.Percent(cpu.TotalUsagePercent);
        CpuGaugeValue = cpu.TotalUsagePercent;
        CpuCoreValues = cpu.PerCoreUsagePercent;
        CpuSparkline = _hub.History.Snapshot(MetricSeries.CpuTotal);

        double effectiveClock = cpu.CurrentClockMhz > 0 ? cpu.CurrentClockMhz : cpu.BaseClockMhz;
        CpuCurrentClockText = effectiveClock > 0 ? ByteFormatter.Clock(effectiveClock) : "—";

        CpuCoreCountText = string.Create(
            CultureInfo.InvariantCulture,
            $"{cpu.PhysicalCoreCount}C / {cpu.LogicalCoreCount}T · ベース {ByteFormatter.Clock(cpu.BaseClockMhz)}");

        // CpuSnapshot.PackageTemperatureC/PackagePowerWatts は CpuProvider の範囲外で常に null。
        // 実体は ThermalSnapshot（LibreHardwareMonitor、管理者時のみ）から合成する。
        double? packageTemp = cpu.PackageTemperatureC ?? thermal.CpuPackageTemperatureC;
        double? packagePower = cpu.PackagePowerWatts ?? thermal.CpuPackagePowerWatts;
        CpuTemperatureText = ByteFormatter.Temperature(packageTemp);
        CpuPowerText = ByteFormatter.Watts(packagePower);

        var summaryParts = new List<string>(3) { CpuUsageText };
        if (effectiveClock > 0)
        {
            summaryParts.Add(ByteFormatter.Clock(effectiveClock));
        }

        if (packageTemp is double summaryTemp)
        {
            summaryParts.Add(ByteFormatter.Temperature(summaryTemp));
        }

        CpuSummary = string.Join(" · ", summaryParts);
    }

    private void ApplyGpu(GpuSnapshot gpu)
    {
        GpuAdapterSnapshot adapter = gpu.Adapters.Count > 0 ? gpu.Adapters[0] : GpuAdapterSnapshot.Empty;

        GpuAdapterNameText = string.IsNullOrWhiteSpace(adapter.Name) ? "GPU" : adapter.Name;
        GpuUsageText = ByteFormatter.Percent(gpu.TotalUsagePercent);
        GpuCoreClockText = FormatClockOrDash(adapter.CoreClockMhz);
        GpuGaugeValue = gpu.TotalUsagePercent;
        GpuSparkline = _hub.History.Snapshot(MetricSeries.GpuTotal);
        Gpu3DValue = adapter.Engine3DPercent;
        GpuCopyValue = adapter.EngineCopyPercent;
        GpuVideoValue = adapter.EngineVideoPercent;
        GpuComputeValue = adapter.EngineComputePercent;

        GpuTemperatureText = ByteFormatter.Temperature(adapter.TemperatureC);
        GpuHotspotTemperatureText = ByteFormatter.Temperature(adapter.HotspotTemperatureC);
        GpuFanText = FormatFan(adapter.FanPercent, adapter.FanRpm);
        GpuPowerText = FormatPower(adapter.PowerWatts, adapter.PowerLimitWatts);
        GpuMemoryClockText = FormatClockOrDash(adapter.MemoryClockMhz);

        GpuVramText = string.Create(
            CultureInfo.InvariantCulture,
            $"{ByteFormatter.Bytes(adapter.DedicatedUsedBytes)} / {ByteFormatter.Bytes(adapter.DedicatedTotalBytes)}");
        GpuVramGaugeValue = adapter.DedicatedTotalBytes > 0
            ? (double)adapter.DedicatedUsedBytes / adapter.DedicatedTotalBytes * 100.0
            : 0.0;

        GpuTemperatureSparkline = _hub.History.Snapshot(MetricSeries.GpuTemperature);

        var summaryParts = new List<string>(3) { GpuUsageText };
        if (adapter.TemperatureC is double summaryTemp)
        {
            summaryParts.Add(ByteFormatter.Temperature(summaryTemp));
        }

        if (adapter.PowerWatts is double summaryPower)
        {
            summaryParts.Add(summaryPower.ToString("F0", CultureInfo.InvariantCulture) + " W");
        }

        GpuSummary = string.Join(" · ", summaryParts);
    }

    private void ApplyMemory(MemorySnapshot memory)
    {
        MemoryUsageText = string.Create(
            CultureInfo.InvariantCulture,
            $"{ByteFormatter.Bytes(memory.UsedBytes)} / {ByteFormatter.Bytes(memory.TotalBytes)}");
        MemoryUsagePercentText = ByteFormatter.Percent(memory.UsedPercent);
        MemoryGaugeValue = memory.UsedPercent;
        MemorySparkline = _hub.History.Snapshot(MetricSeries.MemoryUsedPercent);

        MemoryStackedValues = new double[]
        {
            memory.UsedBytes,
            memory.ModifiedBytes,
            memory.StandbyBytes,
            memory.FreeBytes,
        };
        MemoryCompressedBytes = memory.CompressedBytes;

        MemoryCachedText = ByteFormatter.Bytes(memory.CachedBytes);
        MemoryStandbyText = ByteFormatter.Bytes(memory.StandbyBytes);
        MemoryModifiedText = ByteFormatter.Bytes(memory.ModifiedBytes);
        MemoryFreeText = ByteFormatter.Bytes(memory.FreeBytes);
        MemoryCompressedText = ByteFormatter.Bytes(memory.CompressedBytes);
        MemoryPoolPagedText = ByteFormatter.Bytes(memory.PoolPagedBytes);
        MemoryPoolNonPagedText = ByteFormatter.Bytes(memory.PoolNonPagedBytes);
        MemorySystemCacheText = ByteFormatter.Bytes(memory.SystemCacheBytes);
        MemoryCommitText = string.Create(
            CultureInfo.InvariantCulture,
            $"{ByteFormatter.Bytes(memory.CommittedBytes)} / {ByteFormatter.Bytes(memory.CommitLimitBytes)}");
        MemoryCommitPeakText = ByteFormatter.Bytes(memory.CommitPeakBytes);

        MemoryHandlesLine = string.Create(
            CultureInfo.InvariantCulture,
            $"ハンドル {memory.HandleCount:N0} · プロセス {memory.ProcessCount:N0} · スレッド {memory.ThreadCount:N0}");

        MemoryHasHardwareReserved = memory.HardwareReservedBytes > 0;
        if (MemoryHasHardwareReserved)
        {
            double reservedMb = memory.HardwareReservedBytes / 1024.0 / 1024.0;
            MemoryHardwareReservedText = string.Create(
                CultureInfo.InvariantCulture,
                $"ハードウェア予約 {reservedMb:N0} MB");
        }

        MemorySlotSummaryText = BuildSlotSummary(memory);
        MemoryModules = BuildMemoryModules(memory.Modules);
        MemoryModulesSummary = memory.Modules.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{memory.Modules.Count} 本")
            : "—";

        PageFileRows = BuildPageFileRows(memory.PageFiles);

        MemorySummary = string.Create(CultureInfo.InvariantCulture, $"{MemoryUsageText} · {MemoryUsagePercentText}");
    }

    private static string BuildSlotSummary(MemorySnapshot memory)
    {
        if (memory.Modules.Count == 0 || memory.SlotsTotal == 0)
        {
            return "—";
        }

        MemoryModuleInfo first = memory.Modules[0];
        double capacityGb = first.CapacityBytes / 1024.0 / 1024.0 / 1024.0;
        string type = string.IsNullOrWhiteSpace(first.MemoryType) ? "" : first.MemoryType;
        string speed = memory.SpeedMhz > 0 ? memory.SpeedMhz.ToString(CultureInfo.InvariantCulture) : "?";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{capacityGb:F0}GB {type}-{speed} × {memory.Modules.Count}  ({memory.SlotsUsed}/{memory.SlotsTotal} スロット)");
    }

    private static IReadOnlyList<MemoryModuleRowViewModel> BuildMemoryModules(IReadOnlyList<MemoryModuleInfo> modules)
    {
        if (modules.Count == 0)
        {
            return Array.Empty<MemoryModuleRowViewModel>();
        }

        var rows = new MemoryModuleRowViewModel[modules.Count];
        for (int i = 0; i < modules.Count; i++)
        {
            rows[i] = new MemoryModuleRowViewModel(modules[i]);
        }

        return rows;
    }

    private static IReadOnlyList<PageFileRowViewModel> BuildPageFileRows(IReadOnlyList<PageFileInfo> pageFiles)
    {
        if (pageFiles.Count == 0)
        {
            return Array.Empty<PageFileRowViewModel>();
        }

        var rows = new PageFileRowViewModel[pageFiles.Count];
        for (int i = 0; i < pageFiles.Count; i++)
        {
            rows[i] = new PageFileRowViewModel(pageFiles[i]);
        }

        return rows;
    }

    /// <summary>
    /// 「ストレージ」セクション（物理ディスク見出し行 + 配下ボリューム行）を組み立てる。
    /// 物理ディスク番号の昇順に「ディスク見出し行（キー "#3"）」→「そのディスクへ紐づくボリューム行
    /// （キーはドライブレター、ドライブレター昇順）」を並べる。物理ディスクへ解決できないローカルボリューム
    /// はグループ群の後ろに見出し無しで、ネットワークドライブは最後にまとめて続ける。
    /// "#" 始まりの見出しキーとドライブレター（英字+":"）のボリュームキーは形が異なるため衝突しない。
    /// R/W・温度は物理ディスク見出し行に集約したので、複数ボリュームへの重複表示回避ロジックは不要になった。
    /// </summary>
    private void ApplyStorage(DiskSnapshot disk, IReadOnlyList<VolumeSnapshot> volumes, ThermalSnapshot thermal)
    {
        DiskReadLineText = string.Create(
            CultureInfo.InvariantCulture,
            $"R {ByteFormatter.BytesPerSec(disk.TotalReadBytesPerSec)}");
        DiskWriteLineText = string.Create(
            CultureInfo.InvariantCulture,
            $"W {ByteFormatter.BytesPerSec(disk.TotalWriteBytesPerSec)}");
        DiskReadSparkline = _hub.History.Snapshot(MetricSeries.DiskReadBytesPerSec);
        DiskWriteSparkline = _hub.History.Snapshot(MetricSeries.DiskWriteBytesPerSec);

        var devicesByNumber = new Dictionary<int, DiskDeviceSnapshot>();
        foreach (DiskDeviceSnapshot d in disk.Devices)
        {
            devicesByNumber[d.PhysicalDriveNumber] = d;
        }

        // 物理ディスク番号 → 配下ボリューム（ドライブレター昇順）。解決できないローカルボリュームと
        // ネットワークドライブは別リストへ振り分け、グループ群の後ろにまとめて続ける。
        var volumesByDisk = new Dictionary<int, List<VolumeSnapshot>>();
        var unresolvedVolumes = new List<VolumeSnapshot>();
        var networkVolumes = new List<VolumeSnapshot>();

        ulong totalCapacityBytes = 0;
        ulong usedCapacityBytes = 0;

        foreach (VolumeSnapshot v in volumes)
        {
            // 全体使用率（overallPercent）にはこの PC のローカルディスク容量だけを積算する。
            // ネットワークドライブは NAS 側の全容量を持ち込んでしまい、ローカルの使用状況とは
            // 無関係に率を歪めるため除外する（旧実装からの既存挙動を維持）。
            if (v.Kind != VolumeKind.Network && v.IsReady && v.TotalBytes > 0)
            {
                totalCapacityBytes += v.TotalBytes;
                usedCapacityBytes += v.UsedBytes;
            }

            if (v.Kind == VolumeKind.Network)
            {
                networkVolumes.Add(v);
            }
            else if (v.PhysicalDriveNumber is int pd && devicesByNumber.ContainsKey(pd))
            {
                if (!volumesByDisk.TryGetValue(pd, out List<VolumeSnapshot>? list))
                {
                    list = new List<VolumeSnapshot>();
                    volumesByDisk[pd] = list;
                }

                list.Add(v);
            }
            else
            {
                unresolvedVolumes.Add(v);
            }
        }

        foreach (List<VolumeSnapshot> list in volumesByDisk.Values)
        {
            list.Sort((a, b) => string.CompareOrdinal(a.DriveLetter, b.DriveLetter));
        }

        unresolvedVolumes.Sort((a, b) => string.CompareOrdinal(a.DriveLetter, b.DriveLetter));
        networkVolumes.Sort((a, b) => string.CompareOrdinal(a.DriveLetter, b.DriveLetter));

        DiskDeviceSnapshot[] sortedDevices = disk.Devices.OrderBy(d => d.PhysicalDriveNumber).ToArray();

        var desiredKeys = new List<string>(volumes.Count + disk.Devices.Count);
        var applyActions = new Dictionary<string, Action<StorageRowViewModel>>(volumes.Count + disk.Devices.Count);

        foreach (DiskDeviceSnapshot d in sortedDevices)
        {
            string diskKey = "#" + d.PhysicalDriveNumber.ToString(CultureInfo.InvariantCulture);
            desiredKeys.Add(diskKey);
            applyActions[diskKey] = BuildDiskRowAction(d, thermal);

            if (volumesByDisk.TryGetValue(d.PhysicalDriveNumber, out List<VolumeSnapshot>? childVolumes))
            {
                foreach (VolumeSnapshot v in childVolumes)
                {
                    desiredKeys.Add(v.DriveLetter);
                    applyActions[v.DriveLetter] = BuildVolumeRowAction(v, StorageRowKind.Volume);
                }
            }
        }

        foreach (VolumeSnapshot v in unresolvedVolumes)
        {
            desiredKeys.Add(v.DriveLetter);
            applyActions[v.DriveLetter] = BuildVolumeRowAction(v, StorageRowKind.Volume);
        }

        foreach (VolumeSnapshot v in networkVolumes)
        {
            desiredKeys.Add(v.DriveLetter);
            applyActions[v.DriveLetter] = BuildVolumeRowAction(v, StorageRowKind.Network);
        }

        UpdateStorageRows(desiredKeys, applyActions);

        double overallPercent = totalCapacityBytes > 0 ? 100.0 * usedCapacityBytes / totalCapacityBytes : 0.0;

        StorageSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"{volumes.Count} ドライブ · {ByteFormatter.Percent(overallPercent)} · R {ShortRate(disk.TotalReadBytesPerSec)} W {ShortRate(disk.TotalWriteBytesPerSec)}");
    }

    /// <summary>物理ディスク見出し行1件分の更新アクションを組み立てる。</summary>
    private static Action<StorageRowViewModel> BuildDiskRowAction(DiskDeviceSnapshot d, ThermalSnapshot thermal)
    {
        string modelText = string.IsNullOrWhiteSpace(d.Model) ? $"Disk {d.PhysicalDriveNumber}" : d.Model;
        string busTypeText = string.IsNullOrEmpty(d.BusType) ? "" : d.BusType + (d.IsSsd ? " SSD" : " HDD");
        string readText = ShortRate(d.ReadBytesPerSec);
        string writeText = ShortRate(d.WriteBytesPerSec);
        string busyPercentText = ByteFormatter.Percent(d.BusyPercent);
        double? temperature = d.TemperatureC ?? FindStorageTemperature(thermal.StorageTemperatures, d.Model);
        string temperatureText = ByteFormatter.Temperature(temperature);
        string tooltipText = BuildDiskTooltip(d);
        double writeBytesPerSec = d.WriteBytesPerSec;

        return row => row.UpdateAsDisk(
            modelText, busTypeText, readText, writeText, busyPercentText, temperatureText, writeBytesPerSec, tooltipText);
    }

    /// <summary>ボリューム行/ネットワーク行1件分の更新アクションを組み立てる。列構成は共通なので
    /// <paramref name="kind"/>（Volume / Network）だけで振る舞いを切り替える。</summary>
    private static Action<StorageRowViewModel> BuildVolumeRowAction(VolumeSnapshot v, StorageRowKind kind)
    {
        string driveLetterText = v.DriveLetter;
        string labelText = kind == StorageRowKind.Network
            ? (v.NetworkPath ?? "")
            : (string.IsNullOrWhiteSpace(v.Label) ? "" : v.Label!);
        bool isReady = v.IsReady;
        bool hasCapacity = v.IsReady && v.TotalBytes > 0;
        string usagePercentText = v.IsReady ? ByteFormatter.Percent(v.UsedPercent) : "—";
        double usedPercent = v.UsedPercent;
        string capacityText = hasCapacity ? ByteFormatter.BytesPair(v.UsedBytes, v.TotalBytes) : "—";
        string tooltipText = BuildVolumeTooltip(v);

        return row => row.UpdateAsVolume(
            kind, driveLetterText, labelText, isReady, hasCapacity, usedPercent, usagePercentText, capacityText, tooltipText);
    }

    /// <summary>
    /// 全ストレージ行（物理ディスク見出し行 + ボリューム/ネットワーク行）を <see cref="StorageRowViewModel.Key"/> で
    /// 照合しながら差分更新する。毎回 Clear→Add するとちらつくため、足りない/余る分だけ追加削除する
    /// （<see cref="UpdateProcesses"/> と同じ方針）。<paramref name="desiredKeys"/> の順序を維持する。
    /// </summary>
    private void UpdateStorageRows(List<string> desiredKeys, Dictionary<string, Action<StorageRowViewModel>> applyActions)
    {
        for (int i = StorageRows.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(StorageRows[i].Key))
            {
                StorageRows.RemoveAt(i);
            }
        }

        for (int i = 0; i < desiredKeys.Count; i++)
        {
            string key = desiredKeys[i];

            int existingIndex = -1;
            for (int j = 0; j < StorageRows.Count; j++)
            {
                if (StorageRows[j].Key == key)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                var row = new StorageRowViewModel(key);
                applyActions[key](row);
                int insertIndex = Math.Min(i, StorageRows.Count);
                StorageRows.Insert(insertIndex, row);
            }
            else
            {
                applyActions[key](StorageRows[existingIndex]);
                if (existingIndex != i)
                {
                    StorageRows.Move(existingIndex, i);
                }
            }
        }
    }

    /// <summary>物理ディスク見出し行のツールチップ。バス種別・Busy% は行本体に表示されるためここでは出さず、
    /// 行に出ていない情報（物理ディスク番号・総容量）を出す。モデル名は行側が TextTrimming で省略され得るため、
    /// 確認用にフルの値をここにも残す。</summary>
    private static string BuildDiskTooltip(DiskDeviceSnapshot device)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(device.Model))
        {
            parts.Add(device.Model);
        }

        parts.Add(string.Create(CultureInfo.InvariantCulture, $"物理ディスク {device.PhysicalDriveNumber}"));

        if (device.CapacityBytes > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"総容量 {ByteFormatter.Bytes(device.CapacityBytes)}"));
        }

        // 行の Busy% 列には見出しラベルを置く幅が無い（付けるとモデル名列を削ることになる）ため、
        // 何の百分率かはここで補う。
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"Busy {ByteFormatter.Percent(device.BusyPercent)}"));

        return string.Join(" · ", parts);
    }

    /// <summary>ボリューム行/ネットワーク行のツールチップ。ファイルシステムとボリューム総容量は行本体に
    /// 出ないのでここに出す（<see cref="VolumeSnapshot.FileSystem"/> は従来どこからも参照されていなかった）。</summary>
    private static string BuildVolumeTooltip(VolumeSnapshot v)
    {
        if (!v.IsReady)
        {
            return "";
        }

        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(v.FileSystem))
        {
            parts.Add(v.FileSystem!);
        }

        if (v.TotalBytes > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"総容量 {ByteFormatter.Bytes(v.TotalBytes)}"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// ストレージ行専用の極短縮レート表示。視覚ノイズを減らすため単位は1文字のみ、区切り記号やスペースは付けない。
    /// 1024基数で 999 / 12M / 1.2G のように整形し、実質ゼロ（1 B/s 未満）は控えめな "·" にする。
    /// </summary>
    private static string ShortRate(double bytesPerSec)
    {
        double v = bytesPerSec;
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 1.0)
        {
            return "·";
        }

        string[] units = ["", "K", "M", "G", "T"];
        int unitIndex = 0;
        while (v >= 1024.0 && unitIndex < units.Length - 1)
        {
            v /= 1024.0;
            unitIndex++;
        }

        string numberText = unitIndex == 0
            ? Math.Round(v).ToString(CultureInfo.InvariantCulture)
            : (v < 10.0 ? v.ToString("F1", CultureInfo.InvariantCulture) : Math.Round(v).ToString(CultureInfo.InvariantCulture));

        return numberText + units[unitIndex];
    }

    private void ApplyNetwork(NetworkSnapshot network)
    {
        // 仮想 NIC が多数常駐する環境向けに、受信+送信の合計が最大の NIC を主表示にする。
        int primaryIndex = -1;
        double primaryTotal = -1;
        for (int i = 0; i < network.Interfaces.Count; i++)
        {
            NetworkInterfaceSnapshot candidate = network.Interfaces[i];
            double total = candidate.ReceiveBytesPerSec + candidate.SendBytesPerSec;
            if (total > primaryTotal)
            {
                primaryTotal = total;
                primaryIndex = i;
            }
        }

        NetworkInterfaceSnapshot nic = primaryIndex >= 0 ? network.Interfaces[primaryIndex] : NetworkInterfaceSnapshot.Empty;

        NetworkNicName = nic.Name.Length > 0 ? nic.Name : "ネットワーク未接続";
        NetworkDownText = "↓ " + ByteFormatter.Bits(network.TotalReceiveBytesPerSec);
        NetworkUpText = "↑ " + ByteFormatter.Bits(network.TotalSendBytesPerSec);
        NetworkDownSparkline = _hub.History.Snapshot(MetricSeries.NetReceiveBytesPerSec);
        NetworkUpSparkline = _hub.History.Snapshot(MetricSeries.NetSendBytesPerSec);

        NetworkAllRows = BuildNetworkAllRows(network.Interfaces, primaryIndex);
        NetworkAllSummary = string.Create(CultureInfo.InvariantCulture, $"{NetworkAllRows.Count} 個");

        NetworkSummary = string.Create(CultureInfo.InvariantCulture, $"{NetworkDownText} {NetworkUpText}");
    }

    private static IReadOnlyList<NetworkInterfaceRowViewModel> BuildNetworkAllRows(IReadOnlyList<NetworkInterfaceSnapshot> interfaces, int primaryIndex)
    {
        if (interfaces.Count <= 1)
        {
            return Array.Empty<NetworkInterfaceRowViewModel>();
        }

        var rows = new List<NetworkInterfaceRowViewModel>(interfaces.Count - 1);
        for (int i = 0; i < interfaces.Count; i++)
        {
            if (i == primaryIndex)
            {
                continue;
            }

            rows.Add(new NetworkInterfaceRowViewModel(interfaces[i]));
        }

        return rows;
    }

    private void ApplyThermal(ThermalSnapshot thermal)
    {
        IsThermalElevated = thermal.IsElevated;
        IsThermalDataAvailable = thermal.IsAvailable;

        ThermalCpuPackageText = ByteFormatter.Temperature(thermal.CpuPackageTemperatureC);
        ThermalCpuPowerText = ByteFormatter.Watts(thermal.CpuPackagePowerWatts);
        ThermalMotherboardText = ByteFormatter.Temperature(thermal.MotherboardTemperatureC);
        ThermalVrmText = ByteFormatter.Temperature(thermal.VrmTemperatureC);

        ThermalCoreTemperatures = BuildSensorRows(thermal.CpuCoreTemperatures, v => ByteFormatter.Temperature(v));
        ThermalOtherTemperatures = BuildSensorRows(thermal.OtherTemperatures, v => ByteFormatter.Temperature(v));
        ThermalFans = BuildSensorRows(thermal.Fans, v => ByteFormatter.Rpm((int)Math.Round(v)));

        if (!thermal.IsElevated)
        {
            ThermalSummary = "管理者権限が必要";
        }
        else
        {
            var summaryParts = new List<string>(2);
            if (thermal.CpuPackageTemperatureC is double cpuTemp)
            {
                summaryParts.Add("CPU " + ByteFormatter.Temperature(cpuTemp));
            }

            if (thermal.MotherboardTemperatureC is double mbTemp)
            {
                summaryParts.Add("M/B " + ByteFormatter.Temperature(mbTemp));
            }

            ThermalSummary = summaryParts.Count > 0 ? string.Join(" · ", summaryParts) : "センサー無し";
        }
    }

    private static IReadOnlyList<SensorRowViewModel> BuildSensorRows(IReadOnlyList<SensorReading> readings, Func<double, string> formatValue)
    {
        if (readings.Count == 0)
        {
            return Array.Empty<SensorRowViewModel>();
        }

        var rows = new SensorRowViewModel[readings.Count];
        for (int i = 0; i < readings.Count; i++)
        {
            rows[i] = new SensorRowViewModel(readings[i].Name, formatValue(readings[i].Value));
        }

        return rows;
    }

    /// <summary>
    /// 全ドライブを PhysicalDriveNumber で照合しながら差分更新する。毎回 Clear→Add するとちらつくため、
    /// 足りない/余る分だけ追加削除する（プロセス一覧の差分更新と同じ方針）。
    /// </summary>
    private static double? FindStorageTemperature(IReadOnlyList<SensorReading> storageTemperatures, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        foreach (SensorReading reading in storageTemperatures)
        {
            if (ModelNamesMatch(reading.Name, model))
            {
                return reading.Value;
            }
        }

        return null;
    }

    private static bool ModelNamesMatch(string sensorName, string model)
    {
        if (string.IsNullOrWhiteSpace(sensorName))
        {
            return false;
        }

        return model.Contains(sensorName, StringComparison.OrdinalIgnoreCase)
            || sensorName.Contains(model, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 上位N件のプロセスを、既存行を PID で照合しながら差分更新する。
    /// 毎回 Clear→Add するとちらつくため、足りない/余る分だけ追加削除する。
    /// </summary>
    private void UpdateProcesses(IReadOnlyList<ProcessInfo> processes)
    {
        int topCount = Math.Min(_topProcessCount, processes.Count);

        // 上位に残っていない既存行を削除する。
        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            int pid = Processes[i].Pid;
            bool stillPresent = false;
            for (int j = 0; j < topCount; j++)
            {
                if (processes[j].Pid == pid)
                {
                    stillPresent = true;
                    break;
                }
            }

            if (!stillPresent)
            {
                Processes.RemoveAt(i);
            }
        }

        for (int i = 0; i < topCount; i++)
        {
            ProcessInfo info = processes[i];

            int existingIndex = -1;
            for (int j = 0; j < Processes.Count; j++)
            {
                if (Processes[j].Pid == info.Pid)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                var row = new ProcessRowViewModel(info.Pid);
                row.Update(info);
                int insertIndex = Math.Min(i, Processes.Count);
                Processes.Insert(insertIndex, row);
            }
            else
            {
                Processes[existingIndex].Update(info);
                if (existingIndex != i)
                {
                    Processes.Move(existingIndex, i);
                }
            }
        }
    }

    private static string FormatClockOrDash(double? mhz) => mhz is double v && v > 0 ? ByteFormatter.Clock(v) : "—";

    private static string FormatFan(double? percent, int? rpm)
    {
        string? p = percent is double pv ? ByteFormatter.Percent(pv) : null;
        string? r = rpm is int rv ? ByteFormatter.Rpm(rv) : null;

        if (p is null && r is null)
        {
            return "—";
        }

        if (p is null)
        {
            return r!;
        }

        if (r is null)
        {
            return p!;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{p} · {r}");
    }

    private static string FormatPower(double? watts, double? limitWatts)
    {
        if (watts is not double w)
        {
            return "—";
        }

        string wText = w.ToString("F1", CultureInfo.InvariantCulture);
        return limitWatts is double lim
            ? string.Create(CultureInfo.InvariantCulture, $"{wText} / {lim.ToString("F1", CultureInfo.InvariantCulture)} W")
            : string.Create(CultureInfo.InvariantCulture, $"{wText} W");
    }

    private bool GetExpanded(string key, bool defaultValue = true) =>
        _settings.ExpandedSections.TryGetValue(key, out bool value) ? value : defaultValue;

    private void SetExpanded(string key, bool value, ref bool field, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        _settings.ExpandedSections[key] = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        SettingsStore.Save(_settings);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hub.SnapshotAvailable -= OnSnapshotAvailable;
        _clockTimer.Stop();
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
