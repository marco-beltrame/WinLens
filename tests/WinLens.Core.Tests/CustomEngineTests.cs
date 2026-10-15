using System.Net;
using System.Text;
using System.Text.Json;
using WinLens.Services;
using WinLens.Services.Translation;

namespace WinLens.Core.Tests;

public class CustomEngineTests
{
    private sealed class Router : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _respond;
        public List<(string Path, string? Body)> Calls { get; } = new();
        public Router(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((request.RequestUri!.Host + request.RequestUri.AbsolutePath, body));
            return _respond(request, body);
        }
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static TranslationService Make(Router r, EngineOptions options)
    {
        var svc = new TranslationService(r, Path.Combine(Path.GetTempPath(), "winlens-tests.log"));
        svc.Configure(options);
        return svc;
    }

    private static TranslationRequest[] Lines(params string[] t) => t.Select(x => new TranslationRequest(x, null)).ToArray();

    private static readonly object LibreLanguages = new[]
    {
        new { code = "en", name = "English" },
        new { code = "it", name = "Italian" },
        new { code = "zh-Hans", name = "Chinese" },
        new { code = "zh-Hant", name = "Chinese (traditional)" },
    };

    // ---------------- LibreTranslate ----------------

    [Fact]
    public async Task Libre_SendsTheBatchAsAnArray()
    {
        var r = new Router((req, body) => req.RequestUri!.AbsolutePath.EndsWith("/languages")
            ? Json(LibreLanguages)
            : Json(new { translatedText = JsonDocument.Parse(body!).RootElement.GetProperty("q").EnumerateArray().Select(e => "it:" + e.GetString()).ToArray() }));
        using var svc = Make(r, new EngineOptions(TranslationEngine.LibreTranslate, "localhost:5000", "k"));

        var result = await svc.TranslateManyAsync(Lines("one", "two"), "it");

        Assert.Equal(new[] { "it:one", "it:two" }, result);
        var call = Assert.Single(r.Calls, c => c.Path.EndsWith("/translate"));
        var sent = JsonDocument.Parse(call.Body!).RootElement;
        Assert.Equal("it", sent.GetProperty("target").GetString());
        Assert.Equal("k", sent.GetProperty("api_key").GetString());
        Assert.Equal(2, sent.GetProperty("q").GetArrayLength());
    }

