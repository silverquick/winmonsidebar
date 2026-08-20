using System.Runtime.InteropServices;

namespace Monitor.Windows.Native;

/// <summary>The formatted value union returned by PdhGetFormattedCounterValue / PdhGetFormattedCounterArrayW.
/// Only CStatus and doubleValue are used by this codebase (we always request PDH_FMT_DOUBLE).</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct PDH_FMT_COUNTERVALUE
{
    [FieldOffset(0)] public uint CStatus;
    [FieldOffset(8)] public int longValue;
    [FieldOffset(8)] public double doubleValue;
    [FieldOffset(8)] public long largeValue;
    [FieldOffset(8)] public IntPtr AnsiStringValue;
    [FieldOffset(8)] public IntPtr WideStringValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PDH_FMT_COUNTERVALUE_ITEM_W
{
    public IntPtr szName;
    public PDH_FMT_COUNTERVALUE FmtValue;
}

/// <summary>Raw P/Invoke declarations for pdh.dll. Do not use directly outside this folder;
/// use the managed wrapper in PdhQuery.cs instead.</summary>
internal static partial class Pdh
{
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_LARGE = 0x00000400;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;

    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_CSTATUS_VALID_DATA = 0;
    public const uint PDH_CSTATUS_NEW_DATA = 1;
    public const uint PDH_INVALID_DATA = 0xC0000BC6;
    public const uint PDH_NO_DATA = 0x800007D5;
    public const uint PDH_CALC_NEGATIVE_DENOMINATOR = 0x800007D6;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(IntPtr hQuery);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    /// <summary>ItemBuffer must be an IntPtr owned/allocated by the caller (e.g. via Marshal.AllocHGlobal);
    /// call twice, first with bufferSize=0 to obtain PDH_MORE_DATA and the required size.</summary>
    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr ItemBuffer);

    [LibraryImport("pdh.dll", EntryPoint = "PdhExpandWildCardPathW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhExpandWildCardPathW(string? szDataSource, string szWildCardPath, IntPtr mszExpandedPathList, ref uint pcchPathListLength, uint dwFlags);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhRemoveCounter(IntPtr hCounter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(IntPtr hQuery);
}
