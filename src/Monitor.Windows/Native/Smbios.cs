using System.Runtime.InteropServices;
using System.Text;
using Monitor.Core.Models;

namespace Monitor.Windows.Native;

/// <summary>
/// Reads physical memory module (DIMM/SPD) information straight from the SMBIOS firmware table via
/// <c>GetSystemFirmwareTable</c> (kernel32, documented, no administrator rights required). Deliberately
/// avoids WMI (Win32_PhysicalMemory) per this codebase's "no System.Management" rule.
/// Every public entry point here never throws: on any failure it returns an empty list, matching the
/// "no exception ever leaves a Provider" convention.
/// </summary>
public static partial class Smbios
{
    private const uint RsmbSignature = 0x52534D42; // 'RSMB'
    private const byte TypePhysicalMemoryArray = 16;
    private const byte TypeMemoryDevice = 17;
    private const byte TypeEndOfTable = 127;

    [LibraryImport("kernel32.dll")]
    private static partial uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId, IntPtr firmwareTableBuffer, uint bufferSize);

    /// <summary>Enumerates every populated memory slot from SMBIOS Type 17 (Memory Device) structures.
    /// <paramref name="slotsTotal"/> is the "Number of Memory Devices" field of the Type 16 (Physical
    /// Memory Array) structure (i.e. the total slot count on the board, including empty ones). Populated
    /// slots (Size != 0) are the only ones returned in the list; <c>slotsTotal - list.Count</c> is the
    /// number of empty slots. Never throws.</summary>
    public static IReadOnlyList<MemoryModuleInfo> ReadMemoryModules(out int slotsTotal)
    {
        slotsTotal = 0;

        try
        {
            uint size = GetSystemFirmwareTable(RsmbSignature, 0, IntPtr.Zero, 0);
            if (size == 0)
            {
                return Array.Empty<MemoryModuleInfo>();
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint written = GetSystemFirmwareTable(RsmbSignature, 0, buffer, size);
                if (written < 8)
                {
                    return Array.Empty<MemoryModuleInfo>();
                }

                // RawSMBIOSData header: +4 DWORD Length of the table data that follows at +8.
                int declaredLength = Marshal.ReadInt32(buffer, 4);
                int tableStart = 8;
                int available = (int)written - tableStart;
                int tableLength = declaredLength > 0 && declaredLength <= available ? declaredLength : Math.Max(0, available);

                return ParseTable(buffer, tableStart, tableLength, out slotsTotal);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            slotsTotal = 0;
            return Array.Empty<MemoryModuleInfo>();
        }
    }

    private static List<MemoryModuleInfo> ParseTable(IntPtr buffer, int tableStart, int tableLength, out int slotsTotal)
    {
        var modules = new List<MemoryModuleInfo>();
        slotsTotal = 0;

        int pos = tableStart;
        int end = tableStart + tableLength;

        // Each structure starts with a common header: BYTE Type; BYTE Length; WORD Handle.
        while (pos + 4 <= end)
        {
            byte type = Marshal.ReadByte(buffer, pos);
            byte len = Marshal.ReadByte(buffer, pos + 1);

            if (len < 4 || pos + len > end)
            {
                break; // malformed structure - stop rather than risk reading garbage
            }

            int formattedEnd = pos + len;
            (List<string> strings, int nextPos) = ReadStringArea(buffer, formattedEnd, end);

            if (type == TypePhysicalMemoryArray && len > 0x0E)
            {
                // +0x0D WORD Number of Memory Devices (confirmed against this machine's raw SMBIOS
                // bytes; some secondary references list +0x0E, which is off by one and reads into the
                // following byte instead).
                slotsTotal = ReadUInt16(buffer, pos + 0x0D);
            }
            else if (type == TypeMemoryDevice)
            {
                MemoryModuleInfo? module = ParseMemoryDevice(buffer, pos, len, strings);
                if (module is not null)
                {
                    modules.Add(module);
                }
            }

            if (nextPos <= pos || type == TypeEndOfTable)
            {
                break;
            }

            pos = nextPos;
        }

        return modules;
    }

    /// <summary>Parses one SMBIOS Type 17 (Memory Device) structure. Every field access is guarded by the
    /// structure's own <c>Length</c> so older SMBIOS versions that omit trailing fields (e.g. Configured
    /// Memory Speed at +0x20, added in SMBIOS 2.3) degrade to 0/null instead of reading out of bounds.</summary>
    private static MemoryModuleInfo? ParseMemoryDevice(IntPtr buffer, int pos, int len, List<string> strings)
    {
        if (len <= 0x0D)
        {
            return null; // not even enough to read the Size field at +0x0C
        }

        ushort sizeRaw = ReadUInt16(buffer, pos + 0x0C);
        if (sizeRaw == 0)
        {
            return null; // unpopulated slot
        }

        ulong capacityBytes;
        if (sizeRaw == 0x7FFF)
        {
            // +0x1C DWORD Extended Size, in MB (confirmed against this machine's raw SMBIOS bytes: some
            // secondary references list +0x20, which actually lands on Configured Memory Speed +
            // Minimum Voltage instead and produces a garbage capacity).
            uint extendedMb = len > 0x1F ? ReadUInt32(buffer, pos + 0x1C) : 0u;
            capacityBytes = (ulong)extendedMb * 1024UL * 1024UL;
        }
        else
        {
            bool isKilobytes = (sizeRaw & 0x8000) != 0;
            ulong sizeValue = (ulong)(sizeRaw & 0x7FFF);
            capacityBytes = isKilobytes ? sizeValue * 1024UL : sizeValue * 1024UL * 1024UL;
        }

        string? deviceLocator = GetFieldString(buffer, pos, 0x10, len, strings);
        string? bankLocator = GetFieldString(buffer, pos, 0x11, len, strings);
        byte memoryTypeCode = len > 0x12 ? Marshal.ReadByte(buffer, pos + 0x12) : (byte)0;
        int speedMhz = len > 0x16 ? ReadUInt16(buffer, pos + 0x15) : 0;
        string? manufacturer = GetFieldString(buffer, pos, 0x17, len, strings);
        string? partNumber = GetFieldString(buffer, pos, 0x1A, len, strings);

        // +0x20 WORD Configured Memory Speed (confirmed against this machine's raw SMBIOS bytes: some
        // secondary references list +0x54, which in a 92-byte Type 17 structure like this one falls in
        // the always-zero Non-Volatile/Volatile/Cache/Logical Size region and never has a real value).
        int configuredSpeedMhz = len > 0x21 ? ReadUInt16(buffer, pos + 0x20) : 0;

        return new MemoryModuleInfo
        {
            Slot = deviceLocator ?? "",
            BankLabel = bankLocator,
            CapacityBytes = capacityBytes,
            SpeedMhz = speedMhz,
            ConfiguredSpeedMhz = configuredSpeedMhz,
            Manufacturer = manufacturer,
            PartNumber = partNumber,
            MemoryType = MapMemoryType(memoryTypeCode),
        };
    }

    /// <summary>Reads a one-byte string-table index field at <c>pos + fieldOffset</c> (guarded by
    /// <paramref name="len"/>) and resolves it against this structure's string list. Returns null for
    /// index 0 ("no string"), an out-of-range index, or an empty string.</summary>
    private static string? GetFieldString(IntPtr buffer, int pos, int fieldOffset, int len, List<string> strings)
    {
        if (len <= fieldOffset)
        {
            return null;
        }

        byte index = Marshal.ReadByte(buffer, pos + fieldOffset);
        if (index == 0 || index > strings.Count)
        {
            return null;
        }

        string value = strings[index - 1].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Reads the NUL-separated string table that follows one structure's formatted area, ending
    /// at the first double-NUL terminator (a structure with no strings is just that double-NUL, 2 bytes).
    /// Returns the parsed strings (1-based index in SMBIOS terms) and the buffer position immediately
    /// after the terminator, i.e. where the next structure begins.</summary>
    private static (List<string> Strings, int NextPos) ReadStringArea(IntPtr buffer, int start, int end)
    {
        var strings = new List<string>();
        int p = start;

        if (p + 1 < end && Marshal.ReadByte(buffer, p) == 0 && Marshal.ReadByte(buffer, p + 1) == 0)
        {
            return (strings, p + 2);
        }

        var current = new List<byte>();
        while (p < end)
        {
            byte b = Marshal.ReadByte(buffer, p);
            p++;

            if (b == 0)
            {
                strings.Add(Encoding.ASCII.GetString(current.ToArray()));
                current.Clear();

                if (p < end && Marshal.ReadByte(buffer, p) == 0)
                {
                    p++;
                    break;
                }
            }
            else
            {
                current.Add(b);
            }
        }

        return (strings, p);
    }

    private static string MapMemoryType(byte code) => code switch
    {
        0x12 => "RDRAM",
        0x13 => "DDR",
        0x14 => "SDRAM",
        0x15 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR4",
        0x1D => "LPDDR5",
        0x22 => "DDR5",
        _ => "",
    };

    private static ushort ReadUInt16(IntPtr buffer, int offset) => unchecked((ushort)Marshal.ReadInt16(buffer, offset));

    private static uint ReadUInt32(IntPtr buffer, int offset) => unchecked((uint)Marshal.ReadInt32(buffer, offset));
}
