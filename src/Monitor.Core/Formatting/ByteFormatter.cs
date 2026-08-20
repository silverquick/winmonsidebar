using System.Globalization;

namespace Monitor.Core.Formatting;

/// <summary>
/// バイト数・転送速度を人間が読みやすい文字列へ整形する。
/// </summary>
public static class ByteFormatter
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] ByteRateUnits = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s", "PB/s"];
    private static readonly string[] BitRateUnits = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];

    /// <summary>例: "1.2 GB"。</summary>
    public static string Bytes(double value) => Format(value, 1024d, ByteUnits);

    /// <summary>例: "1.2 MB/s"。</summary>
    public static string BytesPerSec(double value) => Format(value, 1024d, ByteRateUnits);

    /// <summary>バイト/秒を bps 換算して整形する。例: "842 Mbps"。</summary>
    public static string Bits(double bytesPerSec)
    {
        double bitsPerSec = Normalize(bytesPerSec) * 8d;
        return Format(bitsPerSec, 1000d, BitRateUnits);
    }

    /// <summary>例: "51°C"。null なら "—"。</summary>
    public static string Temperature(double? c) =>
        c.HasValue ? $"{c.Value.ToString("F0", CultureInfo.InvariantCulture)}°C" : "—";

    /// <summary>1000以上なら "3.70 GHz"、未満なら "850 MHz"。</summary>
    public static string Clock(double mhz)
    {
        double normalized = Normalize(mhz);
        return normalized >= 1000d
            ? $"{(normalized / 1000d).ToString("F2", CultureInfo.InvariantCulture)} GHz"
            : $"{normalized.ToString("F0", CultureInfo.InvariantCulture)} MHz";
    }

    /// <summary>例: "80.7 W"。null なら "—"。</summary>
    public static string Watts(double? w) =>
        w.HasValue ? $"{w.Value.ToString("F1", CultureInfo.InvariantCulture)} W" : "—";

    /// <summary>例: "1420 rpm"。null なら "—"。</summary>
    public static string Rpm(int? rpm) =>
        rpm.HasValue ? $"{rpm.Value.ToString(CultureInfo.InvariantCulture)} rpm" : "—";

    /// <summary>例: "72%"。</summary>
    public static string Percent(double v) => $"{Normalize(v).ToString("F0", CultureInfo.InvariantCulture)}%";

    private static string Format(double value, double baseValue, string[] units)
    {
        double scaled = Normalize(value);
        int unitIndex = 0;

        while (scaled >= baseValue && unitIndex < units.Length - 1)
        {
            scaled /= baseValue;
            unitIndex++;
        }

        // 先頭の単位（未換算の生の値）は小数を出さず、換算後は 10 未満なら小数1桁、それ以外は0桁。
        int decimals = unitIndex == 0 ? 0 : (scaled < 10d ? 1 : 0);
        string format = decimals == 0 ? "F0" : "F1";

        return $"{scaled.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private static double Normalize(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return 0d;
        }

        return value;
    }
}
