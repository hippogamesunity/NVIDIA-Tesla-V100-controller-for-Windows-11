using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TeslaV100Controller;

internal static class DeviceApi
{
    public static IReadOnlyList<GpuDevice> Enumerate(bool presentOnly = true)
    {
        using var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, (presentOnly ? 0x02u : 0) | 0x04);
        if (set.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var devices = new List<GpuDevice>();
        for (uint i = 0; ; i++)
        {
            var data = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            if (!SetupDiEnumDeviceInfo(set, i, ref data))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 259) break;
                throw new Win32Exception(error);
            }
            var id = new StringBuilder(1024);
            if (!SetupDiGetDeviceInstanceIdW(set, ref data, id, id.Capacity, out _)) continue;
            if (!GpuDevice.IsTarget(id.ToString())) continue;
            uint result = CM_Get_DevNode_Status(out uint status, out uint problem, data.DevInst, 0);
            string name = TextProperty(set, ref data, 12);
            if (string.IsNullOrEmpty(name)) name = TextProperty(set, ref data, 0);
            uint? bus = NumberProperty(set, ref data, 0x15), address = NumberProperty(set, ref data, 0x1C);
            string busId = bus.HasValue && address.HasValue
                ? $"00000000:{bus:X2}:{address >> 16:X2}.{address & 0xffff}" : "";
            devices.Add(new(id.ToString(), name, result == 0 ? (int)problem : -1,
                result == 0 && (status & 0x02) != 0, TextProperty(set, ref data, 9),
                TextProperty(set, ref data, 4), busId));
        }
        return devices;
    }

    private static byte[] Property(DeviceSet set, ref DeviceInfo data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out uint size);
        if (size == 0 || size > 65536) return [];
        var buffer = new byte[size];
        return SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, size, out _) ? buffer : [];
    }
    private static string TextProperty(DeviceSet set, ref DeviceInfo data, uint property) =>
        Encoding.Unicode.GetString(Property(set, ref data, property)).Split('\0')[0];
    private static uint? NumberProperty(DeviceSet set, ref DeviceInfo data, uint property)
    {
        byte[] value = Property(set, ref data, property);
        return value.Length >= 4 ? BitConverter.ToUInt32(value) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    private sealed class DeviceSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DeviceSet() : base(true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern DeviceSet SetupDiGetClassDevsW(IntPtr guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(DeviceSet set, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(DeviceSet set, ref DeviceInfo data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(DeviceSet set, ref DeviceInfo data, uint property,
        out uint type, byte[]? buffer, uint size, out uint required);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
