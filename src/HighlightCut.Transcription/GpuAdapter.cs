using System.Runtime.InteropServices;

namespace HighlightCut.Transcription;

/// <summary>
/// The graphics adapter DirectML runs on: the first one DXGI lists (DirectML's device 0, which sherpa-onnx uses),
/// normally the GPU the main display is connected to.
/// </summary>
/// <param name="Name">"AMD Radeon RX 7800 XT".</param>
/// <param name="DriverVersion">"32.0.21013.1000".</param>
/// <param name="IsSoftware">The Microsoft Basic Render Driver (no GPU driver, virtual machines): DirectML refuses it.</param>
public sealed record GpuAdapter(string Name, int VendorId, int DeviceId, string DriverVersion, bool IsSoftware)
{
    /// <summary>The adapter of this machine, or null when there is none or not on Windows.</summary>
    public static GpuAdapter? Current { get; } = OperatingSystem.IsWindows() ? Find() : null;

    /// <summary>Names the GPU and its driver, so a new one (or a new driver) is checked again.</summary>
    public string Key => $"{Name}|{VendorId:x4}:{DeviceId:x4}|{DriverVersion}";

    private static GpuAdapter? Find()
    {
        try
        {
            return Dxgi.FirstAdapter();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return null;
        }
    }

    /// <summary>Just enough of DXGI (dxgi.dll's COM interfaces, called through their tables) to name adapter 0.</summary>
    private static unsafe class Dxgi
    {
        private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        private static readonly Guid DeviceInterfaceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
        private const int NotFound = unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND: no adapter
        private const uint SoftwareFlag = 2; // DXGI_ADAPTER_FLAG_SOFTWARE

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDesc1
        {
            public fixed char Description[128];
            public uint VendorId, DeviceId, SubSysId, Revision;
            public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

        private static void* Method(IntPtr com, int slot) => (*(void***)com)[slot];

        private static void Release(IntPtr com) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Method(com, 2))(com);

        public static GpuAdapter? FirstAdapter()
        {
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(FactoryId, out var factory));
            try
            {
                // IDXGIFactory1::EnumAdapters1 is slot 12 (IUnknown 3, IDXGIObject 4, IDXGIFactory 5).
                IntPtr adapter;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Method(factory, 12))(factory, 0, &adapter);
                if (hr == NotFound)
                    return null;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    // IDXGIAdapter1::GetDesc1 is slot 10, IDXGIAdapter::CheckInterfaceSupport slot 9.
                    AdapterDesc1 desc;
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)Method(adapter, 10))(adapter, &desc));
                    long umd = 0;
                    Guid device = DeviceInterfaceId;
                    string driver = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, long*, int>)Method(adapter, 9))(adapter, &device, &umd) >= 0
                        ? $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}"
                        : "unknown";
                    string name = new string(desc.Description).TrimEnd('\0').Trim();
                    // The Basic Render Driver is vendor 0x1414, device 0x8c, whether or not it is flagged as software.
                    bool software = (desc.Flags & SoftwareFlag) != 0 || (desc.VendorId == 0x1414 && desc.DeviceId == 0x8c);
                    return new GpuAdapter(name, (int)desc.VendorId, (int)desc.DeviceId, driver, software);
                }
                finally
                {
                    Release(adapter);
                }
            }
            finally
            {
                Release(factory);
            }
        }
    }
}
