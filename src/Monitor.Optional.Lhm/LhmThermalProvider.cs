using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Monitor.Core.Abstractions;
using Monitor.Core.Models;

namespace Monitor.Optional.Lhm;

/// <summary>
/// LibreHardwareMonitor を使って CPU/マザーボード/ディスクの温度・ファン・電力を取得する。
/// 管理者権限で起動されたときだけ有効化される。管理者でなければ何もせず、
/// 常に空の <see cref="ThermalSnapshot"/> を返す（アプリ本体は通常どおり動き続ける）。
/// </summary>
public sealed class LhmThermalProvider : IThermalProvider
{
    private readonly IReadOnlyDictionary<string, string> _sensorAliases;
    private readonly HashSet<string> _hiddenSensors;

    private readonly HashSet<IHardware> _representativeHardware = new();
    private readonly HashSet<IHardware> _knownHardware = new();
    private readonly HashSet<IHardware> _representativeBranches = new();
    private readonly HashSet<IHardware> _knownBranches = new();
    private readonly HashSet<ISensor> _knownSensors = new();
    private readonly HashSet<ISensor> _representativeSensors = new();
    private IHardware[]? _hardwareForTest;
    internal void SetHardwareForTest(IHardware[] hardware)
    {
        _hardwareForTest = hardware;
        IsAvailable = _isElevated = true;
    }

    private Computer? _computer;
    private bool _isElevated;

    /// <param name="sensorAliases">
    /// センサーの生の名前 → 表示したい名前の対応。Super I/O が返す "Temperature #3" のような
    /// 総称名はボードごとに意味が違い、ソフトウェアからは部位を判別できないため、
    /// 利用者が特定した名前を外から与えられるようにする。別名は分類にも使われるので、
    /// "VRM" を含む名前を与えればその値は VRM として扱われる。
    /// </param>
    public LhmThermalProvider(IReadOnlyDictionary<string, string>? sensorAliases = null, IEnumerable<string>? hiddenSensors = null)
    {
        _sensorAliases = sensorAliases ?? new Dictionary<string, string>();
        _hiddenSensors = NormalizeHiddenSensors(hiddenSensors);
    }

    internal static HashSet<string> NormalizeHiddenSensors(IEnumerable<string>? hiddenSensors) =>
        (hiddenSensors ?? Array.Empty<string>())
            .Where(static entry => !string.IsNullOrWhiteSpace(entry))
            .Select(static entry => entry.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string Name => "LibreHardwareMonitor";

    public bool RequiresElevation => true;

    public bool IsAvailable { get; private set; }

    public bool LastSampleFailed { get; private set; }

    public void Initialize()
    {
        try
        {
            _isElevated = IsRunningElevated();
            if (!_isElevated)
            {
                // 管理者でなければ Computer.Open() を呼ばない（ドライバロードを試みて無駄に失敗するため）。
                IsAvailable = false;
                return;
            }

            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsStorageEnabled = true,
                IsGpuEnabled = false,
                IsMemoryEnabled = false,
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
            };

            computer.Open();
            _computer = computer;

            bool hasAnySensor = false;
            foreach (IHardware hw in computer.Hardware)
            {
                if (HardwareHasAnySensor(hw))
                {
                    hasAnySensor = true;
                    break;
                }
            }

            IsAvailable = hasAnySensor;

            if (!hasAnySensor)
            {
                CloseComputerSafe();
                _computer = null;
            }
        }
        catch
        {
            IsAvailable = false;
            CloseComputerSafe();
            _computer = null;
        }
    }

