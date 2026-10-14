using System;
using System.IO;
using Microsoft.Win32;

namespace WinLens.Native;

/// <summary>
/// Toggles "launch at Windows startup" via the per-user Run registry key.
/// All access is best-effort; failures are reported through return values, never thrown
/// (e.g. restricted machines).
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager's Startup tab records its enable/disable switch here, separate from Run.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "WinLens";

    /// <summary>True when the Run entry exists and the user hasn't disabled it in Task Manager.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            if (key?.GetValue(ValueName) == null) return false;
            return !IsDisabledByUser();
        }
        catch { return false; }
    }

    // First byte of the StartupApproved value: even (02/06) = enabled, odd (03/07) = disabled.
    private static bool IsDisabledByUser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, false);
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
        }
        catch { return false; }
    }

    /// <returns>True when the registry now matches <paramref name="enabled"/>.</returns>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return false;

            if (enabled)
            {
                // Keep a working existing entry as-is; only (re)write a missing or dead one.
                if (!(key.GetValue(ValueName) is string existing && CommandExists(existing)))
                {
                    var exe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exe)) return false;
                    key.SetValue(ValueName, $"\"{exe}\"");
                }

                // Clear a Task Manager "Disabled" flag, otherwise Windows ignores the Run entry.
                if (IsDisabledByUser())
                {
                    using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                    approved?.DeleteValue(ValueName, false);
                }
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch { return false; /* access denied */ }

        return IsEnabled() == enabled;
    }

    /// <summary>
    /// If autostart is enabled but the stored command points at a location that no longer
    /// exists (app moved or updated), rewrite it to the currently running executable.
    /// A working entry is left alone.
    /// </summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key?.GetValue(ValueName) is not string existing) return;
            if (CommandExists(existing)) return;

            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(ValueName, $"\"{exe}\"");
        }
        catch { /* best-effort */ }
    }

    // The stored value is either "C:\path\app.exe" (quoted, maybe with args) or a bare path.
    private static bool CommandExists(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return false;
        try
        {
            if (command[0] == '"')
            {
                int end = command.IndexOf('"', 1);
                return end > 1 && File.Exists(command.Substring(1, end - 1));
            }
            if (File.Exists(command)) return true;
            int space = command.IndexOf(' ');
            return space > 0 && File.Exists(command.Substring(0, space));
        }
        catch { return false; }
    }
}
