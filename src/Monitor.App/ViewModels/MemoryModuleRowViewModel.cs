using System.Globalization;
using Monitor.Core.Models;

namespace Monitor.App.ViewModels;

/// <summary>
/// メモリモジュール一覧の1行分（1本の DIMM）。SPD 情報は稼働中に変わらないため、
/// <see cref="DiskRowViewModel"/> 等と違い都度イミュータブルに作り直す。
/// </summary>
public sealed class MemoryModuleRowViewModel
{
    public MemoryModuleRowViewModel(MemoryModuleInfo m)
    {
        SlotText = string.IsNullOrWhiteSpace(m.Slot) ? "—" : m.Slot;
        CapacityText = m.CapacityBytes > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{m.CapacityBytes / 1024.0 / 1024.0 / 1024.0:F0}GB")
            : "—";

        int speed = m.ConfiguredSpeedMhz > 0 ? m.ConfiguredSpeedMhz : m.SpeedMhz;
        SpeedText = speed > 0 ? speed.ToString(CultureInfo.InvariantCulture) : "—";

        ManufacturerText = string.IsNullOrWhiteSpace(m.Manufacturer) ? "—" : m.Manufacturer!;

        PartNumberText = string.IsNullOrWhiteSpace(m.PartNumber) ? "—" : m.PartNumber!;
    }

    public string SlotText { get; }

    public string CapacityText { get; }

    public string SpeedText { get; }

    public string ManufacturerText { get; }

    /// <summary>幅が狭いため一覧本体には出さず、ツールチップ用に持つ。</summary>
    public string PartNumberText { get; }
}
