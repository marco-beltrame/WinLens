using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WinLens.Theme;

/// <summary>
/// Keeps the app's colours in step with the Windows "app mode" (light or dark). The
/// palette is the first dictionary merged in App.xaml; swapping it re-resolves every
/// DynamicResource, so open windows change with the system instead of at the next launch.
/// </summary>
internal static class ThemeManager
{
    private const int WM_SETTINGCHANGE = 0x001A;

    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // App.xaml starts on the dark palette.
    private static bool _light;

    /// <summary>Loads the palette matching Windows, if it is not the one already in use.</summary>
    public static void Apply()
    {
        bool light = WindowsUsesLightApps();
        if (light == _light) return;

        var app = Application.Current;
        if (app == null) return;

        try
        {
            var palette = new ResourceDictionary
            {
                Source = new Uri(
                    $"/WinLens;component/src/Theme/Palette.{(light ? "Light" : "Dark")}.xaml",
                    UriKind.Relative),
            };
            app.Resources.MergedDictionaries[0] = palette;
            _light = light;
        }
        catch
        {
            // Keep the palette already loaded: the wrong colours beat no window at all.
        }
    }

    /// <summary>
    /// Re-applies the palette whenever Windows switches mode. <paramref name="host"/> only
    /// has to be a top-level window that lives as long as the app: the notification is a
    /// broadcast, which hidden windows receive too.
    /// </summary>
    public static void Follow(Window host)
    {
        var hwnd = new WindowInteropHelper(host).Handle;
        if (hwnd != IntPtr.Zero)
            HwndSource.FromHwnd(hwnd)?.AddHook(OnMessage);
    }

    private static IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero)
        {
            try
            {
                if (Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                    Apply();
            }
            catch { /* a setting change must never take the app down */ }
        }
        return IntPtr.Zero;
    }

    /// <summary>Light unless the user chose dark; a missing value means light, as in Windows itself.</summary>
    private static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }
}
