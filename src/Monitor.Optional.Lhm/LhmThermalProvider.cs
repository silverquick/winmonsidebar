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

    private Computer? _computer;
    private bool _isElevated;

    /// <param name="sensorAliases">
    /// センサーの生の名前 → 表示したい名前の対応。Super I/O が返す "Temperature #3" のような
    /// 総称名はボードごとに意味が違い、ソフトウェアからは部位を判別できないため、
    /// 利用者が特定した名前を外から与えられるようにする。別名は分類にも使われるので、
    /// "VRM" を含む名前を与えればその値は VRM として扱われる。
    /// </param>
    public LhmThermalProvider(IReadOnlyDictionary<string, string>? sensorAliases = null)
    {
        _sensorAliases = sensorAliases ?? new Dictionary<string, string>();
    }

    public string Name => "LibreHardwareMonitor";

    public bool RequiresElevation => true;

    public bool IsAvailable { get; private set; }

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
        if (!IsAvailable || _computer is null)
        {
            return ThermalSnapshot.Empty with { IsElevated = _isElevated, Source = "none" };
        }

        try
        {
            var visitor = new UpdateVisitor();
            foreach (IHardware hw in _computer.Hardware)
            {
                hw.Accept(visitor);
            }

            double? cpuPackageTemp = null;
            double? cpuPackagePower = null;
            double? vrmTemp = null;
            double? mbTemp = null;
            var cpuCoreTemps = new List<SensorReading>();
            var fans = new List<SensorReading>();
            var otherTemps = new List<SensorReading>();
            var storageTemps = new List<SensorReading>();

            foreach (IHardware hw in _computer.Hardware)
            {
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
        try
        {
            foreach (ISensor sensor in hw.Sensors)
            {
                try
                {
                    ClassifySensor(
                        hw,
                        sensor,
                        ref cpuPackageTemp,
                        ref cpuPackagePower,
                        ref vrmTemp,
                        ref mbTemp,
                        cpuCoreTemps,
                        fans,
                        otherTemps,
                        storageTemps);
                }
                catch
                {
                    // 1センサーの読み取り失敗は無視して続行する。
                }
            }

            foreach (IHardware sub in hw.SubHardware)
            {
                CollectFromHardware(
                    sub,
                    ref cpuPackageTemp,
                    ref cpuPackagePower,
                    ref vrmTemp,
                    ref mbTemp,
                    cpuCoreTemps,
                    fans,
                    otherTemps,
                    storageTemps);
            }
        }
        catch
        {
            // このハードウェア丸ごとの走査失敗は無視して続行する。
        }
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
        if (!sensor.Value.HasValue)
        {
            return;
        }

        double value = sensor.Value.Value;
        HardwareType hwType = hw.HardwareType;

        // 別名は分類より前に適用する。こうすると利用者が "Temperature #3" に "VRM" を割り当てた
        // だけで、以降の名前判定がそれを VRM として拾い、表示名も置き換わる。
        string rawName = sensor.Name ?? "";
        string name = _sensorAliases.TryGetValue(rawName, out string? alias) && !string.IsNullOrWhiteSpace(alias)
            ? alias
            : rawName;

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
    /// 全ハードウェア（サブハードウェアも含む）の値を更新してから走査するための Visitor。
    /// LHM は hardware.Update() を呼ばないと Sensor.Value が更新されない。
    /// </summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }

        public void VisitHardware(IHardware hardware)
        {
            try
            {
                hardware.Update();
            }
            catch
            {
                // このハードウェアの更新失敗は無視して子の走査は継続する。
            }

            foreach (IHardware sub in hardware.SubHardware)
            {
                sub.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
