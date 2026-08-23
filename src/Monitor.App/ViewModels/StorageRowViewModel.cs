using System.ComponentModel;
using System.Runtime.CompilerServices;
using Monitor.Core;
using Monitor.Core.Collections;

namespace Monitor.App.ViewModels;

/// <summary>「ストレージ」セクション一覧の行種別。<see cref="SidebarViewModel"/> が
/// <see cref="StorageRowViewModel.Kind"/> を見て XAML 側のテンプレートを切り替える
/// （ContentPresenter + DataTrigger。階層コレクションにはせず StorageRows はフラットなまま）。</summary>
public enum StorageRowKind
{
    /// <summary>物理ディスクの見出し行。モデル名・バス種別・R/W・Busy%・温度を持つ。</summary>
    Disk,

    /// <summary>ローカルの論理ボリューム行。</summary>
    Volume,

    /// <summary>ネットワークドライブ行。列構成は Volume と共通。</summary>
    Network,
}

/// <summary>
/// 「ストレージ」セクション一覧の1行分。物理ディスクの見出し行（<see cref="Kind"/> = <see cref="StorageRowKind.Disk"/>）、
/// ローカルボリューム行（<see cref="StorageRowKind.Volume"/>）、ネットワークドライブ行
/// （<see cref="StorageRowKind.Network"/>）のいずれかを表す。
/// <see cref="SidebarViewModel"/> が <see cref="Key"/>（ドライブレターなら "C:"、物理ディスク見出しなら
/// "#3" のような合成キー）で照合しながら差分更新する。行種別ごとに使うプロパティが異なるため、
/// 更新メソッドも <see cref="UpdateAsDisk"/> / <see cref="UpdateAsVolume"/> に分けている。
/// </summary>
public sealed class StorageRowViewModel : INotifyPropertyChanged
{
    private const double WarningThresholdPercent = 90.0;

    private StorageRowKind _kind;

    // ----- 共通 -----
    private bool _isReady = true;
    private string _tooltipText = "";

    // ----- ディスク見出し行 (Kind = Disk) -----
    private string _modelText = string.Empty;
    private string _busTypeText = string.Empty;
    private string _readText = "";
    private string _writeText = "";
    private string _busyPercentText = "";
    private string _temperatureText = "";
    private float[] _writeHistory = Array.Empty<float>();
    private RingBuffer<float>? _writeHistoryBuffer;

    // ----- ボリューム/ネットワーク行 (Kind = Volume / Network) -----
    private string _driveLetterText = string.Empty;
    private string _labelText = string.Empty;
    private bool _isNetwork;
    private bool _hasCapacity;
    private string _usagePercentText = "—";
    private double _gaugeValue;
    private bool _isWarning;
    private string _capacityText = "";

