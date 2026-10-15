using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinLens.Services;
using WinLens.Services.Translation;
using WinLens.Services.Translation.Offline;

namespace WinLens.Core.Tests;

public class OfflineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winlens-offline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    // ---------------- Source detection ----------------

    [Theory]
    [InlineData("服务器暂时不可用", "en-US", "zh")]
    [InlineData("サーバーは一時的に利用できません", null, "ja")]
    [InlineData("서버를 사용할 수 없습니다", null, "ko")]
    [InlineData("Il server non è disponibile per il momento", "en-US", "it")]   // the recognizer tag is wrong
    [InlineData("The server is not available right now", "it-IT", "en")]
    [InlineData("Le serveur est indisponible pour le moment", "en-US", "fr")]
    public void Detect_ReadsTheTextNotJustTheRecognizer(string text, string? ocrTag, string expected)
    {
        var installed = new[] { "it", "fr", "zh", "ja", "ko" };
        Assert.Equal(expected, SourceDetector.Detect(text, ocrTag, installed));
    }

    [Fact]
    public void Detect_ShortLatinText_FallsBackToTheRecognizerWhenInstalled()
    {
        Assert.Equal("it", SourceDetector.Detect("Impostazioni", "it-IT", new[] { "it" }));
        // Not installed: English is the pivot, so the line can at least pass through.
        Assert.Equal("en", SourceDetector.Detect("Impostazioni", "it-IT", Array.Empty<string>()));
    }

    [Fact]
    public void Detect_IgnoresLanguagesThatAreNotInstalled()
    {
        Assert.Equal("en", SourceDetector.Detect("Der Server ist nicht verfügbar", "en-US", new[] { "it" }));
    }

    // ---------------- Codes and manifest ----------------

    [Theory]
    [InlineData("zh", "zh")]
    [InlineData("zh-TW", null)]
    [InlineData("iw", "he")]
    [InlineData("it", "it")]
    public void PackCode_MapsWinLensCodes(string target, string? expected) =>
        Assert.Equal(expected, OfflinePacks.PackCode(target));

    [Fact]
    public void Manifest_OffersEveryLanguageWithAModelToOrFromEnglish()
    {
        var f = new PackFile("x.zip", 1, "00");
        var m = new PackManifest(new(), new()
        {
            ["zh-en"] = f, ["en-zh"] = f,
            ["ja-en"] = f,             // read only
            ["it-en"] = f, ["en-it"] = f,
            ["de-fr"] = f,             // not through English: ignored
        });
        Assert.Equal(new[] { "it", "ja", "zh" }, m.Languages);
    }

    [Fact]
    public async Task Install_OneWayLanguage_InstallsTheDirectionThereIs()
    {
        var files = new Files();
        var runtime = Zip(("onnxruntime.dll", "native"));
        var jaEn = Zip(("model.json", "{}"));
        files.Content["rt.zip"] = runtime;
        files.Content["ja-en.zip"] = jaEn;
        var manifest = new PackManifest(
            new() { [OfflinePacks.RuntimeId] = Describe("rt.zip", runtime) },
            new() { ["ja-en"] = Describe("ja-en.zip", jaEn) });
        using var http = new HttpClient(files);
        var packs = new OfflinePacks(_root, "https://example.test/rel/");

        await packs.InstallLanguageAsync(http, manifest, "ja", null, default);

        Assert.True(packs.CanRead("ja"));
        Assert.False(packs.CanWrite("ja"));
        Assert.Equal(new[] { "ja" }, packs.InstalledLanguages());
    }

    [Fact]
    public void EngineName_RoundTrips()
    {
        Assert.Equal(TranslationEngine.Offline, EngineNames.Parse("offline"));
        Assert.Equal("offline", EngineNames.ToName(TranslationEngine.Offline));
        Assert.Equal("offline", EngineNames.Normalize(" Offline "));
    }

    // ---------------- Downloads ----------------

    private sealed class Files : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Content { get; } = new();
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = request.RequestUri!.Segments[^1];
            lock (Requested) Requested.Add(name);
            return Task.FromResult(Content.TryGetValue(name, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        return ms.ToArray();
    }

    private static PackFile Describe(string name, byte[] bytes) =>
        new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));

    private (Files files, PackManifest manifest) Release()
    {
        var files = new Files();
        var runtime = Zip(("onnxruntime.dll", "native"));
        var zhEn = Zip(("model.json", "{}"));
        var enZh = Zip(("model.json", "{}"));
        files.Content["runtime-" + OfflinePacks.RuntimeId + ".zip"] = runtime;
        files.Content["zh-en.zip"] = zhEn;
        files.Content["en-zh.zip"] = enZh;
        var manifest = new PackManifest(
            new() { [OfflinePacks.RuntimeId] = Describe("runtime-" + OfflinePacks.RuntimeId + ".zip", runtime) },
            new() { ["zh-en"] = Describe("zh-en.zip", zhEn), ["en-zh"] = Describe("en-zh.zip", enZh) });
        files.Content["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
        return (files, manifest);
    }

    [Fact]
    public async Task Install_DownloadsRuntimeAndBothDirections()
    {
        var (files, _) = Release();
        using var http = new HttpClient(files);
        var packs = new OfflinePacks(_root, "https://example.test/rel/");
        var manifest = await packs.GetManifestAsync(http, default);
        var reported = new List<double>();

        await packs.InstallLanguageAsync(http, manifest, "zh", new SyncProgress(reported.Add), default);

        Assert.True(packs.RuntimeInstalled);
        Assert.True(packs.CanRead("zh"));
        Assert.True(packs.CanWrite("zh"));
        Assert.Equal(new[] { "zh" }, packs.InstalledLanguages());
        Assert.True(File.Exists(Path.Combine(packs.ModelDir("en-zh"), "model.json")));
        Assert.Equal(1.0, reported[^1]);
        Assert.True(reported.Zip(reported.Skip(1)).All(p => p.First <= p.Second), "progress never goes back");

        // Installing again downloads nothing.
        files.Requested.Clear();
        await packs.InstallLanguageAsync(http, manifest, "zh", null, default);
        Assert.Empty(files.Requested);
    }

    [Fact]
    public async Task Install_RejectsAFileThatFailsItsChecksum()
    {
        var (files, manifest) = Release();
        files.Content["en-zh.zip"] = Zip(("model.json", "tampered"));
        using var http = new HttpClient(files);
        var packs = new OfflinePacks(_root, "https://example.test/rel/");

        await Assert.ThrowsAsync<InvalidDataException>(() => packs.InstallLanguageAsync(http, manifest, "zh", null, default));

        Assert.False(packs.CanWrite("zh"));
        Assert.False(packs.IsInstalled("en-zh"));
        // Nothing half-written is left next to the packs.
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root), p => p.EndsWith(".part") || p.EndsWith(".tmp"));
    }

    [Fact]
    public async Task Install_Cancelled_LeavesNothingThatLooksInstalled()
    {
        var (files, manifest) = Release();
        using var http = new HttpClient(files);
        var packs = new OfflinePacks(_root, "https://example.test/rel/");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => packs.InstallLanguageAsync(http, manifest, "zh", null, cts.Token));

        Assert.False(packs.RuntimeInstalled);
        Assert.Empty(packs.InstalledLanguages());
    }

    [Fact]
    public async Task Remove_KeepsTheRuntime()
    {
        var (files, manifest) = Release();
        using var http = new HttpClient(files);
        var packs = new OfflinePacks(_root, "https://example.test/rel/");
        await packs.InstallLanguageAsync(http, manifest, "zh", null, default);

        packs.RemoveLanguage("zh");

        Assert.Empty(packs.InstalledLanguages());
        Assert.True(packs.RuntimeInstalled);
    }

    [Fact]
    public async Task Offline_WithNothingInstalled_ReportsEveryLineUntranslated()
    {
        using var svc = new TranslationService(null, Path.Combine(Path.GetTempPath(), "winlens-tests.log"), _root);
        svc.Configure(new EngineOptions(TranslationEngine.Offline));

        var result = await svc.TranslateManyAsync(new[] { new TranslationRequest("你好", null) }, "en");

        Assert.Equal(new[] { "你好" }, result);
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests need the values in order.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
