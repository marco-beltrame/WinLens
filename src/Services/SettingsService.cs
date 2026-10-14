using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    public UserSettings Load()
    {
        bool loadedFromDisk = false;
        string? onDiskJson = null;
        try
        {
            if (File.Exists(FilePath))
            {
                onDiskJson = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<UserSettings>(onDiskJson, Json);
                if (loaded != null)
                {
                    Current = loaded;
                    loadedFromDisk = true;
                }
            }
        }
        catch
        {
            // Corrupt file — ignore and fall back to defaults.
        }

        if (!loadedFromDisk)
        {
            // First run: pick the user's Windows display language as the default
            // target. Use the OS API instead of CultureInfo because the .NET
            // CLI / hosting can override the process culture to en-US.
            var sys = MapToTargetCode(GetUserDisplayLanguage());
            if (!string.IsNullOrEmpty(sys))
                Current.TargetLanguage = sys;
        }
        else
        {
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultLocaleName(StringBuilder lpLocaleName, int cchLocaleName);

    private static string GetUserDisplayLanguage()
    {
        try
        {
            var sb = new StringBuilder(85); // LOCALE_NAME_MAX_LENGTH
            if (GetUserDefaultLocaleName(sb, sb.Capacity) > 0)
            {
                var name = sb.ToString();
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
        }
        catch { /* fallback below */ }

        try { return CultureInfo.InstalledUICulture.Name; }
        catch { return "en"; }
    }

    /// <summary>
    /// Map a full Windows locale name to a code that actually exists in
    /// <see cref="LanguageList"/>. Returning a code outside the list would leave the
    /// language combos with nothing to select (they'd fall back to the first entry) and
    /// lose script distinctions the backend needs (zh-Hant must become zh-TW, not zh).
    /// Unknown locales return null so the caller keeps the "en" default.
    /// </summary>
    private static string? MapToTargetCode(string locale)
    {
        if (string.IsNullOrEmpty(locale)) return null;

        if (locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            bool traditional =
                locale.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                locale.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                locale.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                locale.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
            return traditional ? "zh-TW" : "zh";
        }

        var baseTag = locale.Split('-')[0];
        foreach (var (code, _) in LanguageList.Items)
        {
            if (string.Equals(code, locale, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, baseTag, StringComparison.OrdinalIgnoreCase))
                return code;
        }
        return null;
    }

    public void Save(UserSettings settings)
    {
        Current = settings;
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
