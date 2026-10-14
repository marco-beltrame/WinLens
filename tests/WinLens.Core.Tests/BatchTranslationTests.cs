using System.Net;
using System.Text;
using System.Text.Json;
using WinLens.Services;
using WinLens.Services.Translation;

namespace WinLens.Core.Tests;

public class BatchTranslationTests
{
    /// <summary>Fake Google: "translates" each line of q by upper-casing it, one segment per line.</summary>
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public List<string> Queries { get; } = new();
        public Func<string, string>? Override { get; set; }
        public HashSet<string> Untouched { get; } = new();
        public int MyMemoryCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.Host.Contains("google"))
            {
                Interlocked.Increment(ref MyMemoryCalls);
                var mmq = Uri.UnescapeDataString(Param(request.RequestUri.Query, "q")!);
                return Json(JsonSerializer.Serialize(new
                {
                    responseStatus = 200,
                    responseData = new { translatedText = "mm:" + mmq },
                }));
            }

            string q = request.Method == HttpMethod.Post
                ? Uri.UnescapeDataString(Param(await request.Content!.ReadAsStringAsync(ct), "q")!.Replace('+', ' '))
                : Uri.UnescapeDataString(Param(request.RequestUri.Query, "q")!);
            lock (Queries) Queries.Add(q);

            if (Override != null)
                return Json(Override(q));

            var lines = q.Split('\n');
            var segments = lines.Select((l, k) =>
            {
                var t = Untouched.Contains(l) ? l : l.ToUpperInvariant();
                return new object?[] { k < lines.Length - 1 ? t + "\n" : t, l, null, null, 10 };
            });
            return Json(JsonSerializer.Serialize(new object?[] { segments, null, "auto" }));
        }

        private static string? Param(string query, string name)
        {
            foreach (var part in query.TrimStart('?').Split('&'))
                if (part.StartsWith(name + "=")) return part[(name.Length + 1)..];
            return null;
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static TranslationService Make(FakeGoogle h) =>
        new(h, Path.Combine(Path.GetTempPath(), "winlens-tests.log"));

    private static TranslationRequest[] Lines(params string[] texts) =>
        texts.Select(t => new TranslationRequest(t, null)).ToArray();

    [Fact]
    public async Task WholeScreen_IsOneRequest_AndResultsStayAligned()
    {
        var h = new FakeGoogle();
        using var svc = Make(h);
        var result = await svc.TranslateManyAsync(Lines("one", "two", "three"), "it");
        Assert.Equal(new[] { "ONE", "TWO", "THREE" }, result);
        Assert.Equal("one\ntwo\nthree", Assert.Single(h.Queries));
    }

    [Fact]
    public async Task RepeatedAndBlankLines_AreSentOnce_AndFilledBack()
    {
        var h = new FakeGoogle();
        using var svc = Make(h);
        var result = await svc.TranslateManyAsync(Lines("ok", " ", "next", "ok"), "it");
        Assert.Equal(new[] { "OK", " ", "NEXT", "OK" }, result);
        Assert.Equal("ok\nnext", Assert.Single(h.Queries));
    }

    [Fact]
    public async Task CachedLines_AreNotSentAgain()
    {
        var h = new FakeGoogle();
        using var svc = Make(h);
        await svc.TranslateManyAsync(Lines("alpha", "beta"), "it");
        var result = await svc.TranslateManyAsync(Lines("alpha", "gamma"), "it");
        Assert.Equal(new[] { "ALPHA", "GAMMA" }, result);
        Assert.Equal("gamma", h.Queries[^1]);
    }

    [Fact]
    public async Task LineCountMismatch_FallsBackToOneRequestPerLine()
    {
        var h = new FakeGoogle();
        h.Override = q => q.Contains('\n')
            ? """[[["MERGED","x",null,null,10]],null,"en"]"""
            : JsonSerializer.Serialize(new object?[] { new[] { new object?[] { q.ToUpperInvariant(), q, null, null, 10 } }, null, "en" });
        using var svc = Make(h);
        var result = await svc.TranslateManyAsync(Lines("left", "right"), "it");
        Assert.Equal(new[] { "LEFT", "RIGHT" }, result);
        Assert.Equal(3, h.Queries.Count); // the batch, then each line
    }

    [Fact]
    public async Task LineLeftUntranslatedInBatch_IsRetriedOnItsOwn()
    {
        var h = new FakeGoogle();
        h.Untouched.Add("Settings"); // pretend the batch was detected as another language
        using var svc = Make(h);
        h.Override = q => q.Contains('\n')
            ? """[[["HALLO WELT\n","Hallo Welt\n",null,null,10],["Settings","Settings",null,null,10]],null,"de"]"""
            : """[[["IMPOSTAZIONI","Settings",null,null,10]],null,"en"]""";
        var result = await svc.TranslateManyAsync(Lines("Hallo Welt", "Settings"), "it");
        Assert.Equal(new[] { "HALLO WELT", "IMPOSTAZIONI" }, result);
        Assert.Equal(new[] { "Hallo Welt\nSettings", "Settings" }, h.Queries);
    }

    [Fact]
    public async Task ScreenAlreadyInTarget_IsNotRetriedLineByLine()
    {
        var h = new FakeGoogle();
        h.Untouched.UnionWith(new[] { "ciao", "mondo" });
        using var svc = Make(h);
        var result = await svc.TranslateManyAsync(Lines("ciao", "mondo"), "it");
        Assert.Equal(new[] { "ciao", "mondo" }, result);
        Assert.Single(h.Queries);
    }

    [Fact]
    public async Task LongScreens_AreSplitIntoSeveralBatches()
    {
        var h = new FakeGoogle();
        using var svc = Make(h);
        var lines = Enumerable.Range(0, 250).Select(i => $"line {i}").ToArray();
        var result = await svc.TranslateManyAsync(Lines(lines), "it");
        Assert.Equal(lines.Select(l => l.ToUpperInvariant()), result);
        Assert.Equal(3, h.Queries.Count); // at most 100 lines per request
    }

    [Fact]
    public async Task GoogleDown_FallsBackToMyMemoryPerLine()
    {
        var h = new FakeGoogle { Override = _ => "<html>blocked</html>" };
        using var svc = Make(h);
        var result = await svc.TranslateManyAsync(Lines("one", "two"), "it");
        Assert.Equal(new[] { "mm:one", "mm:two" }, result);
        Assert.Equal(2, h.MyMemoryCalls);
    }

    [Theory]
    [InlineData("こんにちは", null, "ja")]
    [InlineData("안녕하세요", null, "ko")]
    [InlineData("你好", null, "zh")]
    [InlineData("Bonjour", "fr-FR", "fr")]
    [InlineData("Hello", "zh-Hans", "en")]
    public void GuessSource_ReadsScriptFirst(string text, string? tag, string expected) =>
        Assert.Equal(expected, MyMemoryProvider.GuessSource(text, tag));
}