    public StorageRowViewModel(string key)
    {
        Key = key;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>差分更新のキー。ボリューム/ネットワーク行なら "C:"、物理ディスク見出し行なら "#3"。
    /// 見出し行は "#" 始まり、ボリューム行はドライブレター（英字+":"）始まりなので衝突しない。</summary>
    public string Key { get; }

    /// <summary>行種別。XAML 側のテンプレート切り替えに使う。</summary>
    public StorageRowKind Kind { get => _kind; private set => SetProperty(ref _kind, value); }

    public bool IsReady { get => _isReady; private set => SetProperty(ref _isReady, value); }

    /// <summary>見出し行: 物理ディスク番号・総容量など行に出ていない情報。
    /// ボリューム/ネットワーク行: ファイルシステム・ボリューム総容量。</summary>
    public string TooltipText { get => _tooltipText; private set => SetProperty(ref _tooltipText, value); }

    // ----- ディスク見出し行用プロパティ -----

    /// <summary>モデル名。空なら "Disk {番号}"。</summary>
    public string ModelText { get => _modelText; private set => SetProperty(ref _modelText, value); }

    /// <summary>"NVMe SSD" / "SATA HDD" 形式。</summary>
    public string BusTypeText { get => _busTypeText; private set => SetProperty(ref _busTypeText, value); }

    /// <summary>読み込み速度の短縮表示（"12M" 等）。ディスク見出し行にのみ表示する
    /// （旧: 配下ボリュームの先頭行にのみ表示していたが、見出し行に集約したため重複回避ロジックは不要になった）。</summary>
    public string ReadText { get => _readText; private set => SetProperty(ref _readText, value); }

    public string WriteText { get => _writeText; private set => SetProperty(ref _writeText, value); }

    public string BusyPercentText { get => _busyPercentText; private set => SetProperty(ref _busyPercentText, value); }

    public string TemperatureText { get => _temperatureText; private set => SetProperty(ref _temperatureText, value); }

    /// <summary>この物理ディスクの書き込みレート履歴（サンプル数は <see cref="MetricsHistory.DefaultCapacity"/> に揃える）。
    /// <see cref="Controls.Sparkline.Values"/> にそのままバインドする。行が使い回される限りリングバッファは
    /// 保持し続け、<see cref="UpdateAsDisk"/> のたびに1点だけ push する（作り直さない）。</summary>
    public float[] WriteHistory { get => _writeHistory; private set => SetProperty(ref _writeHistory, value); }

    // ----- ボリューム/ネットワーク行用プロパティ -----

    /// <summary>ドライブレター（"C:"）。</summary>
    public string DriveLetterText { get => _driveLetterText; private set => SetProperty(ref _driveLetterText, value); }

    /// <summary>ボリュームラベル、またはネットワークドライブの UNC パス。</summary>
    public string LabelText { get => _labelText; private set => SetProperty(ref _labelText, value); }

    public bool IsNetwork { get => _isNetwork; private set => SetProperty(ref _isNetwork, value); }

    /// <summary>使用率バー・使用率・容量を表示するか（未準備ドライブ等では false）。</summary>
    public bool HasCapacity { get => _hasCapacity; private set => SetProperty(ref _hasCapacity, value); }

    public string UsagePercentText { get => _usagePercentText; private set => SetProperty(ref _usagePercentText, value); }

    public double GaugeValue { get => _gaugeValue; private set => SetProperty(ref _gaugeValue, value); }

    /// <summary>使用率が90%以上。XAML 側でバーの警告色切り替えに使う。</summary>
    public bool IsWarning { get => _isWarning; private set => SetProperty(ref _isWarning, value); }

    /// <summary>"使用 / 合計" 容量（例: "1.4 / 2.0 TB"）。未準備・容量不明なら "—"。</summary>
    public string CapacityText { get => _capacityText; private set => SetProperty(ref _capacityText, value); }

    /// <summary>物理ディスク見出し行として更新する。</summary>
    public void UpdateAsDisk(
        string modelText,
        string busTypeText,
        string readText,
        string writeText,
        string busyPercentText,
        string temperatureText,
        double writeBytesPerSec,
        string tooltipText)
    {
        Kind = StorageRowKind.Disk;
        ModelText = modelText;
        BusTypeText = busTypeText;
        ReadText = readText;
        WriteText = writeText;
        BusyPercentText = busyPercentText;
        TemperatureText = temperatureText;
        TooltipText = tooltipText;

        PushWriteHistory(writeBytesPerSec);
    }

    /// <summary>ボリューム行、またはネットワークドライブ行として更新する。</summary>
    public void UpdateAsVolume(
        StorageRowKind kind,
        string driveLetterText,
        string labelText,
        bool isReady,
        bool hasCapacity,
        double usedPercent,
        string usagePercentText,
        string capacityText,
        string tooltipText)
    {
        Kind = kind;
        DriveLetterText = driveLetterText;
        LabelText = labelText;
        IsNetwork = kind == StorageRowKind.Network;
        IsReady = isReady;
        HasCapacity = hasCapacity;
        UsagePercentText = usagePercentText;
        GaugeValue = hasCapacity ? Math.Clamp(usedPercent, 0.0, 100.0) : 0.0;
        IsWarning = hasCapacity && usedPercent >= WarningThresholdPercent;
        CapacityText = capacityText;
        TooltipText = tooltipText;
    }

    /// <summary>書き込みレートを1点 push し、Sparkline 用のスナップショット配列を再生成する。
    /// リングバッファ自体は行が使い回される限り保持し続ける（毎回作り直すと履歴が途切れる）。</summary>
    private void PushWriteHistory(double writeBytesPerSec)
    {
        _writeHistoryBuffer ??= new RingBuffer<float>(MetricsHistory.DefaultCapacity);
        _writeHistoryBuffer.Add((float)writeBytesPerSec);

        // MetricsHistory.Snapshot と同じ流儀: ロック不要（UI スレッド専属）だが、
        // Sparkline は AffectsRender の DependencyProperty なので新しい配列を渡して再描画を誘発する。
        var snapshot = new float[_writeHistoryBuffer.Count];
        _writeHistoryBuffer.CopyTo(snapshot);
        WriteHistory = snapshot;
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