    public ThermalSnapshot Sample(TimeSpan elapsed)
    {
        LastSampleFailed = false;
        if (!IsAvailable || (_computer is null && _hardwareForTest is null))
        {
            return ThermalSnapshot.Empty with { IsElevated = _isElevated, Source = "none" };
        }

        try
        {
            IList<IHardware> hardware = _hardwareForTest ?? _computer!.Hardware;

            double? cpuPackageTemp = null;
            double? cpuPackagePower = null;
            double? vrmTemp = null;
            double? mbTemp = null;
            var cpuCoreTemps = new List<SensorReading>();
            var fans = new List<SensorReading>();
            var otherTemps = new List<SensorReading>();
            var storageTemps = new List<SensorReading>();

            for (int hardwareIndex = 0; hardwareIndex < hardware.Count; hardwareIndex++)
            {
                IHardware hw = hardware[hardwareIndex];
                CollectFromHardware(
                    hw,
                    ref cpuPackageTemp,
                    ref cpuPackagePower,
                    ref vrmTemp,
                    ref mbTemp,
                    cpuCoreTemps,
                    fans,
                    otherTemps,
                    storageTemps);
            }

            return new ThermalSnapshot
            {
                IsElevated = _isElevated,
                IsAvailable = true,
                Source = "LibreHardwareMonitor",
                CpuPackageTemperatureC = cpuPackageTemp,
                CpuCoreTemperatures = cpuCoreTemps,
                CpuPackagePowerWatts = cpuPackagePower,
                MotherboardTemperatureC = mbTemp,
                VrmTemperatureC = vrmTemp,
                Fans = fans,
                OtherTemperatures = otherTemps,
                StorageTemperatures = storageTemps,
            };
        }
        catch
        {
            LastSampleFailed = true;
            return ThermalSnapshot.Empty with { IsElevated = _isElevated, Source = "none" };
        }
    }

    public void Dispose()
    {
        CloseComputerSafe();
    }

    private void CloseComputerSafe()
    {
        try
        {
            _computer?.Close();
        }
        catch
        {
            // Dispose/Close の失敗は握りつぶす。
        }
    }

