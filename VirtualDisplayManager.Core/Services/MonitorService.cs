using System.Runtime.InteropServices;
using VirtualDisplayManager.Core.Models;

namespace VirtualDisplayManager.Core.Services;

public sealed class MonitorService
{
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
    private const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;
    private const int ENUM_CURRENT_SETTINGS = -1;

    private readonly object _sync = new();
    private IReadOnlyList<MonitorInfo> _monitors = [];

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        lock (_sync)
            return _monitors.Count == 0 ? RefreshMonitors() : _monitors.ToArray();
    }

    public IReadOnlyList<MonitorInfo> RefreshMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var seenDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddMonitorHandles(monitors, seenDeviceNames);
        AddDisplayDevicesFallback(monitors, seenDeviceNames);

        lock (_sync)
            _monitors = monitors
                .GroupBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(monitor => monitor.Width > 0 && monitor.Height > 0).First())
                .OrderByDescending(monitor => monitor.IsPrimary)
                .ThenBy(monitor => monitor.X)
                .ThenBy(monitor => monitor.Y)
                .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return _monitors.ToArray();
    }

    public MonitorInfo? GetPrimaryMonitor() => GetMonitors().FirstOrDefault(monitor => monitor.IsPrimary);

    public MonitorInfo? GetMonitorById(string monitorId) =>
        GetMonitors().FirstOrDefault(monitor => monitor.Id.Equals(monitorId, StringComparison.Ordinal));

    private static void AddMonitorHandles(List<MonitorInfo> monitors, HashSet<string> seenDeviceNames)
    {
        var callback = new MonitorEnumProc((handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(handle, ref info)) return true;

            var display = GetDisplayDevice(info.DeviceName);
            var adapter = GetDisplayAdapter(info.DeviceName);
            var name = PickDisplayName(display, adapter, info.DeviceName);
            var deviceId = FirstNonEmpty(display.DeviceId, adapter.DeviceId);
            var bounds = info.Monitor;
            var isPrimary = (info.Flags & 1) != 0 || HasFlag(adapter.StateFlags, DISPLAY_DEVICE_PRIMARY_DEVICE);
            var isVdd = ContainsVddHint(name, info.DeviceName, deviceId, display.DeviceKey, adapter.DeviceKey, adapter.DeviceString);
            var id = BuildMonitorId(info.DeviceName, deviceId, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);

            monitors.Add(new MonitorInfo(id, name, info.DeviceName, deviceId,
                bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                isPrimary, isVdd));
            seenDeviceNames.Add(info.DeviceName);
            return true;
        });

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new InvalidOperationException($"EnumDisplayMonitors 실패: {Marshal.GetLastWin32Error()}");
    }

    private static void AddDisplayDevicesFallback(List<MonitorInfo> monitors, HashSet<string> seenDeviceNames)
    {
        for (uint index = 0; ; index++)
        {
            var adapter = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref adapter, 0)) break;

            if (string.IsNullOrWhiteSpace(adapter.DeviceName)) continue;
            if (seenDeviceNames.Contains(adapter.DeviceName)) continue;
            if (HasFlag(adapter.StateFlags, DISPLAY_DEVICE_MIRRORING_DRIVER)) continue;

            var isAttached = HasFlag(adapter.StateFlags, DISPLAY_DEVICE_ATTACHED_TO_DESKTOP);
            var isVdd = ContainsVddHint(adapter.DeviceString, adapter.DeviceName, adapter.DeviceId, adapter.DeviceKey);

            // 일반 비활성 그래픽 어댑터까지 전부 넣으면 목록이 지저분해집니다.
            // 그래서 활성 데스크톱 출력 또는 VDD로 추정되는 출력만 보강합니다.
            if (!isAttached && !isVdd) continue;

            var display = GetDisplayDevice(adapter.DeviceName);
            var mode = GetCurrentDisplayMode(adapter.DeviceName);
            var width = mode.Width;
            var height = mode.Height;
            var x = mode.X;
            var y = mode.Y;

            var name = PickDisplayName(display, adapter, adapter.DeviceName);
            if (!isAttached && isVdd)
                name += " (비활성 VDD 장치)";

            var deviceId = FirstNonEmpty(display.DeviceId, adapter.DeviceId);
            var id = BuildMonitorId(adapter.DeviceName, deviceId, x, y, width, height);

            monitors.Add(new MonitorInfo(id, name, adapter.DeviceName, deviceId,
                x, y, width, height,
                HasFlag(adapter.StateFlags, DISPLAY_DEVICE_PRIMARY_DEVICE),
                ContainsVddHint(name, adapter.DeviceName, deviceId, display.DeviceKey, adapter.DeviceKey, adapter.DeviceString)));
            seenDeviceNames.Add(adapter.DeviceName);
        }
    }

    private static (int X, int Y, int Width, int Height) GetCurrentDisplayMode(string deviceName)
    {
        var mode = new DevMode
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<DevMode>()
        };

        if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref mode, 0))
            return (0, 0, 0, 0);

        return (mode.dmPositionX, mode.dmPositionY, checked((int)mode.dmPelsWidth), checked((int)mode.dmPelsHeight));
    }

    private static DisplayDevice GetDisplayAdapter(string deviceName)
    {
        for (uint index = 0; ; index++)
        {
            var adapter = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref adapter, 0)) break;
            if (adapter.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) return adapter;
        }
        return new DisplayDevice();
    }

    private static DisplayDevice GetDisplayDevice(string deviceName)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(deviceName, 0, ref device, 0) ? device : new DisplayDevice();
    }

    private static string PickDisplayName(DisplayDevice display, DisplayDevice adapter, string fallback)
    {
        foreach (var candidate in new[] { display.DeviceString, adapter.DeviceString, fallback })
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        return fallback;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value;
        return string.Empty;
    }

    private static string BuildMonitorId(string deviceName, string deviceId, int x, int y, int width, int height) =>
        $"{deviceName}|{deviceId}|{x},{y},{width},{height}";

    private static bool HasFlag(uint value, uint flag) => (value & flag) == flag;

    private static bool ContainsVddHint(params string[] values)
    {
        var text = string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
        return text.Contains("mttvdd") || text.Contains("virtual display driver") ||
               text.Contains("iddsampledriver") || text.Contains("mikethetech") ||
               text.Contains("virtualdrivers") || text.Contains("virtual display");
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip,
        MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceNumber,
        ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNum,
        ref DevMode devMode, uint flags);
}
