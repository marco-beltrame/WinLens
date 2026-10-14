namespace WinLens.Services.Translation;

public enum TranslationEngine
{
    /// <summary>Google's free endpoint, MyMemory as fallback. No setup.</summary>
    Google,
    /// <summary>A LibreTranslate server (self-hosted or a hosted instance).</summary>
    LibreTranslate,
    /// <summary>Any OpenAI-compatible chat server: Ollama, LM Studio, llama.cpp, vLLM…</summary>
    LocalLlm,
}

/// <param name="Url">Server address; ignored for <see cref="TranslationEngine.Google"/>.</param>
/// <param name="ApiKey">Optional key, sent as LibreTranslate's api_key or as a Bearer token.</param>
/// <param name="Model">Model name for <see cref="TranslationEngine.LocalLlm"/>; empty picks the server's first model.</param>
/// <param name="FallbackToGoogle">
/// Use Google (then MyMemory) for lines the chosen engine could not translate. Off by
/// default: whoever picks their own server usually wants their text to stay on it.
/// </param>
public sealed record EngineOptions(
    TranslationEngine Engine,
    string? Url = null,
    string? ApiKey = null,
    string? Model = null,
    bool FallbackToGoogle = false)
{
    public static EngineOptions Default { get; } = new(TranslationEngine.Google);
}

/// <param name="Sample">The test phrase as translated, when it worked.</param>
/// <param name="Model">The model that answered (local models only).</param>
public sealed record EngineTestResult(bool Ok, string Message, string? Sample = null, string? Model = null);
