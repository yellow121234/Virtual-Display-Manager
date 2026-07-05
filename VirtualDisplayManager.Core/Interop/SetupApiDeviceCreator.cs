using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualDisplayManager.Core.Interop;

internal static class SetupApiDeviceCreator
{
    private const uint DicdGenerateId = 0x00000001;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint DifRegisterDevice = 0x00000019;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly Guid DisplayClassGuid = new("4D36E968-E325-11CE-BFC1-08002BE10318");

    public static void CreateRootDisplayDevice(string hardwareId, string description)
    {
        if (!hardwareId.StartsWith("Root\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a Root-enumerated VDD hardware ID may be created.");

        var classGuid = DisplayClassGuid;
        var deviceInfoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (deviceInfoSet == InvalidHandleValue)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var deviceInfo = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
            if (!SetupDiCreateDeviceInfo(deviceInfoSet, description, ref classGuid, null,
                    IntPtr.Zero, DicdGenerateId, ref deviceInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var multiSz = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(deviceInfoSet, ref deviceInfo, SpdrpHardwareId,
                    multiSz, (uint)multiSz.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!SetupDiCallClassInstaller(DifRegisterDevice, deviceInfoSet, ref deviceInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCreateDeviceInfo(IntPtr deviceInfoSet, string deviceName,
        ref Guid classGuid, string? deviceDescription, IntPtr hwndParent, uint creationFlags,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData, uint property, byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
