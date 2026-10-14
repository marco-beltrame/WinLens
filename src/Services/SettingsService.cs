using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinLens.Models;

namespace WinLens.Services;

public sealed class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinLens");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public UserSettings Current { get; private set; } = new();

    /// <summary>True when settings were read from an existing file (i.e. not a first run).</summary>
    public bool LoadedFromDisk { get; private set; }

    /// <summary>True when invalid saved shortcuts were replaced by the defaults on load.</summary>
    public bool HotkeysWereReset { get; private set; }

    // Set when settings.json exists but could not be read (locked by an AV scanner or a
    // sync client). Saving then would overwrite the user's real settings with defaults.
    private bool _unreadable;

    public UserSettings Load()
    {
        bool loadedFromDisk = false;
        string? onDiskJson = null;

        if (File.Exists(FilePath))
        {
            onDiskJson = ReadWithRetry();
            if (onDiskJson == null)
            {
                // Could not read it: run on defaults, but this is NOT a first run and the
                // file must stay untouched.
                _unreadable = true;
                LoadedFromDisk = true;
                return Current;
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<UserSettings>(onDiskJson, Json);
                if (loaded != null)
                {
                    Current = loaded;
                    loadedFromDisk = true;
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"settings.json is corrupt: {ex.Message}");
            }

            if (!loadedFromDisk)
            {
                // Keep the unreadable file for inspection before it is replaced.
                try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); }
                catch (Exception ex) { Debug.WriteLine($"Could not back up settings.json: {ex.Message}"); }
            }
        }

        if (!loadedFromDisk)
        {
            // First run: pick the user's Windows display language as the default
            // target. Use the OS API instead of CultureInfo because the .NET
            // CLI / hosting can override the process culture to en-US.
            Current.TargetLanguage = LocaleMapper.ToTargetCode(GetUserDisplayLanguage());
        }
        else
        {
            HotkeysWereReset = Current.Sanitize();

            // Drop properties left over from older versions, and materialise ones added
            // since (e.g. the region hotkey), so the file on disk always mirrors the
            // current model. Only rewrite when it actually differs — otherwise every
            // launch would touch the file for nothing.
            var canonical = JsonSerializer.Serialize(Current, Json);
            if (canonical != onDiskJson)
                WriteAtomic(canonical);
        }

        LoadedFromDisk = loadedFromDisk;
        return Current;
    }

    /// <summary>Reads settings.json, retrying briefly on sharing violations; null if it stays locked.</summary>
    private static string? ReadWithRetry()
    {
        const int attempts = 4;
        for (int i = 0; i < attempts; i++)
        {
            try { return File.ReadAllText(FilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"settings.json read failed (attempt {i + 1}): {ex.Message}");
                if (i < attempts - 1) Thread.Sleep(100 * (i + 1));
            }
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserPreferredUILanguages(
        uint dwFlags, out uint pulNumLanguages, char[]? pwszLanguagesBuffer, ref uint pcchLanguagesBuffer);

    private const uint MUI_LANGUAGE_NAME = 0x8;

    /// <summary>The Windows display (UI) language, e.g. "it-IT" — not the regional-format locale.</summary>
    private static string GetUserDisplayLanguage()
    {
        try
        {
            uint size = 0;
            if (GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, null, ref size) && size > 0)
            {
                var buf = new char[size];
                if (GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, buf, ref size))
                {
                    // NUL-separated list; the first entry is the preferred one.
                    var first = new string(buf).Split('\0')[0];
                    if (!string.IsNullOrEmpty(first))
                        return first;
                }
            }
        }
        catch { /* fallback below */ }

        try { return CultureInfo.CurrentUICulture.Name; }
        catch { return "en"; }
    }

    public void Save(UserSettings settings)
    {
        Current = settings;
        if (_unreadable)
        {
            // The file existed but was locked at startup. Only write once it can be read
            // again, so a transient lock never costs the user their saved settings.
            if (File.Exists(FilePath) && ReadWithRetry() == null) return;
            _unreadable = false;
        }
        WriteAtomic(JsonSerializer.Serialize(settings, Json));
    }

    /// <summary>
    /// Writes settings.json via a temp file + replace, so an interrupted write can never
    /// leave truncated JSON behind. That matters more than it looks: <see cref="Load"/>
    /// treats unparseable JSON as "first run", which would silently reset the user's
    /// language and shortcuts and pop the settings window on the next launch.
    /// Best-effort throughout — Save runs from UI event handlers and a locked or full
    /// disk must not take the app down; the in-memory settings stay applied either way.
    /// </summary>
    private static void WriteAtomic(string json)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, null);
            else
                File.Move(tmp, FilePath);
        }
        catch
        {
            // Clean up a half-written temp file so it can't accumulate.
            try { File.Delete(FilePath + ".tmp"); } catch { /* nothing more to do */ }
        }
    }
}
