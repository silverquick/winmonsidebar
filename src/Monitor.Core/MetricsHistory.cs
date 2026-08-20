using Monitor.Core.Collections;
using Monitor.Core.Models;

namespace Monitor.Core;

/// <summary>
/// スパークライン描画用の時系列履歴。
/// </summary>
public enum MetricSeries
{
    CpuTotal,
    GpuTotal,
    MemoryUsedPercent,
    NetReceiveBytesPerSec,
    NetSendBytesPerSec,
    DiskReadBytesPerSec,
    DiskWriteBytesPerSec,
    GpuTemperature,
    CpuTemperature,
}

/// <summary>
/// メトリクスの時系列履歴を保持する。UI スレッドから読み、バックグラウンドスレッドから書き込まれるため、
/// すべての読み書きは内部ロックで保護される。
/// </summary>
public sealed class MetricsHistory
{
    public const int DefaultCapacity = 180;

    private readonly object _lock = new object();

    private readonly RingBuffer<float> _cpuTotal;
    private readonly RingBuffer<float> _gpuTotal;
    private readonly RingBuffer<float> _memoryUsedPercent;
    private readonly RingBuffer<float> _netReceiveBytesPerSec;
    private readonly RingBuffer<float> _netSendBytesPerSec;
    private readonly RingBuffer<float> _diskReadBytesPerSec;
    private readonly RingBuffer<float> _diskWriteBytesPerSec;
    private readonly RingBuffer<float> _gpuTemperature;
    private readonly RingBuffer<float> _cpuTemperature;

    private float _lastGpuTemperature;
    private float _lastCpuTemperature;

    public MetricsHistory(int capacity = DefaultCapacity)
    {
        Capacity = capacity;
        _cpuTotal = new RingBuffer<float>(capacity);
        _gpuTotal = new RingBuffer<float>(capacity);
        _memoryUsedPercent = new RingBuffer<float>(capacity);
        _netReceiveBytesPerSec = new RingBuffer<float>(capacity);
        _netSendBytesPerSec = new RingBuffer<float>(capacity);
        _diskReadBytesPerSec = new RingBuffer<float>(capacity);
        _diskWriteBytesPerSec = new RingBuffer<float>(capacity);
        _gpuTemperature = new RingBuffer<float>(capacity);
        _cpuTemperature = new RingBuffer<float>(capacity);
    }

    public int Capacity { get; }

    /// <summary>
    /// 与えられたスナップショットの値で全系列を1サンプル分進める。
    /// </summary>
    public void Append(MetricsSnapshot s)
    {
        lock (_lock)
        {
            _cpuTotal.Add((float)s.Cpu.TotalUsagePercent);
            _gpuTotal.Add((float)s.Gpu.TotalUsagePercent);
            _memoryUsedPercent.Add((float)s.Memory.UsedPercent);
            _netReceiveBytesPerSec.Add((float)s.Network.TotalReceiveBytesPerSec);
            _netSendBytesPerSec.Add((float)s.Network.TotalSendBytesPerSec);
            _diskReadBytesPerSec.Add((float)s.Disk.TotalReadBytesPerSec);
            _diskWriteBytesPerSec.Add((float)s.Disk.TotalWriteBytesPerSec);

            double? gpuTemperature = s.Gpu.Adapters.Count > 0 ? s.Gpu.Adapters[0].TemperatureC : null;
            float gpuTemperatureValue = gpuTemperature.HasValue ? (float)gpuTemperature.Value : _lastGpuTemperature;
            _gpuTemperature.Add(gpuTemperatureValue);
            _lastGpuTemperature = gpuTemperatureValue;

            double? cpuTemperature = s.Cpu.PackageTemperatureC;
            float cpuTemperatureValue = cpuTemperature.HasValue ? (float)cpuTemperature.Value : _lastCpuTemperature;
            _cpuTemperature.Add(cpuTemperatureValue);
            _lastCpuTemperature = cpuTemperatureValue;
        }
    }

    /// <summary>
    /// 指定系列の現在の履歴をロック内でコピーして返す（最古が先頭）。
    /// </summary>
    public float[] Snapshot(MetricSeries series)
    {
        lock (_lock)
        {
            RingBuffer<float> buffer = GetBuffer(series);
            var result = new float[buffer.Count];
            buffer.CopyTo(result);
            return result;
        }
    }

    /// <summary>
    /// ロックで保護しつつ、指定系列の現在の履歴をコールバックへ渡す。
    /// コールバックの引数は呼び出し中のみ有効な読み取り専用ビュー。
    /// </summary>
    public void Read(MetricSeries series, Action<IReadOnlyList<float>> reader)
    {
        lock (_lock)
        {
            RingBuffer<float> buffer = GetBuffer(series);
            var snapshot = new float[buffer.Count];
            buffer.CopyTo(snapshot);
            reader(snapshot);
        }
    }

    private RingBuffer<float> GetBuffer(MetricSeries series) => series switch
    {
        MetricSeries.CpuTotal => _cpuTotal,
        MetricSeries.GpuTotal => _gpuTotal,
        MetricSeries.MemoryUsedPercent => _memoryUsedPercent,
        MetricSeries.NetReceiveBytesPerSec => _netReceiveBytesPerSec,
        MetricSeries.NetSendBytesPerSec => _netSendBytesPerSec,
        MetricSeries.DiskReadBytesPerSec => _diskReadBytesPerSec,
        MetricSeries.DiskWriteBytesPerSec => _diskWriteBytesPerSec,
        MetricSeries.GpuTemperature => _gpuTemperature,
        MetricSeries.CpuTemperature => _cpuTemperature,
        _ => throw new ArgumentOutOfRangeException(nameof(series), series, null),
    };
}
