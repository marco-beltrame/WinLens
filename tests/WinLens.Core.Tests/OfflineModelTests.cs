using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using WinLens.Services;
using WinLens.Services.Translation;
using WinLens.Services.Translation.Offline;

namespace WinLens.Core.Tests;

/// <summary>
/// Runs the real packs, in the offline-models workflow only (it sets the two variables):
/// WinLens's tokenizer and decoder loop must give the same ids and the same translations
/// as the Python reference that built the packs.
/// </summary>
public class OfflineModelTests
{
    private static readonly string? Root = Environment.GetEnvironmentVariable("WINLENS_OFFLINE_ROOT");
    private static readonly string? FixturesPath = Environment.GetEnvironmentVariable("WINLENS_OFFLINE_FIXTURES");

    private sealed record Fixture(string Text, int[] Ids, string Output);

    private static Dictionary<string, Fixture[]> Fixtures() =>
        JsonSerializer.Deserialize<Dictionary<string, Fixture[]>>(File.ReadAllText(FixturesPath!),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void EveryPack_MatchesThePythonReference()
    {
        if (Root == null || FixturesPath == null) return;
        var packs = new OfflinePacks(Root);
        OnnxRuntimeLoader.Use(packs.RuntimeDir);
        using var options = new SessionOptions();

        var failures = new List<string>();
        foreach (var (pair, fixtures) in Fixtures())
        {
            using var model = new MarianModel(packs.ModelDir(pair), options);
            var outputs = model.Translate(fixtures.Select(f => f.Text).ToArray(), default);
            for (int i = 0; i < fixtures.Length; i++)
            {
                var f = fixtures[i];
                var ids = model.Encode(f.Text);
                if (!ids.SequenceEqual(f.Ids))
                    failures.Add($"{pair} ids for \"{f.Text}\": [{string.Join(',', ids)}] != [{string.Join(',', f.Ids)}]");
                Console.WriteLine($"{pair}: {f.Text} -> {outputs[i]}");
                if (outputs[i] != f.Output)
                    failures.Add($"{pair} \"{f.Text}\": \"{outputs[i]}\" != \"{f.Output}\"");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Pivot_TranslatesBetweenTwoNonEnglishLanguages()
    {
        if (Root == null) return;
        var packs = new OfflinePacks(Root);
        var langs = packs.InstalledLanguages();
        if (!langs.Contains("zh") || !langs.Contains("it")) return;

        using var svc = new TranslationService(null, Path.Combine(Path.GetTempPath(), "winlens-tests.log"), Root);
        svc.Configure(new EngineOptions(TranslationEngine.Offline));
        var lines = new[] { "服务器暂时不可用，请稍后再试。", "设置", "Il server non è disponibile." };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await svc.TranslateManyAsync(lines.Select(l => new TranslationRequest(l, "zh-Hans-CN")).ToArray(), "it");
        Console.WriteLine($"zh/it -> it in {sw.ElapsedMilliseconds} ms: {string.Join(" | ", result)}");

        Assert.NotEqual(lines[0], result[0]);
        Assert.DoesNotContain(result[0], c => c >= 0x4E00 && c <= 0x9FFF);
        Assert.Equal(lines[2], result[2]); // already Italian
    }
}
