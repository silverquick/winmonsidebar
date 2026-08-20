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
        DiskRows = new ObservableCollection<DiskRowViewModel>();
        VolumeRows = new ObservableCollection<VolumeRowViewModel>();

        // 展開状態を設定から復元する。setter を経由すると保存を誘発するため、フィールドへ直接代入する。
        _isCpuExpanded = GetExpanded("cpu");
        _isGpuExpanded = GetExpanded("gpu");
        _isMemoryExpanded = GetExpanded("memory");
        _isMemoryModulesExpanded = GetExpanded("memory-modules", defaultValue: false);
        _isDiskExpanded = GetExpanded("disk");
        _isVolumesExpanded = GetExpanded("volumes");
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

    public ObservableCollection<DiskRowViewModel> DiskRows { get; }

    public ObservableCollection<VolumeRowViewModel> VolumeRows { get; }

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

    private bool _isDiskExpanded;
    public bool IsDiskExpanded { get => _isDiskExpanded; set => SetExpanded("disk", value, ref _isDiskExpanded); }

    private bool _isVolumesExpanded;
    public bool IsVolumesExpanded { get => _isVolumesExpanded; set => SetExpanded("volumes", value, ref _isVolumesExpanded); }

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

    private string _diskSummary = string.Empty;
    public string DiskSummary { get => _diskSummary; private set => SetProperty(ref _diskSummary, value); }

    private string _volumesSummary = string.Empty;
    public string VolumesSummary { get => _volumesSummary; private set => SetProperty(ref _volumesSummary, value); }

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

    // ----- ディスク -----
    private string _diskReadText = string.Empty;
    public string DiskReadText { get => _diskReadText; private set => SetProperty(ref _diskReadText, value); }

    private string _diskWriteText = string.Empty;
    public string DiskWriteText { get => _diskWriteText; private set => SetProperty(ref _diskWriteText, value); }

    private float[] _diskReadSparkline = Array.Empty<float>();
    public float[] DiskReadSparkline { get => _diskReadSparkline; private set => SetProperty(ref _diskReadSparkline, value); }

    private float[] _diskWriteSparkline = Array.Empty<float>();
    public float[] DiskWriteSparkline { get => _diskWriteSparkline; private set => SetProperty(ref _diskWriteSparkline, value); }

    private double _diskBusyGaugeValue;
    public double DiskBusyGaugeValue { get => _diskBusyGaugeValue; private set => SetProperty(ref _diskBusyGaugeValue, value); }

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
        ApplyDisk(s.Disk, s.Thermal);
        ApplyVolumes(s.Volumes);
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

    private void ApplyDisk(DiskSnapshot disk, ThermalSnapshot thermal)
    {
        DiskReadText = "読み取り " + ByteFormatter.BytesPerSec(disk.TotalReadBytesPerSec);
        DiskWriteText = "書き込み " + ByteFormatter.BytesPerSec(disk.TotalWriteBytesPerSec);
        DiskReadSparkline = _hub.History.Snapshot(MetricSeries.DiskReadBytesPerSec);
        DiskWriteSparkline = _hub.History.Snapshot(MetricSeries.DiskWriteBytesPerSec);
        DiskBusyGaugeValue = disk.BusyPercent;
        DiskSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"R {ByteFormatter.BytesPerSec(disk.TotalReadBytesPerSec)} · W {ByteFormatter.BytesPerSec(disk.TotalWriteBytesPerSec)}");

        UpdateDiskRows(disk.Devices, thermal.StorageTemperatures);
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

    /// <summary>
    /// 全論理ドライブを DriveLetter で照合しながら差分更新する。毎回 Clear→Add するとちらつくため、
    /// 足りない/余る分だけ追加削除する（<see cref="UpdateDiskRows"/> と同じ方針）。ドライブレター昇順を維持する。
    /// </summary>
    private void ApplyVolumes(IReadOnlyList<VolumeSnapshot> volumes)
    {
        VolumeSnapshot[] sorted = volumes.ToArray();
        Array.Sort(sorted, (a, b) => string.CompareOrdinal(a.DriveLetter, b.DriveLetter));

        for (int i = VolumeRows.Count - 1; i >= 0; i--)
        {
            string driveLetter = VolumeRows[i].DriveLetter;
            bool stillPresent = false;
            for (int j = 0; j < sorted.Length; j++)
            {
                if (sorted[j].DriveLetter == driveLetter)
                {
                    stillPresent = true;
                    break;
                }
            }

            if (!stillPresent)
            {
                VolumeRows.RemoveAt(i);
            }
        }

        ulong totalBytes = 0;
        ulong usedBytes = 0;

        for (int i = 0; i < sorted.Length; i++)
        {
            VolumeSnapshot v = sorted[i];

            if (v.Kind != VolumeKind.Network && v.IsReady && v.TotalBytes > 0)
            {
                totalBytes += v.TotalBytes;
                usedBytes += v.UsedBytes;
            }

            int existingIndex = -1;
            for (int j = 0; j < VolumeRows.Count; j++)
            {
                if (VolumeRows[j].DriveLetter == v.DriveLetter)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                var row = new VolumeRowViewModel(v.DriveLetter);
                row.Update(v);
                int insertIndex = Math.Min(i, VolumeRows.Count);
                VolumeRows.Insert(insertIndex, row);
            }
            else
            {
                VolumeRows[existingIndex].Update(v);
                if (existingIndex != i)
                {
                    VolumeRows.Move(existingIndex, i);
                }
            }
        }

        double overallPercent = totalBytes > 0 ? 100.0 * usedBytes / totalBytes : 0.0;
        VolumesSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"{sorted.Length} ドライブ · 使用 {overallPercent:F0}%");
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
    private void UpdateDiskRows(IReadOnlyList<DiskDeviceSnapshot> devices, IReadOnlyList<SensorReading> storageTemperatures)
    {
        for (int i = DiskRows.Count - 1; i >= 0; i--)
        {
            int driveNumber = DiskRows[i].PhysicalDriveNumber;
            bool stillPresent = false;
            for (int j = 0; j < devices.Count; j++)
            {
                if (devices[j].PhysicalDriveNumber == driveNumber)
                {
                    stillPresent = true;
                    break;
                }
            }

            if (!stillPresent)
            {
                DiskRows.RemoveAt(i);
            }
        }

        for (int i = 0; i < devices.Count; i++)
        {
            DiskDeviceSnapshot device = devices[i];

            // StorageApi (管理者不要) が温度を取れなかったドライブだけ、LHM (管理者時) の値で補う。
            double? overrideTemperature = device.TemperatureC is null
                ? FindStorageTemperature(storageTemperatures, device.Model)
                : null;

            int existingIndex = -1;
            for (int j = 0; j < DiskRows.Count; j++)
            {
                if (DiskRows[j].PhysicalDriveNumber == device.PhysicalDriveNumber)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                var row = new DiskRowViewModel(device.PhysicalDriveNumber);
                row.Update(device, overrideTemperature);
                int insertIndex = Math.Min(i, DiskRows.Count);
                DiskRows.Insert(insertIndex, row);
            }
            else
            {
                DiskRows[existingIndex].Update(device, overrideTemperature);
                if (existingIndex != i)
                {
                    DiskRows.Move(existingIndex, i);
                }
            }
        }
    }

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
