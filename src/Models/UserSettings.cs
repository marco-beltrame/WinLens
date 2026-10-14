using System.Windows.Input;
using WinLens.Native;
using WinLens.Services;
using WinLens.Services.Translation;

namespace WinLens.Models;

public sealed class UserSettings
{
    public string TargetLanguage { get; set; } = "en";

    /// <summary>OCR source language tag, or "auto" to try all installed recognizers.</summary>
    public string SourceLanguage { get; set; } = "auto";

    /// <summary>Most-recently-used target language codes, most recent first (experimental).</summary>
    public System.Collections.Generic.List<string> RecentLanguages { get; set; } = new();

    public HotkeyModifiers HotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    public Key HotkeyKey { get; set; } = Key.T;

    /// <summary>
    /// Shortcut for region (drag-to-select) capture. Default Ctrl+Shift+R — no Alt, to
    /// avoid the common NVIDIA / overlay-tool bindings.
    /// </summary>
    public HotkeyModifiers RegionHotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Shift;
    public Key RegionHotkeyKey { get; set; } = Key.R;

    /// <summary>
    /// "google" (default, no setup), "libretranslate" or "local-llm". A string rather than
    /// an enum so a value from a newer version falls back to Google instead of making the
    /// whole file unreadable.
    /// </summary>
    public string TranslationEngine { get; set; } = EngineNames.Google;

    public string LibreTranslateUrl { get; set; } = "";
    /// <summary>API key encrypted for the current Windows user (DPAPI), base64.</summary>
    public string? LibreTranslateKeyProtected { get; set; }

    public string LlmUrl { get; set; } = "http://localhost:11434/v1";
    /// <summary>Empty = the first model the server lists.</summary>
    public string LlmModel { get; set; } = "";
    /// <summary>API key encrypted for the current Windows user (DPAPI), base64.</summary>
    public string? LlmKeyProtected { get; set; }

    /// <summary>Use Google for lines the chosen server couldn't translate. Off keeps text on the user's server.</summary>
    public bool EngineFallbackToGoogle { get; set; }

    /// <summary>The engine settings in the form TranslationService takes, keys decrypted.</summary>
    public EngineOptions ToEngineOptions()
    {
        var engine = EngineNames.Parse(TranslationEngine);
        return engine switch
        {
            Services.Translation.TranslationEngine.LibreTranslate => new EngineOptions(engine,
                LibreTranslateUrl, SecretStore.Unprotect(LibreTranslateKeyProtected), null, EngineFallbackToGoogle),
            Services.Translation.TranslationEngine.LocalLlm => new EngineOptions(engine,
                LlmUrl, SecretStore.Unprotect(LlmKeyProtected), LlmModel, EngineFallbackToGoogle),
            _ => EngineOptions.Default,
        };
    }

    /// <summary>
    /// Replaces null/unknown values (hand-edited or damaged file) with defaults, and
    /// shortcuts WinLens would no longer accept (e.g. a bare Shift+A saved by an older
    /// version) with the default pair.
    /// </summary>
    /// <returns>True if a shortcut had to be reset.</returns>
    public bool Sanitize()
    {
        TargetLanguage = SettingsRules.NormalizeTarget(TargetLanguage);
        SourceLanguage = SettingsRules.NormalizeSource(SourceLanguage);
        RecentLanguages = SettingsRules.NormalizeRecent(RecentLanguages, 4);
        TranslationEngine = EngineNames.Normalize(TranslationEngine);
        LibreTranslateUrl = LibreTranslateUrl?.Trim() ?? "";
        LlmUrl = string.IsNullOrWhiteSpace(LlmUrl) ? "http://localhost:11434/v1" : LlmUrl.Trim();
        LlmModel = LlmModel?.Trim() ?? "";

        bool reset = false;
        if (!SettingsRules.IsHotkeyUsable((uint)(HotkeyModifiers & ~HotkeyModifiers.NoRepeat), HotkeyKey.ToString()))
        {
            HotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;
            HotkeyKey = Key.T;
            reset = true;
        }
        if (!SettingsRules.IsHotkeyUsable((uint)(RegionHotkeyModifiers & ~HotkeyModifiers.NoRepeat), RegionHotkeyKey.ToString())
            || (RegionHotkeyModifiers, RegionHotkeyKey) == (HotkeyModifiers, HotkeyKey))
        {
            RegionHotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift;
            RegionHotkeyKey = Key.R;
            reset = true;
        }
        return reset;
    }
}
