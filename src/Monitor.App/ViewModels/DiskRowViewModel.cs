using System.ComponentModel;
using System.Runtime.CompilerServices;
using Monitor.Core.Formatting;
using Monitor.Core.Models;

namespace Monitor.App.ViewModels;

/// <summary>
/// ディスク一覧の1行分（物理ドライブ1台）。<see cref="SidebarViewModel"/> が
/// <see cref="PhysicalDriveNumber"/> で照合して既存インスタンスを差分更新するため、
/// それ以外のすべてのプロパティは変更通知付きで再代入可能にしてある。
/// </summary>
public sealed class DiskRowViewModel : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private string _modelText = string.Empty;
    private string _busTypeText = string.Empty;
    private string _temperatureText = "—";
    private string _readText = string.Empty;
    private string _writeText = string.Empty;
    private double _busyGaugeValue;
    private string _capacityText = string.Empty;
    private double _capacityGaugeValue;

    public DiskRowViewModel(int physicalDriveNumber)
    {
        PhysicalDriveNumber = physicalDriveNumber;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int PhysicalDriveNumber { get; }

    /// <summary>"C: D:" のようなドライブレター、無ければ "Disk 3"。</summary>
    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string ModelText
    {
        get => _modelText;
        private set => SetProperty(ref _modelText, value);
    }

    public string BusTypeText
    {
        get => _busTypeText;
        private set => SetProperty(ref _busTypeText, value);
    }

    /// <summary>取得できない場合は "—"。</summary>
    public string TemperatureText
    {
        get => _temperatureText;
        private set => SetProperty(ref _temperatureText, value);
    }

    public string ReadText
    {
        get => _readText;
        private set => SetProperty(ref _readText, value);
    }

    public string WriteText
    {
        get => _writeText;
        private set => SetProperty(ref _writeText, value);
    }

    public double BusyGaugeValue
    {
        get => _busyGaugeValue;
        private set => SetProperty(ref _busyGaugeValue, value);
    }

    /// <summary>"298 GB / 512 GB" のような使用量表示。ボリュームが無ければ物理容量のみ。</summary>
    public string CapacityText
    {
        get => _capacityText;
        private set => SetProperty(ref _capacityText, value);
    }

    public double CapacityGaugeValue
    {
        get => _capacityGaugeValue;
        private set => SetProperty(ref _capacityGaugeValue, value);
    }

    public void Update(DiskDeviceSnapshot d, double? overrideTemperatureC = null)
    {
        DisplayName = string.IsNullOrEmpty(d.DisplayName) ? $"Disk {d.PhysicalDriveNumber}" : d.DisplayName;
        ModelText = string.IsNullOrWhiteSpace(d.Model) ? "不明なドライブ" : d.Model;
        BusTypeText = string.IsNullOrEmpty(d.BusType) ? "" : d.BusType + (d.IsSsd ? " SSD" : " HDD");

        double? temperature = overrideTemperatureC ?? d.TemperatureC;
        TemperatureText = ByteFormatter.Temperature(temperature);

        ReadText = "R " + ByteFormatter.BytesPerSec(d.ReadBytesPerSec);
        WriteText = "W " + ByteFormatter.BytesPerSec(d.WriteBytesPerSec);
        BusyGaugeValue = d.BusyPercent;

        (string capacityText, double capacityPercent) = BuildCapacity(d);
        CapacityText = capacityText;
        CapacityGaugeValue = capacityPercent;
    }

    private static (string Text, double Percent) BuildCapacity(DiskDeviceSnapshot d)
    {
        if (d.Volumes.Count == 0)
        {
            return d.CapacityBytes > 0 ? (ByteFormatter.Bytes(d.CapacityBytes), 0.0) : ("—", 0.0);
        }

        ulong totalBytes = 0;
        ulong freeBytes = 0;
        foreach (LogicalVolumeSnapshot v in d.Volumes)
        {
            totalBytes += v.TotalBytes;
            freeBytes += v.FreeBytes;
        }

        if (totalBytes == 0)
        {
            return d.CapacityBytes > 0 ? (ByteFormatter.Bytes(d.CapacityBytes), 0.0) : ("—", 0.0);
        }

        ulong usedBytes = totalBytes >= freeBytes ? totalBytes - freeBytes : 0UL;
        double percent = Math.Clamp(100.0 * usedBytes / totalBytes, 0.0, 100.0);
        string text = $"{ByteFormatter.Bytes(usedBytes)} / {ByteFormatter.Bytes(totalBytes)}";
        return (text, percent);
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
