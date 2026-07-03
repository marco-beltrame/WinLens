using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WinLens.Native;

[Flags]
public enum HotkeyModifiers : uint
{
    None    = 0x0000,
    Alt     = 0x0001,
    Control = 0x0002,
    Shift   = 0x0004,
    Win     = 0x0008,
    NoRepeat = 0x4000,
}

/// <summary>
/// Registers a single system-wide hotkey using user32.RegisterHotKey and
/// raises <see cref="Pressed"/> on the WPF UI thread when triggered.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Window _host;
    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    public GlobalHotkey(Window host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public void Register(HotkeyModifiers modifiers, Key key)
    {
        Unregister();

        var helper = new WindowInteropHelper(_host);
        if (helper.Handle == IntPtr.Zero)
        {
            // Force-create the HWND for hidden windows.
            helper.EnsureHandle();
        }

        _source = HwndSource.FromHwnd(helper.Handle);
        if (_source == null)
            throw new InvalidOperationException("Could not obtain HwndSource for host window.");

        _source.AddHook(WndProc);

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        uint mods = (uint)modifiers | (uint)HotkeyModifiers.NoRepeat;

        if (!RegisterHotKey(helper.Handle, HotkeyId, mods, vk))
        {
            int err = Marshal.GetLastWin32Error();
            // Don't leave a dead hook on the shared HwndSource when registration fails.
            _source.RemoveHook(WndProc);
            _source = null;
            throw new InvalidOperationException(
                $"RegisterHotKey failed (Win32 error {err}). The combination may already be in use.");
        }
        _registered = true;
    }

    public void Unregister()
    {
        if (_source != null)
        {
            if (_registered)
            {
                var helper = new WindowInteropHelper(_host);
                UnregisterHotKey(helper.Handle, HotkeyId);
                _registered = false;
            }
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();
}
