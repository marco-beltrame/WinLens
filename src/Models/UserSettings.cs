using System.Windows.Input;
using WinLens.Native;

namespace WinLens.Models;

/// <summary>Which backend translates the captured text.</summary>
public enum TranslationProvider
{
    /// <summary>Built-in free engines: Google gtx with a MyMemory fallback (the default).</summary>
    BuiltIn,

    /// <summary>An OpenAI-compatible chat endpoint (OpenAI, DeepSeek, Ollama, etc.).</summary>
    OpenAiCompatible,
}

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
    /// Translation backend. Defaults to <see cref="TranslationProvider.BuiltIn"/> so
    /// existing installs keep the free Google/MyMemory behaviour untouched.
    /// </summary>
    public TranslationProvider TranslationProvider { get; set; } = TranslationProvider.BuiltIn;

    /// <summary>Base URL of the OpenAI-compatible API, e.g. https://api.openai.com/v1.</summary>
    public string LlmBaseUrl { get; set; } = "";

    /// <summary>Bearer token for the OpenAI-compatible API.</summary>
    public string LlmApiKey { get; set; } = "";

    /// <summary>Chat model id, e.g. gpt-4o-mini.</summary>
    public string LlmModel { get; set; } = "";
}
