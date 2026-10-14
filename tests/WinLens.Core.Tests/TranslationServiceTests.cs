using System.Net;
using System.Text;
using WinLens.Services;

namespace WinLens.Core.Tests;

public class TranslationServiceTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }

        public int Count(string host) => Requests.Count(r => r.RequestUri!.Host.Contains(host));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string GoogleOk = """[[["Hello ","你好",null,null,10],["world","世界",null,null,10]],null,"zh-CN"]""";

    private static TranslationService Make(FakeHandler h) =>
        new(h, Path.Combine(Path.GetTempPath(), "winlens-tests.log"));

    private static bool IsGoogle(HttpRequestMessage r) => r.RequestUri!.Host.Contains("google");

    [Fact]
    public async Task Google_MultiSegment_IsConcatenated_AndUsesGet()
    {
        var h = new FakeHandler(_ => Json(GoogleOk));
        using var svc = Make(h);
        Assert.Equal("Hello world", await svc.TranslateAsync("你好世界", "en"));
        var req = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Contains("client=gtx&sl=auto&tl=en&dt=t&q=", req.RequestUri!.Query);
    }

    [Fact]
    public async Task CacheHit_AvoidsSecondRequest()
    {
        var h = new FakeHandler(_ => Json(GoogleOk));
        using var svc = Make(h);
        await svc.TranslateAsync("你好世界", "en");
        await svc.TranslateAsync("你好世界", "en");
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Google429_FallsBack_AndBreakerSkipsGoogleNextCall()
    {
        var h = new FakeHandler(r => IsGoogle(r)
            ? Json("", HttpStatusCode.TooManyRequests)
            : Json("""{"responseStatus":200,"responseData":{"translatedText":"Hi"}}"""));
        using var svc = Make(h);
        Assert.Equal("Hi", await svc.TranslateAsync("你好", "en"));
        Assert.Equal("Hi", await svc.TranslateAsync("再见", "en"));
        Assert.Equal(1, h.Count("google"));
        Assert.Equal(2, h.Count("mymemory"));
    }

    [Fact]
    public async Task MyMemoryQuotaWarning_ReturnsOriginal_AndIsNotCached()
    {
        var h = new FakeHandler(r => IsGoogle(r)
            ? Json("", HttpStatusCode.InternalServerError)
            : Json("""{"responseStatus":429,"responseData":{"translatedText":"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS"}}"""));
        using var svc = Make(h);
        Assert.Equal("你好", await svc.TranslateAsync("你好", "en"));
        Assert.Equal("你好", await svc.TranslateAsync("你好", "en"));
        Assert.Equal(2, h.Count("mymemory"));
    }

    [Fact]
    public async Task MyMemoryWarningText_WithStatus200_IsRejected()
    {
        var h = new FakeHandler(r => IsGoogle(r)
            ? Json("", HttpStatusCode.InternalServerError)
            : Json("""{"responseStatus":200,"responseData":{"translatedText":"QUERY LENGTH LIMIT EXCEEDED. MAX ALLOWED QUERY : 500 CHARS"}}"""));
        using var svc = Make(h);
        Assert.Equal("你好", await svc.TranslateAsync("你好", "en"));
    }

    [Fact]
    public async Task MyMemoryStatusAsString200_IsAccepted()
    {
        var h = new FakeHandler(r => IsGoogle(r)
            ? Json("", HttpStatusCode.InternalServerError)
            : Json("""{"responseStatus":"200","responseData":{"translatedText":"Hi"}}"""));
        using var svc = Make(h);
        Assert.Equal("Hi", await svc.TranslateAsync("你好", "en"));
    }

    [Fact]
    public async Task TextOver500Chars_SkipsMyMemory()
    {
        var h = new FakeHandler(r => Json("", HttpStatusCode.InternalServerError));
        using var svc = Make(h);
        var text = new string('a', 501);
        Assert.Equal(text, await svc.TranslateAsync(text, "it"));
        Assert.Equal(0, h.Count("mymemory"));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var h = new FakeHandler(_ => Json(GoogleOk));
        using var svc = Make(h);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.TranslateAsync("你好", "en", ct: cts.Token));
    }

    [Fact]
    public async Task LongText_UsesPost_WithQueryInBody()
    {
        var h = new FakeHandler(_ => Json(GoogleOk));
        using var svc = Make(h);
        var text = new string('你', 1800);
        Assert.Equal("Hello world", await svc.TranslateAsync(text, "en"));
        var req = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.DoesNotContain("q=", req.RequestUri!.Query.Replace("dt=t", ""));
        Assert.Equal("application/x-www-form-urlencoded", req.Content!.Headers.ContentType!.MediaType);
    }
}
