using System;

namespace WinLens.Services.Translation;

/// <summary>How an engine is written in settings.json.</summary>
public static class EngineNames
{
    public const string Google = "google";
    public const string LibreTranslate = "libretranslate";
    public const string LocalLlm = "local-llm";
    public const string Offline = "offline";

    public static TranslationEngine Parse(string? name) => Normalize(name) switch
    {
        LibreTranslate => TranslationEngine.LibreTranslate,
        LocalLlm => TranslationEngine.LocalLlm,
        Offline => TranslationEngine.Offline,
        _ => TranslationEngine.Google,
    };

    public static string ToName(TranslationEngine engine) => engine switch
    {
        TranslationEngine.LibreTranslate => LibreTranslate,
        TranslationEngine.LocalLlm => LocalLlm,
        TranslationEngine.Offline => Offline,
        _ => Google,
    };

    /// <summary>A known name, or "google" for null/unknown values.</summary>
    public static string Normalize(string? name)
    {
        var n = name?.Trim();
        if (string.Equals(n, LibreTranslate, StringComparison.OrdinalIgnoreCase)) return LibreTranslate;
        if (string.Equals(n, LocalLlm, StringComparison.OrdinalIgnoreCase)) return LocalLlm;
        if (string.Equals(n, Offline, StringComparison.OrdinalIgnoreCase)) return Offline;
        return Google;
    }
}
