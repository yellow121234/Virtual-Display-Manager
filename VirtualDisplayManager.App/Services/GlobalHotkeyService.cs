using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VirtualDisplayManager.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int F11HotkeyId = 0x5D11;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF11 = 0x7A;
    private const uint VkF12 = 0x7B;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly WindowInteropHelper _windowInteropHelper;
    private readonly Dispatcher _dispatcher;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private HwndSource? _source;
    private bool _f11Registered;
    private IntPtr _keyboardHook;
    private bool _f12Down;

    public event EventHandler? F11Pressed;
    public event EventHandler? F12Pressed;

    public GlobalHotkeyService(Window window)
    {
        _windowInteropHelper = new WindowInteropHelper(window);
        _dispatcher = window.Dispatcher;
        _keyboardProc = KeyboardHookProc;
    }

    public void RegisterF11AndF12()
    {
        var handle = _windowInteropHelper.Handle;
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("창 핸들이 아직 준비되지 않았습니다.");

        _source ??= HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        _f11Registered = RegisterHotKey(handle, F11HotkeyId, ModNoRepeat, VkF11);
        if (!_f11Registered)
            throw new InvalidOperationException("F11 전역 단축키 등록에 실패했습니다. 다른 프로그램이 이미 F11을 전역 단축키로 사용 중일 수 있습니다.");

        // F12는 Windows 디버거 예약 키이므로 RegisterHotKey 대신 F12만 확인하는 훅을 사용합니다.
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "F12 전역 키 감시를 시작하지 못했습니다.");
    }

    public void Dispose()
    {
        var handle = _windowInteropHelper.Handle;
        if (_f11Registered && handle != IntPtr.Zero)
            UnregisterHotKey(handle, F11HotkeyId);

        if (_keyboardHook != IntPtr.Zero)
            UnhookWindowsHookEx(_keyboardHook);

        if (_source is not null)
            _source.RemoveHook(WndProc);

        _f11Registered = false;
        _keyboardHook = IntPtr.Zero;
        _f12Down = false;
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == F11HotkeyId)
        {
            handled = true;
            F11Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (data.VirtualKeyCode == VkF12)
            {
                var message = wParam.ToInt32();
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    if (!_f12Down)
                    {
                        _f12Down = true;
                        _dispatcher.BeginInvoke(() => F12Pressed?.Invoke(this, EventArgs.Empty));
                    }
                }
                else if (message is WmKeyUp or WmSysKeyUp)
                {
                    _f12Down = false;
                }
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback,
        IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