    private static bool IsRunningElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool HardwareHasAnySensor(IHardware hw)
    {
        try
        {
            if (hw.Sensors.Length > 0)
            {
                return true;
            }

            foreach (IHardware sub in hw.SubHardware)
            {
                if (HardwareHasAnySensor(sub))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 無視して false 扱い。
        }

        return false;
    }

    private void CollectFromHardware(
        IHardware hw,
        ref double? cpuPackageTemp,
        ref double? cpuPackagePower,
        ref double? vrmTemp,
        ref double? mbTemp,
        List<SensorReading> cpuCoreTemps,
        List<SensorReading> fans,
        List<SensorReading> otherTemps,
        List<SensorReading> storageTemps)
    {
        bool updated = true;
        try { hw.Update(); }
        catch { updated = false; RecordHardwareFailure(hw); }

        if (updated)
        {
            try
            {
                foreach (ISensor sensor in hw.Sensors)
                {
                    try
                    {
                        if (!_knownSensors.Contains(sensor))
                        {
                            if (IsRepresentativeSensor(hw, sensor))
                            {
                                _representativeHardware.Add(hw);
                                _representativeSensors.Add(sensor);
                            }
                            _knownSensors.Add(sensor);
                        }
                        ClassifySensor(hw, sensor, ref cpuPackageTemp, ref cpuPackagePower, ref vrmTemp, ref mbTemp,
                            cpuCoreTemps, fans, otherTemps, storageTemps);
                    }
                    catch
                    {
                        // 読めない補助センサーは捨てる。代表温度の失敗だけをソースの失敗にする。
                        if (_knownSensors.Contains(sensor)) LastSampleFailed |= _representativeSensors.Contains(sensor);
                        else
                        {
                            try { if (IsRepresentativeSensor(hw, sensor)) LastSampleFailed = true; }
                            catch { RecordHardwareFailure(hw); }
                        }
                    }
                }
                _knownHardware.Add(hw);
            }
            catch { RecordHardwareFailure(hw); }
        }

        // 親の更新が失敗しても、独立して更新できる子のデータは読む。
        try
        {
            foreach (IHardware sub in hw.SubHardware)
            {
                CollectFromHardware(sub, ref cpuPackageTemp, ref cpuPackagePower, ref vrmTemp, ref mbTemp,
                    cpuCoreTemps, fans, otherTemps, storageTemps);
                if (_representativeHardware.Contains(sub) || _representativeBranches.Contains(sub)) _representativeBranches.Add(hw);
            }
            _knownBranches.Add(hw);
        }
        catch
        {
            if (_representativeBranches.Contains(hw) || (!_knownBranches.Contains(hw) && CouldSupplyRepresentative(hw))) LastSampleFailed = true;
            else RecordHardwareFailure(hw);
        }
    }

    private void RecordHardwareFailure(IHardware hw)
    {
        if (_representativeHardware.Contains(hw)) { LastSampleFailed = true; return; }
        try
        {
            foreach (ISensor sensor in hw.Sensors)
                if (IsRepresentativeSensor(hw, sensor)) { LastSampleFailed = true; return; }
        }
        catch
        {
            // 既知の補助ハードウェアは全体を落とさない。初回から走査不能なら保守的に判定する。
            if (!_knownHardware.Contains(hw))
            {
                LastSampleFailed |= CouldSupplyRepresentative(hw);
            }
        }
    }

    private static bool CouldSupplyRepresentative(IHardware hw)
    {
        try { return hw.HardwareType is HardwareType.Cpu or HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.Storage; }
        catch { return true; }
    }

    private bool IsRepresentativeSensor(IHardware hw, ISensor sensor)
    {
        if (sensor.SensorType != SensorType.Temperature) return false;
        string rawName = sensor.Name ?? "";
        if (_hiddenSensors.Count > 0 && IsHiddenSensor(_hiddenSensors, rawName, sensor.Identifier.ToString())) return false;
        string name = _sensorAliases.TryGetValue(rawName, out string? alias) && !string.IsNullOrWhiteSpace(alias) ? alias : rawName;
        return hw.HardwareType switch
        {
            HardwareType.Cpu => name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || name.Contains("Package", StringComparison.OrdinalIgnoreCase),
            HardwareType.Motherboard or HardwareType.SuperIO => name.Contains("VRM", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("System", StringComparison.OrdinalIgnoreCase) || name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Temperature #1", StringComparison.OrdinalIgnoreCase),
            HardwareType.Storage => !IsStorageThresholdSensor(rawName),
            _ => false,
        };
    }

    private void ClassifySensor(
        IHardware hw,
        ISensor sensor,
        ref double? cpuPackageTemp,
        ref double? cpuPackagePower,
        ref double? vrmTemp,
        ref double? mbTemp,
        List<SensorReading> cpuCoreTemps,
        List<SensorReading> fans,
        List<SensorReading> otherTemps,
        List<SensorReading> storageTemps)
    {
        // 別名は分類より前に適用する。こうすると利用者が "Temperature #3" に "VRM" を割り当てた
        // だけで、以降の名前判定がそれを VRM として拾い、表示名も置き換わる。
        string rawName = sensor.Name ?? "";
        if (sensor.SensorType == SensorType.Temperature && _hiddenSensors.Count > 0 &&
            IsHiddenSensor(_hiddenSensors, rawName, sensor.Identifier.ToString()))
        {
            return;
        }
        string name = _sensorAliases.TryGetValue(rawName, out string? alias) && !string.IsNullOrWhiteSpace(alias)
            ? alias
            : rawName;
        float? reading = sensor.Value;
        if (!reading.HasValue) return;
        double value = reading.Value;
        HardwareType hwType = hw.HardwareType;

        // ---- Fan（RPM）。ハードウェア種別を問わず拾う。未接続（0 RPM）は除外。 ----
        if (sensor.SensorType == SensorType.Fan)
        {
            if (value > 0)
            {
                fans.Add(new SensorReading(name, value));
            }

            return;
        }

        // 温度センサーはここで妥当性を検査し、あり得ない値は「取得不能」として捨てる。
        //
        // これが要るのは、必要なカーネルモジュールが使えないときに LHM が例外を出さず
        // 0.00 を返すため。実機でも PawnIO を入れる前は CPU パッケージ温度が 0.0 で返り、
        // 未接続の Super I/O 入力や一部の古い SATA SSD（INTEL SSDSC2CT120A3）も 0.0 を返す。
        // 素通しすると画面に「0.0°C」と出てしまい、本プロジェクトの
        // 「0°C と『取れない』を混同させない」という原則が崩れる。
        if (sensor.SensorType == SensorType.Temperature && !IsPlausibleTemperature(value))
        {
            return;
        }

        // ---- CPU 温度 ----
        if (hwType == HardwareType.Cpu && sensor.SensorType == SensorType.Temperature)
        {
            if (name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            {
                // "Core (Tctl/Tdie)" または "CPU Package"
                cpuPackageTemp = value;
            }
            else
            {
                // "Core #N" / "CCD1 (Tdie)" 等
                cpuCoreTemps.Add(new SensorReading(name, value));
            }

            return;
        }

        // ---- CPU 電力（パッケージ） ----
        if (hwType == HardwareType.Cpu && sensor.SensorType == SensorType.Power &&
            name.Contains("Package", StringComparison.OrdinalIgnoreCase))
        {
            cpuPackagePower = value;
            return;
        }

        // ---- マザーボード / Super I/O 温度 ----
        if ((hwType == HardwareType.Motherboard || hwType == HardwareType.SuperIO) &&
            sensor.SensorType == SensorType.Temperature)
        {
            if (name.Contains("VRM", StringComparison.OrdinalIgnoreCase))
            {
                vrmTemp = value;
            }
            else if (name.Contains("System", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Temperature #1", StringComparison.OrdinalIgnoreCase))
            {
                mbTemp = value;
            }
            else
            {
                otherTemps.Add(new SensorReading(name, value));
            }

            return;
        }

        // ---- ディスク温度（モデル名で照合できるよう hw.Name を使う） ----
        if (hwType == HardwareType.Storage && sensor.SensorType == SensorType.Temperature)
        {
            // NVMe では "Warning Temperature" / "Critical Temperature" も温度センサーとして並ぶが、
            // これはドライブが申告する閾値（例: WD Blue SN580 で 83/87°C）であって測定値ではない。
            // 取り込むと過熱しているように見え、モデル名照合の順序次第では表示にも出てしまうため除外する。
            // 閾値は Monitor.Windows の DiskProvider が別途 WarningTemperatureC / CriticalTemperatureC として扱う。
            if (IsStorageThresholdSensor(rawName))
            {
                return;
            }

            // hw.Name は末尾に空白が入ることがある（例: "INTEL SSDSC2CT120A3      "）。
            // 呼び出し側はモデル名で照合するので、ここで詰めておく。
            storageTemps.Add(new SensorReading((hw.Name ?? name).Trim(), value));
        }
    }

    /// <summary>
    /// 温度として現実的な範囲か。0 以下と 150°C 超はセンサーが読めていないときの値とみなす。
    /// </summary>
    private static bool IsPlausibleTemperature(double celsius) => celsius > 0.0 && celsius < 150.0;

    /// <summary>
    /// LHM のストレージ温度センサーのうち、測定値ではなく閾値を表すものか（センサー名の別名適用前の名前で判定する）。
    /// </summary>
    internal static bool IsStorageThresholdSensor(string rawSensorName) =>
        rawSensorName.Trim() is var n &&
        (n.Equals("Warning Temperature", StringComparison.OrdinalIgnoreCase) ||
         n.Equals("Critical Temperature", StringComparison.OrdinalIgnoreCase));

    internal static bool IsHiddenSensor(IReadOnlySet<string> hiddenSensors, string rawName, string identifier) =>
        hiddenSensors.Contains(rawName) || (!string.IsNullOrEmpty(identifier) && hiddenSensors.Contains(identifier));

}
