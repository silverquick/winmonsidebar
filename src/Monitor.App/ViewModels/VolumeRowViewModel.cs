using System.ComponentModel;
using System.Runtime.CompilerServices;
using Monitor.Core.Formatting;
using Monitor.Core.Models;

namespace Monitor.App.ViewModels;

/// <summary>
/// ドライブ一覧の1行分（論理ドライブ1個）。<see cref="SidebarViewModel"/> が
/// <see cref="DriveLetter"/> をキーに既存インスタンスを差分更新する。
/// </summary>
public sealed class VolumeRowViewModel : INotifyPropertyChanged
{
    private const double WarningThresholdPercent = 90.0;

    private string _labelText = string.Empty;
    private string _usagePercentText = "—";
    private double _gaugeValue;
    private string _capacityText = string.Empty;
    private bool _isNetwork;
    private bool _isReady = true;
    private bool _isWarning;

    public VolumeRowViewModel(string driveLetter)
    {
        DriveLetter = driveLetter;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>差分更新のキー。例 "C:"。</summary>
    public string DriveLetter { get; }

    /// <summary>ラベル、または（ネットワークドライブなら）UNC パス。</summary>
    public string LabelText
    {
        get => _labelText;
        private set => SetProperty(ref _labelText, value);
    }

    public string UsagePercentText
    {
        get => _usagePercentText;
        private set => SetProperty(ref _usagePercentText, value);
    }

    public double GaugeValue
    {
        get => _gaugeValue;
        private set => SetProperty(ref _gaugeValue, value);
    }

    /// <summary>"431 GB 空き / 931 GB" のような使用量表示。<see cref="IsReady"/> が false なら "利用不可"。</summary>
    public string CapacityText
    {
        get => _capacityText;
        private set => SetProperty(ref _capacityText, value);
    }

    public bool IsNetwork
    {
        get => _isNetwork;
        private set => SetProperty(ref _isNetwork, value);
    }

    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    /// <summary>使用率が 90% 以上。XAML 側でバーの警告色切り替えに使う。</summary>
    public bool IsWarning
    {
        get => _isWarning;
        private set => SetProperty(ref _isWarning, value);
    }

    public void Update(VolumeSnapshot v)
    {
        IsNetwork = v.Kind == VolumeKind.Network;
        IsReady = v.IsReady;

        LabelText = IsNetwork
            ? (v.NetworkPath ?? "")
            : (string.IsNullOrWhiteSpace(v.Label) ? "" : v.Label!);

        if (!v.IsReady)
        {
            UsagePercentText = "—";
            CapacityText = "利用不可";
            GaugeValue = 0;
            IsWarning = false;
            return;
        }

        UsagePercentText = ByteFormatter.Percent(v.UsedPercent);
        GaugeValue = Math.Clamp(v.UsedPercent, 0.0, 100.0);
        IsWarning = v.UsedPercent >= WarningThresholdPercent;
        CapacityText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{ByteFormatter.Bytes(v.FreeBytes)} 空き / {ByteFormatter.Bytes(v.TotalBytes)}");
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
