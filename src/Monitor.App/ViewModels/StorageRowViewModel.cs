using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Monitor.App.ViewModels;

/// <summary>
/// 「ストレージ」セクション一覧の1行分。論理ドライブ1個、または紐づく論理ドライブの無い
/// 物理ディスク1台のいずれかを表す（<see cref="DiskRowViewModel"/> と <see cref="VolumeRowViewModel"/>
/// を統合したもの。旧2クラスは削除済み）。
/// <see cref="SidebarViewModel"/> が <see cref="Key"/>（ドライブレターなら "C:"、ボリューム無し物理ディスクなら
/// "#3" のような合成キー）で照合しながら差分更新する。
/// </summary>
public sealed class StorageRowViewModel : INotifyPropertyChanged
{
    private const double WarningThresholdPercent = 90.0;

    private string _driveLetterText = string.Empty;
    private string _labelText = string.Empty;
    private bool _isNetwork;
    private bool _isReady = true;
    private bool _hasCapacity;
    private string _usagePercentText = "—";
    private double _gaugeValue;
    private bool _isWarning;
    private string _freeText = "";
    private string _readText = "";
    private string _writeText = "";
    private string _temperatureText = "";
    private string _tooltipText = "";

    public StorageRowViewModel(string key)
    {
        Key = key;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>差分更新のキー。ボリュームなら "C:"、ボリューム無し物理ディスクなら "#3"。</summary>
    public string Key { get; }

    /// <summary>ドライブレター（"C:"）、またはボリューム無し物理ディスクの表示名（"Disk 3"）。</summary>
    public string DriveLetterText { get => _driveLetterText; private set => SetProperty(ref _driveLetterText, value); }

    /// <summary>ボリュームラベル / UNC ホスト名 / 物理ディスクのモデル名。</summary>
    public string LabelText { get => _labelText; private set => SetProperty(ref _labelText, value); }

    public bool IsNetwork { get => _isNetwork; private set => SetProperty(ref _isNetwork, value); }

    public bool IsReady { get => _isReady; private set => SetProperty(ref _isReady, value); }

    /// <summary>使用率バー・使用率・空き容量を表示するか（ボリューム無し物理ディスク行では false）。</summary>
    public bool HasCapacity { get => _hasCapacity; private set => SetProperty(ref _hasCapacity, value); }

    public string UsagePercentText { get => _usagePercentText; private set => SetProperty(ref _usagePercentText, value); }

    public double GaugeValue { get => _gaugeValue; private set => SetProperty(ref _gaugeValue, value); }

    /// <summary>使用率が90%以上。XAML 側でバーの警告色切り替えに使う。</summary>
    public bool IsWarning { get => _isWarning; private set => SetProperty(ref _isWarning, value); }

    public string FreeText { get => _freeText; private set => SetProperty(ref _freeText, value); }

    /// <summary>読み込み速度の短縮表示（"12M" 等）。同じ物理ディスクの2行目以降・非対応行では空文字。
    /// ネットワークドライブでは "—"。</summary>
    public string ReadText { get => _readText; private set => SetProperty(ref _readText, value); }

    public string WriteText { get => _writeText; private set => SetProperty(ref _writeText, value); }

    public string TemperatureText { get => _temperatureText; private set => SetProperty(ref _temperatureText, value); }

    /// <summary>型番・バス種別・総容量・物理ディスク番号・Busy% などの詳細（ツールチップ用）。</summary>
    public string TooltipText { get => _tooltipText; private set => SetProperty(ref _tooltipText, value); }

    public void Update(
        string driveLetterText,
        string labelText,
        bool isNetwork,
        bool isReady,
        bool hasCapacity,
        double usedPercent,
        string usagePercentText,
        string freeText,
        string readText,
        string writeText,
        string temperatureText,
        string tooltipText)
    {
        DriveLetterText = driveLetterText;
        LabelText = labelText;
        IsNetwork = isNetwork;
        IsReady = isReady;
        HasCapacity = hasCapacity;
        UsagePercentText = usagePercentText;
        GaugeValue = hasCapacity ? Math.Clamp(usedPercent, 0.0, 100.0) : 0.0;
        IsWarning = hasCapacity && usedPercent >= WarningThresholdPercent;
        FreeText = freeText;
        ReadText = readText;
        WriteText = writeText;
        TemperatureText = temperatureText;
        TooltipText = tooltipText;
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
