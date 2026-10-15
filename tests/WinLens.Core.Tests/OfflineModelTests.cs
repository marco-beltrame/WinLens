using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using WinLens.Services;
using WinLens.Services.Translation;
using WinLens.Services.Translation.Offline;

namespace WinLens.Core.Tests;

/// <summary>
/// Runs the real packs, in the offline-models workflow only (it sets the two variables):
/// WinLens's tokenizer must give the ids the Python tokenizer gave when the packs were
/// built, and its decoder loop the translations tools/offline-models/reference.py decoded
/// on this same machine.
/// </summary>
public class OfflineModelTests
{
    private static readonly string? Root = Environment.GetEnvironmentVariable("WINLENS_OFFLINE_ROOT");
    private static readonly string? FixturesPath = Environment.GetEnvironmentVariable("WINLENS_OFFLINE_FIXTURES");

    private sealed record Fixture(string Text, int[] Ids, int[] OutIds);

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

        var all = Fixtures();
        Assert.NotEmpty(all);
        var failures = new List<string>();
        foreach (var (pair, fixtures) in all)
        {
            using var model = new MarianModel(packs.ModelDir(pair), options);
            foreach (var f in fixtures)
            {
                var ids = model.Encode(f.Text);
                if (!ids.SequenceEqual(f.Ids))
                    failures.Add($"{pair} ids for \"{f.Text}\": [{string.Join(',', ids)}] != [{string.Join(',', f.Ids)}]");
                // One line at a time, like the reference: int8 activations are quantized
                // per batch, so a line's neighbours can nudge a word or two.
                var output = model.Translate(new[] { f.Text }, default)[0];
                var expected = model.Decode(f.OutIds);
                Console.WriteLine($"{pair}: {f.Text} -> {output}");
                if (output != expected)
                    failures.Add($"{pair} \"{f.Text}\": \"{output}\" != \"{expected}\"");
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
        Assert.Contains("zh", langs);
        Assert.Contains("it", langs);

        using var svc = new TranslationService(null, Path.Combine(Path.GetTempPath(), "winlens-tests.log"), Root);
        svc.Configure(new EngineOptions(TranslationEngine.Offline));
        var lines = new[] { "服务器暂时不可用，请稍后再试。", "设置", "Il server non è disponibile." };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await svc.TranslateManyAsync(lines.Select(l => new TranslationRequest(l, "zh-Hans-CN")).ToArray(), "it");
        Console.WriteLine($"zh/it -> it in {sw.ElapsedMilliseconds} ms (models loading): {string.Join(" | ", result)}");

        // A screenful with the models loaded: what the user waits for on each capture.
        var screen = new[]
        {
            "关闭前是否保存更改？", "您的下载即将开始。", "点击这里重置密码。", "按任意键继续", "背包已满。",
            "您有3条未读消息。", "该物品无法出售给商人。", "订单满50元包邮", "服务条款和隐私政策",
            "更新失败，因为该文件正被另一个程序使用。", "他拔出剑，走进了黑暗之中。", "电量不足，请连接充电器。",
            "确定要永久删除此文件吗？", "会议已改到周四下午三点。", "订阅后，新剧集上线时会通知你。", "加载中，请稍候……",
        };
        sw.Restart();
        var screenResult = await svc.TranslateManyAsync(screen.Select(l => new TranslationRequest(l, "zh-Hans-CN")).ToArray(), "it");
        Console.WriteLine($"{screen.Length} lines zh -> it (via en) in {sw.ElapsedMilliseconds} ms");
        for (int i = 0; i < screen.Length; i++) Console.WriteLine($"  {screen[i]} -> {screenResult[i]}");

        Assert.NotEqual(lines[0], result[0]);
        Assert.DoesNotContain(result[0], c => c >= 0x4E00 && c <= 0x9FFF);
        Assert.Equal(lines[2], result[2]); // already Italian
    }
}
