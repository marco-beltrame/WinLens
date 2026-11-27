using System.Net;
using System.Text;
using WinLens.Services;
using WinLens.Services.Translation;

namespace WinLens.Core.Tests;

public class MicrosoftTranslatorTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // The client disposes the request content once sent, so keep the body now.
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) { Requests.Add(request); Bodies.Add(body); }
            ct.ThrowIfCancellationRequested();
            return _respond(request);
        }

        public int Count(string host) => Requests.Count(r => r.RequestUri!.Host.Contains(host));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static bool IsAuth(HttpRequestMessage r) => r.RequestUri!.Host == "edge.microsoft.com";
    private static bool IsTranslate(HttpRequestMessage r) => r.RequestUri!.Host.Contains("cognitive");

    private static string Jwt(long exp)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"none"}""")}.{B64($$"""{"exp":{{exp}}}""")}.sig";
    }

    private static string FreshJwt() => Jwt(DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());

    private static HttpResponseMessage Reply(params string[] texts) =>
        Json("[" + string.Join(",", texts.Select(t =>
            $$"""{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"{{t}}","to":"it"}]}""")) + "]");

    private static MicrosoftTranslatorProvider Make(FakeHandler h) =>
        new(new TranslationHttp(h, Path.Combine(Path.GetTempPath(), "winlens-tests.log")));

    private static TranslationRequest[] Lines(params string[] texts) =>
        texts.Select(t => new TranslationRequest(t, null)).ToArray();

    [Fact]
    public async Task Response_IsAlignedWithInput_AndMissingTextIsNull()
    {
        var h = new FakeHandler(r => IsAuth(r) ? Json(FreshJwt()) : Json("""
            [{"translations":[{"text":"Ciao","to":"it"}]},
             {"translations":[]},
             {"translations":[{"text":"","to":"it"}]},
             {"translations":[{"text":"Mondo","to":"it"}]}]
            """));
        var result = await Make(h).TranslateAsync(Lines("Hello", "a", "b", "World\nagain"), "it", default);
        Assert.Equal(new string?[] { "Ciao", null, null, "Mondo" }, result);

        var req = Assert.Single(h.Requests, IsTranslate);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("api-version=3.0&to=it", req.RequestUri!.Query.TrimStart('?'));
        Assert.StartsWith("Bearer ", req.Headers.Authorization!.ToString());
        var body = Assert.Single(h.Bodies, b => b.StartsWith('['));
        Assert.Contains("\"Text\":\"World\\nagain\"", body);
    }

    [Fact]
    public async Task WrongLengthResponse_ReturnsNulls()
    {
        var h = new FakeHandler(r => IsAuth(r) ? Json(FreshJwt()) : Reply("Ciao"));
        var result = await Make(h).TranslateAsync(Lines("a", "b"), "it", default);
        Assert.All(result, Assert.Null);
    }

    [Fact]
    public async Task Token_IsFetchedOnce_AndReusedAcrossCalls()
    {
        var h = new FakeHandler(r => IsAuth(r) ? Json(FreshJwt()) : Reply("Ciao"));
        var p = Make(h);
        await p.TranslateAsync(Lines("Hello"), "it", default);
        await p.TranslateAsync(Lines("Hi"), "it", default);
        Assert.Equal(1, h.Count("edge.microsoft"));
        Assert.Equal(2, h.Count("cognitive"));
    }

    [Fact]
    public async Task ExpiredToken_IsRefetched()
    {
        var h = new FakeHandler(r => IsAuth(r)
            ? Json(Jwt(DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds())) // inside the 1 min margin
            : Reply("Ciao"));
        var p = Make(h);
        await p.TranslateAsync(Lines("Hello"), "it", default);
        await p.TranslateAsync(Lines("Hi"), "it", default);
        Assert.Equal(2, h.Count("edge.microsoft"));
    }

    [Fact]
    public async Task Unauthorized_RefreshesTokenOnce_AndRetries()
    {
        int auths = 0;
        var h = new FakeHandler(r =>
        {
            if (IsAuth(r)) return Json(Interlocked.Increment(ref auths) == 1 ? FreshJwt() + "old" : FreshJwt() + "new");
            return r.Headers.Authorization!.Parameter!.EndsWith("old")
                ? Json("", HttpStatusCode.Unauthorized)
                : Reply("Ciao");
        });
        var result = await Make(h).TranslateAsync(Lines("Hello"), "it", default);
        Assert.Equal("Ciao", result[0]);
        Assert.Equal(2, h.Count("edge.microsoft"));
        Assert.Equal(2, h.Count("cognitive"));
    }

    [Fact]
    public async Task PersistentUnauthorized_RetriesOnlyOnce()
    {
        var h = new FakeHandler(r => IsAuth(r) ? Json(FreshJwt()) : Json("", HttpStatusCode.Unauthorized));
        var result = await Make(h).TranslateAsync(Lines("Hello"), "it", default);
        Assert.Null(result[0]);
        Assert.Equal(2, h.Count("cognitive"));
    }

    [Fact]
    public async Task Http429_TripsBreaker()
    {
        var h = new FakeHandler(r => IsAuth(r) ? Json(FreshJwt()) : Json("", HttpStatusCode.TooManyRequests));
        var p = Make(h);
        Assert.Null((await p.TranslateAsync(Lines("Hello"), "it", default))[0]);
        Assert.Null((await p.TranslateAsync(Lines("Hi"), "it", default))[0]);
        Assert.Equal(1, h.Count("cognitive"));
    }

    [Fact]
    public async Task Batches_SplitAtHundredItems()
    {
        var h = new FakeHandler(r => IsAuth(r) ? Json(FreshJwt()) : Reply(Enumerable.Repeat("x", 100).ToArray()));
        var result = await Make(h).TranslateAsync(Lines(Enumerable.Range(0, 100).Select(i => $"l{i}").ToArray()), "it", default);
        Assert.Equal(1, h.Count("cognitive"));
        Assert.All(result, s => Assert.Equal("x", s));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var h = new FakeHandler(r => IsAuth(r) ? Json(FreshJwt()) : Reply("Ciao"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Make(h).TranslateAsync(Lines("Hello"), "it", cts.Token));
    }

    [Theory]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("iw", "he")]
    [InlineData("jw", "jv")]
    [InlineData("tl", "fil")]
    [InlineData("no", "nb")]
    [InlineData("sr", "sr-Cyrl")]
    [InlineData("mn", "mn-Cyrl")]
    [InlineData("ku", "kmr")]
    [InlineData("hmn", "mww")]
    [InlineData("ny", "nya")]
    [InlineData("it", "it")]
    [InlineData("en", "en")]
    public void MapTarget_MapsDifferingCodes(string code, string expected) =>
        Assert.Equal(expected, MicrosoftTranslatorProvider.MapTarget(code));

    [Fact]
    public void LifeOf_ReadsExp_AndFallsBackWhenUndecodable()
    {
        var now = DateTimeOffset.UtcNow;
        var life = MicrosoftTranslatorProvider.LifeOf(Jwt(now.AddMinutes(10).ToUnixTimeSeconds()), now);
        Assert.InRange(life.TotalSeconds, 539, 541);
        Assert.Equal(TimeSpan.Zero, MicrosoftTranslatorProvider.LifeOf(Jwt(now.AddSeconds(10).ToUnixTimeSeconds()), now));
        Assert.Equal(TimeSpan.FromMinutes(8), MicrosoftTranslatorProvider.LifeOf("not-a-jwt"));
    }

    [Fact]
    public async Task Chain_GoogleUnreachable_FallsToMicrosoft_AndGoogleIsSkippedNextTime()
    {
        var h = new FakeHandler(r =>
        {
            if (r.RequestUri!.Host.Contains("google")) throw new HttpRequestException("name not resolved");
            return IsAuth(r) ? Json(FreshJwt()) : Reply("Hi");
        });
        using var svc = new TranslationService(h, Path.Combine(Path.GetTempPath(), "winlens-tests.log"));
        Assert.Equal("Hi", await svc.TranslateAsync("你好", "en"));
        Assert.Equal("Hi", await svc.TranslateAsync("再见", "en"));
        Assert.Equal(1, h.Count("google"));
        Assert.Equal(2, h.Count("cognitive"));
        Assert.Equal(0, h.Count("mymemory"));
    }

    [Fact]
    public async Task Test_DefaultEngine_FallsBackToMicrosoft_AndReportsTotalFailure()
    {
        var h = new FakeHandler(r => r.RequestUri!.Host.Contains("google")
            ? throw new HttpRequestException("blocked")
            : IsAuth(r) ? Json(FreshJwt()) : Reply("Hi"));
        using var svc = new TranslationService(h, Path.Combine(Path.GetTempPath(), "winlens-tests.log"));
        var ok = await svc.TestAsync(EngineOptions.Default, "en");
        Assert.True(ok.Ok);
        Assert.Equal("Hi", ok.Sample);

        var down = new FakeHandler(_ => throw new HttpRequestException("blocked"));
        using var svc2 = new TranslationService(down, Path.Combine(Path.GetTempPath(), "winlens-tests.log"));
        var bad = await svc2.TestAsync(EngineOptions.Default, "en");
        Assert.False(bad.Ok);
        Assert.DoesNotContain("Google", bad.Message);
    }
}
