using System.Runtime.InteropServices;

namespace VirtualDisplayManager.App.Services;

public sealed record DisplayPrimarySwitchResult(
    bool Changed,
    string PrimaryDeviceName,
    string PrimaryDisplayName,
    int PhysicalDisplayCount,
    int VirtualDisplayCount);

public static class DisplayRecoveryService
{
    public static Task<DisplayPrimarySwitchResult> MakePhysicalDisplayPrimaryPreserveExtendAsync() =>
        Task.Run(MakePhysicalDisplayPrimaryPreserveExtend);

    public static Task<DisplayPrimarySwitchResult> SwitchPrimaryDisplayPreserveExtendAsync() =>
        Task.Run(SwitchPrimaryDisplayPreserveExtend);

    private static DisplayPrimarySwitchResult MakePhysicalDisplayPrimaryPreserveExtend()
    {
        var displays = GetAttachedDesktopDisplays();
        if (displays.Count == 0)
            throw new InvalidOperationException("활성 디스플레이를 찾지 못했습니다.");

        var physicalDisplays = displays
            .Where(display => !display.IsVirtualDisplay)
            .OrderByDescending(display => display.IsPrimary)
            .ThenBy(display => display.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (physicalDisplays.Length == 0)
            throw new InvalidOperationException("VDD가 아닌 물리 디스플레이를 찾지 못했습니다.");

        var target = physicalDisplays[0];
        var virtualCount = displays.Count(display => display.IsVirtualDisplay);

        if (target.IsPrimary && target.X == 0 && target.Y == 0)
        {
            return new DisplayPrimarySwitchResult(
                Changed: false,
                PrimaryDeviceName: target.DeviceName,
                PrimaryDisplayName: target.DisplayName,
                PhysicalDisplayCount: physicalDisplays.Length,
                VirtualDisplayCount: virtualCount);
        }

        // Windows에서 주 디스플레이는 좌표 (0,0)에 있어야 합니다.
        // 기존 확장 배치를 보존하기 위해 모든 디스플레이 좌표를 같은 만큼 이동합니다.
        var offsetX = target.X;
        var offsetY = target.Y;

        foreach (var display in displays)
        {
            var mode = display.Mode;
            mode.dmFields |= DM_POSITION;
            mode.dmPositionX = display.X - offsetX;
            mode.dmPositionY = display.Y - offsetY;

            var flags = CDS_UPDATEREGISTRY | CDS_NORESET;
            if (display.DeviceName.Equals(target.DeviceName, StringComparison.OrdinalIgnoreCase))
                flags |= CDS_SET_PRIMARY;

            var result = ChangeDisplaySettingsEx(display.DeviceName, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
            if (result != DISP_CHANGE_SUCCESSFUL)
                throw new InvalidOperationException($"{display.DeviceName} 위치/주 디스플레이 설정 실패: {DescribeDisplayChangeResult(result)} ({result})");
        }

        var applyResult = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (applyResult != DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException($"디스플레이 설정 적용 실패: {DescribeDisplayChangeResult(applyResult)} ({applyResult})");

        return new DisplayPrimarySwitchResult(
            Changed: true,
            PrimaryDeviceName: target.DeviceName,
            PrimaryDisplayName: target.DisplayName,
            PhysicalDisplayCount: physicalDisplays.Length,
            VirtualDisplayCount: virtualCount);
    }

    private static DisplayPrimarySwitchResult SwitchPrimaryDisplayPreserveExtend()
    {
        var displays = GetAttachedDesktopDisplays()
            .OrderBy(display => GetDisplayNumber(display.DeviceName))
            .ThenBy(display => display.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (displays.Count < 2)
            throw new InvalidOperationException("전환할 다른 디스플레이가 없습니다. 확장 모니터가 2개 이상이어야 합니다.");

        var currentPrimaryIndex = displays.FindIndex(display => display.IsPrimary);
        if (currentPrimaryIndex < 0)
        {
            currentPrimaryIndex = displays.FindIndex(display => display.X == 0 && display.Y == 0);
            if (currentPrimaryIndex < 0) currentPrimaryIndex = 0;
        }

        var target = displays[(currentPrimaryIndex + 1) % displays.Count];
        SetPrimaryForF12(displays, target);
        return new DisplayPrimarySwitchResult(
            Changed: true,
            PrimaryDeviceName: target.DeviceName,
            PrimaryDisplayName: target.DisplayName,
            PhysicalDisplayCount: displays.Count(display => !display.IsVirtualDisplay),
            VirtualDisplayCount: displays.Count(display => display.IsVirtualDisplay));
    }

    private static void SetPrimaryForF12(IReadOnlyCollection<DisplayEntry> displays, DisplayEntry target)
    {
        var offsetX = target.X;
        var offsetY = target.Y;
        var orderedDisplays = displays
            .OrderByDescending(display => display.DeviceName.Equals(target.DeviceName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        try
        {
            foreach (var display in orderedDisplays)
            {
                // VDD가 거부할 수 있는 해상도/주사율 필드는 제외하고 위치만 전달합니다.
                var mode = CreatePositionMode(display.X - offsetX, display.Y - offsetY);
                var flags = CDS_UPDATEREGISTRY | CDS_NORESET;
                if (display.DeviceName.Equals(target.DeviceName, StringComparison.OrdinalIgnoreCase))
                    flags |= CDS_SET_PRIMARY;

                var result = ChangeDisplaySettingsEx(display.DeviceName, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
                if (result != DISP_CHANGE_SUCCESSFUL)
                    throw new InvalidOperationException($"{display.DeviceName} 위치/주 디스플레이 설정 실패: {DescribeDisplayChangeResult(result)} ({result})");
            }

            var applyResult = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (applyResult != DISP_CHANGE_SUCCESSFUL)
                throw new InvalidOperationException($"디스플레이 설정 적용 실패: {DescribeDisplayChangeResult(applyResult)} ({applyResult})");
        }
        catch
        {
            RestoreOriginalLayout(displays);
            throw;
        }
    }

    private static DevMode CreatePositionMode(int x, int y) => new()
    {
        dmDeviceName = string.Empty,
        dmFormName = string.Empty,
        dmSize = (ushort)Marshal.SizeOf<DevMode>(),
        dmFields = DM_POSITION,
        dmPositionX = x,
        dmPositionY = y
    };

    private static void RestoreOriginalLayout(IReadOnlyCollection<DisplayEntry> displays)
    {
        try
        {
            foreach (var display in displays.OrderByDescending(display => display.IsPrimary))
            {
                var mode = CreatePositionMode(display.X, display.Y);
                var flags = CDS_UPDATEREGISTRY | CDS_NORESET;
                if (display.IsPrimary) flags |= CDS_SET_PRIMARY;
                _ = ChangeDisplaySettingsEx(display.DeviceName, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
            }

            _ = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        }
        catch
        {
            // 원래 F12 오류를 유지합니다. F11 복구 기능은 그대로 사용할 수 있습니다.
        }
    }

    private static List<DisplayEntry> GetAttachedDesktopDisplays()
    {
        var displays = new List<DisplayEntry>();

        for (uint index = 0; ; index++)
        {
            var adapter = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref adapter, 0)) break;

            if (string.IsNullOrWhiteSpace(adapter.DeviceName)) continue;
            if (!HasFlag(adapter.StateFlags, DISPLAY_DEVICE_ATTACHED_TO_DESKTOP)) continue;
            if (HasFlag(adapter.StateFlags, DISPLAY_DEVICE_MIRRORING_DRIVER)) continue;

            var mode = new DevMode
            {
                dmDeviceName = string.Empty,
                dmFormName = string.Empty,
                dmSize = (ushort)Marshal.SizeOf<DevMode>()
            };
            if (!EnumDisplaySettingsEx(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref mode, 0))
                continue;

            var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            _ = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0);

            var displayName = FirstNonEmpty(monitor.DeviceString, adapter.DeviceString, adapter.DeviceName);
            var idText = string.Join(' ', new[]
            {
                adapter.DeviceName, adapter.DeviceString, adapter.DeviceID, adapter.DeviceKey,
                monitor.DeviceName, monitor.DeviceString, monitor.DeviceID, monitor.DeviceKey
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            displays.Add(new DisplayEntry(
                adapter.DeviceName,
                displayName,
                HasFlag(adapter.StateFlags, DISPLAY_DEVICE_PRIMARY_DEVICE),
                ContainsVddHint(idText),
                mode.dmPositionX,
                mode.dmPositionY,
                mode));
        }

        return displays;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value;
        return string.Empty;
    }

    private static bool ContainsVddHint(string value)
    {
        var text = value.ToLowerInvariant();
        return text.Contains("mttvdd") ||
               text.Contains("virtual display driver") ||
               text.Contains("iddsampledriver") ||
               text.Contains("mikethetech") ||
               text.Contains("virtualdrivers") ||
               text.Contains("virtual display");
    }

    private static int GetDisplayNumber(string deviceName)
    {
        const string prefix = "\\\\.\\DISPLAY";
        if (!deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return int.MaxValue;

        return int.TryParse(deviceName[prefix.Length..], out var number) ? number : int.MaxValue;
    }

    private static bool HasFlag(uint value, uint flag) => (value & flag) == flag;

    private static string DescribeDisplayChangeResult(int result) => result switch
    {
        DISP_CHANGE_RESTART => "재부팅 필요",
        DISP_CHANGE_FAILED => "변경 실패",
        DISP_CHANGE_BADMODE => "지원하지 않는 모드",
        DISP_CHANGE_NOTUPDATED => "레지스트리 업데이트 실패",
        DISP_CHANGE_BADFLAGS => "잘못된 플래그",
        DISP_CHANGE_BADPARAM => "잘못된 매개변수",
        DISP_CHANGE_BADDUALVIEW => "DualView 상태 충돌",
        _ => "알 수 없는 오류"
    };

    private sealed record DisplayEntry(
        string DeviceName,
        string DisplayName,
        bool IsPrimary,
        bool IsVirtualDisplay,
        int X,
        int Y,
        DevMode Mode);

    private const int ENUM_CURRENT_SETTINGS = -1;

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
    private const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

    private const uint DM_POSITION = 0x00000020;

    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_SET_PRIMARY = 0x00000010;
    private const uint CDS_NORESET = 0x10000000;

    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_RESTART = 1;
    private const int DISP_CHANGE_FAILED = -1;
    private const int DISP_CHANGE_BADMODE = -2;
    private const int DISP_CHANGE_NOTUPDATED = -3;
    private const int DISP_CHANGE_BADFLAGS = -4;
    private const int DISP_CHANGE_BADPARAM = -5;
    private const int DISP_CHANGE_BADDUALVIEW = -6;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum,
        ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum,
        ref DevMode lpDevMode, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName,
        ref DevMode lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName,
        IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
}
