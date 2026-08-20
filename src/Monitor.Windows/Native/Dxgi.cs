using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Monitor.Windows.Native;

/// <summary>Dedicated/shared VRAM totals and identity for one physical GPU adapter, read via DXGI
/// (WMI's Win32_VideoController.AdapterRAM is a DWORD and saturates at 4 GiB, which DXGI does not).</summary>
public readonly record struct DxgiAdapterInfo(string Description, ulong DedicatedVideoMemory, ulong SharedSystemMemory, uint VendorId, long Luid);

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public int LowPart;
    public int HighPart;
}

// x64 layout, 312 bytes total. DedicatedVideoMemory/DedicatedSystemMemory/SharedSystemMemory are SIZE_T
// (8 bytes on x64); the compiler naturally 8-byte-aligns them after the four UINT fields (offset 272),
// so no explicit padding fields are needed for Sequential layout to match the real ABI.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DXGI_ADAPTER_DESC1
{
    public fixed char Description[128];
    public uint VendorId;
    public uint DeviceId;
    public uint SubSysId;
    public uint Revision;
    public nuint DedicatedVideoMemory;
    public nuint DedicatedSystemMemory;
    public nuint SharedSystemMemory;
    public LUID AdapterLuid;
    public uint Flags;
}

/// <summary>
/// DXGI adapter enumeration, called through hand-resolved vtable slots via raw function pointers instead
/// of a [ComImport] interface. A [ComImport] interface's vtable layout depends on the C# compiler laying
/// out inherited-interface methods in exactly the right slot order, which is fragile and was previously
/// getting it wrong (EnumAdapters1 landed on the wrong slot, so the real return was never S_OK/
/// DXGI_ERROR_NOT_FOUND but garbage). Calling through explicit slot indices removes that risk entirely.
/// </summary>
public static unsafe partial class Dxgi
{
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    // IDXGIFactory1 vtable slots (IUnknown 0-2, IDXGIObject 3-6, IDXGIFactory 7-11, IDXGIFactory1 12-13).
    private const int Factory1_EnumAdapters1Slot = 12;

    // IDXGIAdapter1 vtable slots (IUnknown 0-2, IDXGIObject 3-6, IDXGIAdapter 7-9, IDXGIAdapter1 10).
    private const int Adapter1_GetDesc1Slot = 10;

    private const int IUnknown_ReleaseSlot = 2;

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, void** ppFactory);

    /// <summary>Enumerates physical (non-software) GPU adapters. Tries DXGI first; if DXGI is unavailable
    /// or yields nothing (older Windows, RDP session, etc.) falls back to a registry scan. Never throws.</summary>
    public static IReadOnlyList<DxgiAdapterInfo> EnumerateAdapters()
    {
        try
        {
            IReadOnlyList<DxgiAdapterInfo> viaDxgi = EnumerateAdaptersViaDxgi();
            if (viaDxgi.Count > 0)
            {
                return viaDxgi;
            }
        }
        catch
        {
            // Fall through to the registry fallback below.
        }

        try
        {
            return EnumerateAdaptersFromRegistry();
        }
        catch
        {
            return Array.Empty<DxgiAdapterInfo>();
        }
    }

    private static List<DxgiAdapterInfo> EnumerateAdaptersViaDxgi()
    {
        var results = new List<DxgiAdapterInfo>();

        void* factory = null;
        Guid riid = IID_IDXGIFactory1;
        int hr = CreateDXGIFactory1(&riid, &factory);
        if (hr < 0 || factory is null)
        {
            return results;
        }

        try
        {
            void** factoryVtbl = *(void***)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<void*, uint, void**, int>)factoryVtbl[Factory1_EnumAdapters1Slot];

            uint index = 0;
            while (true)
            {
                void* adapter = null;
                int enumHr = enumAdapters1(factory, index, &adapter);
                index++;

                // EnumAdapters1 returns DXGI_ERROR_NOT_FOUND (negative) once the list is exhausted.
                // Anything else negative, or a null adapter with a non-negative HRESULT, also ends
                // enumeration rather than looping forever.
                if (enumHr < 0 || adapter is null)
                {
                    break;
                }

                try
                {
                    void** adapterVtbl = *(void***)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<void*, DXGI_ADAPTER_DESC1*, int>)adapterVtbl[Adapter1_GetDesc1Slot];

                    DXGI_ADAPTER_DESC1 desc;
                    int descHr = getDesc1(adapter, &desc);

                    if (descHr >= 0 && (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0)
                    {
                        string description = new string(desc.Description);
                        long luid = ((long)desc.AdapterLuid.HighPart << 32) | (uint)desc.AdapterLuid.LowPart;
                        results.Add(new DxgiAdapterInfo(description, desc.DedicatedVideoMemory, desc.SharedSystemMemory, desc.VendorId, luid));
                    }
                }
                finally
                {
                    ComRelease(adapter);
                }
            }
        }
        finally
        {
            ComRelease(factory);
        }

        return results;
    }

    private static void ComRelease(void* unknown)
    {
        if (unknown is null)
        {
            return;
        }

        void** vtbl = *(void***)unknown;
        var release = (delegate* unmanaged[Stdcall]<void*, uint>)vtbl[IUnknown_ReleaseSlot];
        release(unknown);
    }

    /// <summary>Fallback for environments where DXGI enumeration fails outright: scans the display
    /// adapter class key directly. qwMemorySize (QWORD) is used rather than the older MemorySize (DWORD),
    /// which saturates at 4 GiB. No LUID is available at this location, so entries here carry Luid=0 and
    /// will not match PDH's per-adapter "\GPU Engine(*)"/"\GPU Adapter Memory(*)" instances by LUID; this
    /// path exists only so a name and a total VRAM figure are still available when DXGI is unusable.</summary>
    private static List<DxgiAdapterInfo> EnumerateAdaptersFromRegistry()
    {
        var results = new List<DxgiAdapterInfo>();
        const string basePath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(basePath);
        if (classKey is null)
        {
            return results;
        }

        for (int i = 0; i < 100; i++)
        {
            string subKeyName = i.ToString("D4", CultureInfo.InvariantCulture);
            using RegistryKey? adapterKey = classKey.OpenSubKey(subKeyName);
            if (adapterKey is null)
            {
                break;
            }

            object? memSizeValue = adapterKey.GetValue("HardwareInformation.qwMemorySize");
            object? descValue = adapterKey.GetValue("DriverDesc");

            if (memSizeValue is null && descValue is null)
            {
                continue;
            }

            ulong dedicatedVideoMemory = memSizeValue is long qwMemorySize ? unchecked((ulong)qwMemorySize) : 0UL;
            string description = descValue as string ?? $"GPU {i}";

            results.Add(new DxgiAdapterInfo(description, dedicatedVideoMemory, 0UL, 0U, 0L));
        }

        return results;
    }
}
