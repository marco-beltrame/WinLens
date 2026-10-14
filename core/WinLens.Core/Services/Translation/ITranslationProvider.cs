using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinLens.Services.Translation;

/// <summary>One line of text to translate, with the OCR engine's language tag as a hint.</summary>
public readonly record struct TranslationRequest(string Text, string? SourceHint);

/// <summary>
/// A translation backend. <see cref="TranslationService"/> tries providers in order, each
/// one only for the lines the previous ones could not translate.
/// </summary>
public interface ITranslationProvider
{
    string Name { get; }

    /// <summary>
    /// Translates every item to <paramref name="target"/>. The result is aligned with
    /// <paramref name="items"/> and holds <c>null</c> where this provider failed. Failures
    /// are reported that way, never thrown; only the caller's cancellation throws.
    /// </summary>
    Task<string?[]> TranslateAsync(IReadOnlyList<TranslationRequest> items, string target, CancellationToken ct);
}