    [Theory]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("it", "it")]
    [InlineData("ja", null)]
    public void Libre_MapsOurCodesOntoTheServers(string ours, string? expected)
    {
        var codes = new HashSet<string>(new[] { "en", "it", "zh-Hans", "zh-Hant" }, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected, LibreTranslateProvider.MapCode(ours, codes));
    }

    [Fact]
    public void Libre_MapsChineseOnOlderServers()
    {
        var codes = new HashSet<string>(new[] { "zh", "zt" }, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("zt", LibreTranslateProvider.MapCode("zh-TW", codes));
        Assert.Equal("zh", LibreTranslateProvider.MapCode("zh", codes));
    }

    [Fact]
    public async Task Libre_ServerRefusingArrays_IsAskedLineByLine()
    {
        var r = new Router((req, body) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/languages")) return Json(LibreLanguages);
            var q = JsonDocument.Parse(body!).RootElement.GetProperty("q");
            return q.ValueKind == JsonValueKind.Array
                ? Json(new { error = "Invalid request" }, HttpStatusCode.BadRequest)
                : Json(new { translatedText = "it:" + q.GetString() });
        });
        using var svc = Make(r, new EngineOptions(TranslationEngine.LibreTranslate, "http://localhost:5000"));
        Assert.Equal(new[] { "it:one", "it:two" }, await svc.TranslateManyAsync(Lines("one", "two"), "it"));
    }

    [Fact]
    public async Task Libre_Down_WithoutFallback_LeavesTextAndNeverCallsGoogle()
    {
        var r = new Router((_, _) => throw new HttpRequestException("refused"));
        using var svc = Make(r, new EngineOptions(TranslationEngine.LibreTranslate, "http://localhost:5000"));
        Assert.Equal(new[] { "one", "two" }, await svc.TranslateManyAsync(Lines("one", "two"), "it"));
        Assert.DoesNotContain(r.Calls, c => c.Path.Contains("google") || c.Path.Contains("mymemory"));
        Assert.Single(r.Calls, c => c.Path.EndsWith("/translate")); // not retried line by line
    }

    [Fact]
    public async Task Libre_Down_WithFallback_UsesGoogle()
    {
        var r = new Router((req, _) => req.RequestUri!.Host.Contains("google")
            ? Json("""[[["UNO\n","one\n",null,null,10],["DUE","two",null,null,10]],null,"en"]""")
            : throw new HttpRequestException("refused"));
        using var svc = Make(r, new EngineOptions(TranslationEngine.LibreTranslate, "http://localhost:5000", FallbackToGoogle: true));
        Assert.Equal(new[] { "UNO", "DUE" }, await svc.TranslateManyAsync(Lines("one", "two"), "it"));
    }

    // ---------------- Local model ----------------

    private static HttpResponseMessage Chat(string content) =>
        Json(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1")]
    [InlineData("localhost:1234/v1/", "http://localhost:1234/v1")]
    [InlineData("http://localhost:8080/v1/chat/completions", "http://localhost:8080/v1")]
    [InlineData("https://example.com/openai/v1", "https://example.com/openai/v1")]
    public void Llm_AcceptsTheUrlsPeoplePaste(string input, string expected) =>
        Assert.Equal(expected, OpenAiCompatibleProvider.ApiBase(input)!.ToString().TrimEnd('/'));

    [Theory]
    [InlineData("""["uno","due"]""")]
    [InlineData("```json\n[\"uno\", \"due\"]\n```")]
    [InlineData("<think>The user wants Italian.\n[\"x\"]</think>\n[\"uno\",\"due\"]")]
    [InlineData("Here you go: [\"uno\",\"due\"]")]
    public void Llm_ParsesTheArrayOutOfTheReply(string reply) =>
        Assert.Equal(new[] { "uno", "due" }, OpenAiCompatibleProvider.ParseReply(reply, 2));

    [Fact]
    public void Llm_WrongCount_IsRejected() =>
        Assert.Null(OpenAiCompatibleProvider.ParseReply("""["uno due"]""", 2));

    [Fact]
    public void Llm_SingleLine_AcceptsAPlainAnswer() =>
        Assert.Equal(new[] { "Ciao" }, OpenAiCompatibleProvider.ParseReply("\"Ciao\"", 1));

    [Fact]
    public async Task Llm_PicksTheFirstModel_AndTranslatesTheBatch()
    {
        var r = new Router((req, body) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/models"))
                return Json(new { data = new[] { new { id = "qwen2.5:7b" }, new { id = "other" } } });
            var root = JsonDocument.Parse(body!).RootElement;
            Assert.Equal("qwen2.5:7b", root.GetProperty("model").GetString());
            Assert.Contains("Italian", root.GetProperty("messages")[0].GetProperty("content").GetString());
            var lines = JsonSerializer.Deserialize<string[]>(root.GetProperty("messages")[1].GetProperty("content").GetString()!)!;
            return Chat(JsonSerializer.Serialize(lines.Select(l => "it:" + l)));
        });
        using var svc = Make(r, new EngineOptions(TranslationEngine.LocalLlm, "http://localhost:11434"));
        Assert.Equal(new[] { "it:one", "it:two" }, await svc.TranslateManyAsync(Lines("one", "two"), "it"));
        Assert.Contains(r.Calls, c => c.Path == "localhost/v1/chat/completions");
    }

    [Fact]
    public async Task Llm_LosingCount_IsRetriedLineByLine()
    {
        var r = new Router((req, body) =>
        {
            var root = JsonDocument.Parse(body!).RootElement;
            var lines = JsonSerializer.Deserialize<string[]>(root.GetProperty("messages")[1].GetProperty("content").GetString()!)!;
            return Chat(lines.Length > 1 ? """["merged"]""" : JsonSerializer.Serialize(new[] { "it:" + lines[0] }));
        });
        using var svc = Make(r, new EngineOptions(TranslationEngine.LocalLlm, "http://localhost:11434", Model: "m"));
        Assert.Equal(new[] { "it:one", "it:two" }, await svc.TranslateManyAsync(Lines("one", "two"), "it"));
        Assert.Equal(3, r.Calls.Count);
    }

    [Fact]
    public async Task Test_ReportsTheSampleAndTheModel()
    {
        var r = new Router((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/models")
            ? Json(new { data = new[] { new { id = "llama3.2" } } })
            : Chat("""["Ciao, come stai?"]"""));
        using var svc = new TranslationService(r, Path.Combine(Path.GetTempPath(), "winlens-tests.log"));
        var result = await svc.TestAsync(new EngineOptions(TranslationEngine.LocalLlm, "localhost:11434"), "it");
        Assert.True(result.Ok);
        Assert.Equal("Ciao, come stai?", result.Sample);
        Assert.Equal("llama3.2", result.Model);
    }

    [Fact]
    public async Task Test_WithoutUrl_SaysWhatToEnter()
    {
        var r = new Router((_, _) => throw new InvalidOperationException("no request expected"));
        using var svc = new TranslationService(r, Path.Combine(Path.GetTempPath(), "winlens-tests.log"));
        var result = await svc.TestAsync(new EngineOptions(TranslationEngine.LibreTranslate, " "), "it");
        Assert.False(result.Ok);
        Assert.Contains("server address", result.Message);
    }
}

public class EngineNamesTests
{
    [Theory]
    [InlineData(null, "google")]
    [InlineData("", "google")]
    [InlineData("deepl", "google")]
    [InlineData(" LibreTranslate ", "libretranslate")]
    [InlineData("local-llm", "local-llm")]
    public void UnknownOrMissing_FallsBackToGoogle(string? stored, string expected) =>
        Assert.Equal(expected, EngineNames.Normalize(stored));

    [Fact]
    public void NamesRoundTrip()
    {
        foreach (var e in Enum.GetValues<TranslationEngine>())
            Assert.Equal(e, EngineNames.Parse(EngineNames.ToName(e)));
    }
}
