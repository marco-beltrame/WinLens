using System;
using System.Collections.Generic;
using System.Linq;

namespace WinLens.Services.Translation.Offline;

/// <summary>
/// Picks the source language of a line for the offline models, which (unlike Google)
/// cannot detect it themselves. CJK is read off the script. Latin text is matched against
/// the most common words of the installed languages, because the OCR tag only names the
/// recognizer that read the line (on an Italian PC that is Italian, whatever the text).
/// </summary>
internal static class SourceDetector
{
    // The most frequent short words of each language, few enough to stay unambiguous.
    private static readonly Dictionary<string, HashSet<string>> CommonWords = new()
    {
        ["en"] = Set("the and of to is in it you that for are with this on not be your have was can will from"),
        ["it"] = Set("il di che è la per un non una sono del della con le gli si ho questo al nel più anche"),
        ["fr"] = Set("le la les de des et est un une pour que qui dans pas vous sur au avec ce sont du"),
        ["es"] = Set("el la los las de que y es en un una por para con no se su al lo del está"),
        ["de"] = Set("der die das und ist nicht ein eine zu mit den sie es ich auf für von dem sich wird"),
        ["pt"] = Set("o a os as de que e é um uma para com não do da em no na se por"),
        ["nl"] = Set("de het een en van is dat niet op te voor met zijn je ik er wordt"),
        ["pl"] = Set("i w nie na się jest z że do to jak co ale po tak już"),
        ["sv"] = Set("och att det som en är på för med inte jag har av till den"),
        ["id"] = Set("yang dan di ini itu dengan untuk tidak dari ke ada akan dalam anda saya"),
        ["vi"] = Set("và của là có không được cho một những trong này với các đã"),
        ["tr"] = Set("ve bir bu da de için ile ne çok değil daha olarak gibi ama"),
    };

    private static HashSet<string> Set(string words) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    public static string Detect(string text, string? ocrTag, IReadOnlyCollection<string> installed)
    {
        var guess = MyMemoryProvider.GuessSource(text, ocrTag);
        if (guess is "zh" or "ja" or "ko") return guess;

        // Non-Latin scripts the OCR read with a matching recognizer (ru, ar, el…) keep its tag.
        if (!text.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
            return guess;

        var words = text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '«', '»', '¿', '¡'))
            .ToArray();

        string? best = null;
        int bestHits = 0;
        foreach (var lang in installed.Append(OfflinePacks.English).Distinct())
        {
            if (!CommonWords.TryGetValue(lang, out var common)) continue;
            int hits = words.Count(common.Contains);
            if (hits > bestHits) { best = lang; bestHits = hits; }
        }
        if (best != null) return best;

        // Too short to tell ("Settings", "Inventario"): the recognizer's language is the
        // best remaining hint when it is installed, English otherwise.
        return installed.Contains(guess) ? guess : OfflinePacks.English;
    }
}
