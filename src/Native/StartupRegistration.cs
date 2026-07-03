using System;
using Microsoft.Win32;

namespace WinLens.Native;

/// <summary>
/// Toggles "launch at Windows startup" via the per-user Run registry key.
/// All access is best-effort; failures are swallowed (e.g. restricted machines).
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinLens";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch { /* access denied — ignore */ }
    }

    /// <summary>
    /// If autostart is enabled but the stored command points at a stale location
    /// (app moved or updated), rewrite it to the currently running executable.
    /// </summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key?.GetValue(ValueName) is not string existing) return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var expected = $"\"{exe}\"";
            if (!string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, expected);
        }
        catch { /* best-effort */ }
    }
}
