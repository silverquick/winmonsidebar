namespace Monitor.Core.Models;

/// <summary>1本の物理メモリモジュール（DIMM）の SPD 情報。SMBIOS Type 17 (Memory Device) から得る。
/// 未実装スロットは <see cref="MemorySnapshot.Modules"/> に含めない。</summary>
public sealed record MemoryModuleInfo
{
    /// <summary>SMBIOS Device Locator。例 "DIMM_A1"。</summary>
    public string Slot { get; init; } = "";

    /// <summary>SMBIOS Bank Locator。例 "BANK 0"。取れなければ null。</summary>
    public string? BankLabel { get; init; }

    public ulong CapacityBytes { get; init; }

    /// <summary>SPD 上の定格速度 (MT/s)。取れなければ 0。</summary>
    public int SpeedMhz { get; init; }

    /// <summary>実際の動作速度 (MT/s)。SMBIOS 構造体が短く値を持たない場合は 0。</summary>
    public int ConfiguredSpeedMhz { get; init; }

    public string? Manufacturer { get; init; }

    public string? PartNumber { get; init; }

    /// <summary>例 "DDR4"。判別できない場合は空文字列。</summary>
    public string MemoryType { get; init; } = "";
}

/// <summary>1本のページファイルの容量・使用量。<see cref="MemorySnapshot.PageFiles"/> の要素。</summary>
public sealed record PageFileInfo
{
    /// <summary>表示用に正規化されたパス。例 "C:\pagefile.sys"。</summary>
    public string Path { get; init; } = "";

    public ulong TotalBytes { get; init; }

    public ulong UsedBytes { get; init; }

    public ulong PeakBytes { get; init; }

    public double UsagePercent { get; init; }
}

/// <summary>物理メモリ・コミット量・内訳（キャッシュ/プール/圧縮メモリ等）・ページファイル・
/// 物理メモリモジュール(SPD)構成のスナップショット。取得できなかった値は 0 / null / 空リストで表す。</summary>
public sealed record MemorySnapshot
{
    // 既存: 物理メモリの総量・空き・使用量とコミット量。
    public ulong TotalBytes { get; init; }
    public ulong AvailableBytes { get; init; }
    public ulong UsedBytes { get; init; }
    public double UsedPercent { get; init; }
    public ulong CommittedBytes { get; init; }
    public ulong CommitLimitBytes { get; init; }

    // 内訳（取れなければ 0）。
    public ulong CachedBytes { get; init; }
    public ulong StandbyBytes { get; init; }
    public ulong ModifiedBytes { get; init; }
    public ulong FreeBytes { get; init; }

    /// <summary>メモリ圧縮ストアのサイズ。"Memory Compression" プロセスのワーキングセット由来。</summary>
    public ulong CompressedBytes { get; init; }

    public ulong PoolPagedBytes { get; init; }
    public ulong PoolNonPagedBytes { get; init; }
    public ulong SystemCacheBytes { get; init; }

    /// <summary>BIOS が報告するハードウェア搭載量 (GetPhysicallyInstalledSystemMemory)。</summary>
    public ulong InstalledBytes { get; init; }

    /// <summary>InstalledBytes - TotalBytes（負になる場合は 0）。OS/ファームウェアに予約された分。</summary>
    public ulong HardwareReservedBytes { get; init; }

    public ulong CommitPeakBytes { get; init; }

    // カーネルオブジェクト（GetPerformanceInfo 由来）。
    public int HandleCount { get; init; }
    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }

    // ページファイル。
    public IReadOnlyList<PageFileInfo> PageFiles { get; init; } = Array.Empty<PageFileInfo>();
    public ulong PageFileTotalBytes { get; init; }
    public ulong PageFileUsedBytes { get; init; }
    public ulong PageFilePeakBytes { get; init; }
    public double PageFileUsagePercent { get; init; }

    // モジュール（SPD、SMBIOS 由来）。
    public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = Array.Empty<MemoryModuleInfo>();
    public int SlotsUsed { get; init; }
    public int SlotsTotal { get; init; }

    /// <summary>代表値（モジュールの ConfiguredSpeedMhz の最頻値。取れなければ SpeedMhz の最頻値）。</summary>
    public int SpeedMhz { get; init; }

    public static MemorySnapshot Empty { get; } = new();
}
