using System.Runtime.InteropServices;

namespace Monitor.Windows.Native;

/// <summary>Mirrors the native MIB_IF_ROW2 struct (netioapi.h) field-for-field, relying on the CLR's default
/// (non-Pack=1) sequential layout to reproduce the compiler padding the real struct has (notably the 4 bytes
/// between InterfaceAndOperStatusFlags/OperStatus and the ULONG64 counters that follow, and the 4 bytes of
/// padding MIB_IF_TABLE2 inserts before its Table[] array, handled separately in ReadInterfaceTable).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public unsafe struct MIB_IF_ROW2
{
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public Guid InterfaceGuid;
    public fixed char Alias[257];
    public fixed char Description[257];
    public uint PhysicalAddressLength;
    public fixed byte PhysicalAddress[32];
    public fixed byte PermanentPhysicalAddress[32];
    public uint Mtu;
    public uint Type;
    public int TunnelType;
    public int MediaType;
    public int PhysicalMediumType;
    public int AccessType;
    public int DirectionType;
    public byte InterfaceAndOperStatusFlags;
    public int OperStatus;
    public int AdminStatus;
    public int MediaConnectState;
    public Guid NetworkGuid;
    public int ConnectionType;

    public ulong TransmitLinkSpeed;
    public ulong ReceiveLinkSpeed;

    public ulong InOctets;
    public ulong InUcastPkts;
    public ulong InNUcastPkts;
    public ulong InDiscards;
    public ulong InErrors;
    public ulong InUnknownProtos;
    public ulong InUcastOctets;
    public ulong InMulticastOctets;
    public ulong InBroadcastOctets;

    public ulong OutOctets;
    public ulong OutUcastPkts;
    public ulong OutNUcastPkts;
    public ulong OutDiscards;
    public ulong OutErrors;
    public ulong OutUcastOctets;
    public ulong OutMulticastOctets;
    public ulong OutBroadcastOctets;

    public ulong OutQLen;

    public readonly string GetAlias()
    {
        fixed (char* p = Alias)
        {
            return new string(p);
        }
    }

    public readonly string GetDescription()
    {
        fixed (char* p = Description)
        {
            return new string(p);
        }
    }

    /// <summary>InterfaceAndOperStatusFlags の bit1 (0x02)。立っていれば WFP 等のフィルタ用疑似 IF。</summary>
    public readonly bool IsFilterInterface => (InterfaceAndOperStatusFlags & 0x02) != 0;
}

public static partial class IpHlpApi
{
    public const int IfOperStatusUp = 1;
    public const int IF_TYPE_SOFTWARE_LOOPBACK = 24;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIfTable2(out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(IntPtr memory);

    /// <summary>Enumerates all network interfaces via GetIfTable2. MIB_IF_TABLE2 is a variable-length
    /// struct ({ ULONG NumEntries; MIB_IF_ROW2 Table[ANY_SIZE]; }) so it is not marshalled as a whole;
    /// instead the header is read manually and each MIB_IF_ROW2 entry is read by pointer arithmetic.
    /// Never throws; returns an empty list on any failure.</summary>
    public static unsafe IReadOnlyList<MIB_IF_ROW2> ReadInterfaceTable()
    {
        IntPtr tablePtr = IntPtr.Zero;
        try
        {
            uint status = GetIfTable2(out tablePtr);
            if (status != 0 || tablePtr == IntPtr.Zero)
            {
                return Array.Empty<MIB_IF_ROW2>();
            }

            uint numEntries = unchecked((uint)Marshal.ReadInt32(tablePtr));

            // MIB_IF_ROW2 requires 8-byte alignment (it starts with a ULONG64 LUID union), so the compiler
            // inserts 4 bytes of padding after the leading ULONG NumEntries before Table[] begins.
            // Use the unmanaged (unsafe) sizeof, not Marshal.SizeOf: this struct is read via raw pointer
            // dereference below (no interop marshaling), so the stride must match the CLR's in-memory
            // layout exactly, not whatever size the (Ansi-by-default) marshaler would compute.
            int rowSize = sizeof(MIB_IF_ROW2);
            IntPtr firstRow = IntPtr.Add(tablePtr, 8);

            var result = new List<MIB_IF_ROW2>((int)numEntries);
            for (int i = 0; i < numEntries; i++)
            {
                IntPtr rowPtr = IntPtr.Add(firstRow, i * rowSize);
                // Marshal.PtrToStructure would marshal the fixed char[] buffers as ANSI (this struct's
                // StructLayout default before was Ansi, and even with CharSet.Unicode PtrToStructure is
                // unnecessary overhead here) and corrupt them. The struct is entirely blittable (fixed
                // buffers + primitives), so a raw pointer read reproduces the native memory exactly.
                result.Add(*(MIB_IF_ROW2*)rowPtr);
            }

            return result;
        }
        catch
        {
            return Array.Empty<MIB_IF_ROW2>();
        }
        finally
        {
            if (tablePtr != IntPtr.Zero)
            {
                FreeMibTable(tablePtr);
            }
        }
    }
}
