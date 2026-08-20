using System.Globalization;
using Monitor.Core.Formatting;
using Monitor.Core.Models;

namespace Monitor.App.ViewModels;

/// <summary>ページファイル一覧の1行分。都度イミュータブルに作り直す（<see cref="MemoryModuleRowViewModel"/> と同じ方針）。</summary>
public sealed class PageFileRowViewModel
{
    public PageFileRowViewModel(PageFileInfo p)
    {
        PathText = string.IsNullOrWhiteSpace(p.Path) ? "—" : p.Path;
        PercentText = ByteFormatter.Percent(p.UsagePercent);
        GaugeValue = Math.Clamp(p.UsagePercent, 0.0, 100.0);
        UsageText = string.Create(
            CultureInfo.InvariantCulture,
            $"{FormatGb(p.UsedBytes)} / {FormatGb(p.TotalBytes)} GB");
        PeakText = string.Create(CultureInfo.InvariantCulture, $"ピーク {FormatGb(p.PeakBytes)} GB");
    }

    public string PathText { get; }

    public string PercentText { get; }

    public double GaugeValue { get; }

    public string UsageText { get; }

    public string PeakText { get; }

    private static string FormatGb(ulong bytes) =>
        (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F2", CultureInfo.InvariantCulture);
}
