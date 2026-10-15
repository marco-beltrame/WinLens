using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WinLens.Services.Translation.Offline;

/// <summary>One downloadable file of the offline models release.</summary>
public sealed record PackFile(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// manifest.json of the offline models release: the ONNX Runtime build for each
/// architecture and one zip per translation direction ("zh-en", "en-zh"…).
/// </summary>
public sealed record PackManifest(
    [property: JsonPropertyName("runtime")] Dictionary<string, PackFile> Runtime,
    [property: JsonPropertyName("models")] Dictionary<string, PackFile> Models)
{
    /// <summary>
    /// Languages offline translation can offer: those with a model to or from English,
    /// since every other pair goes through English. Most have both; a few only one (no
    /// open model translates English into Japanese well enough, so Japanese is read only).
    /// </summary>
    public IReadOnlyList<string> Languages =>
        Models.Keys.Select(k => k.Split('-')).Where(p => p.Length == 2 && (p[0] == "en") != (p[1] == "en"))
            .Select(p => p[0] == "en" ? p[1] : p[0]).Distinct().OrderBy(l => l).ToList();
}

/// <summary>
/// Where offline translation lives on disk (%LocalAppData%\WinLens\offline): the ONNX
/// Runtime DLL in <c>runtime\</c> and one folder per direction in <c>models\</c>. Nothing
/// here ships with WinLens itself; it is downloaded when the user picks the offline
/// engine, so the default download stays the same size.
/// </summary>
public sealed class OfflinePacks
{
    public const string English = "en";

    /// <summary>The release every file is downloaded from; its tag does not change between versions.</summary>
    public const string DefaultBaseUrl = "https://github.com/marco-beltrame/WinLens/releases/download/offline-models/";

    private const string OkMarker = ".installed";

    private readonly string _baseUrl;

    public OfflinePacks(string root, string? baseUrl = null)
    {
        Root = root;
        _baseUrl = baseUrl ?? DefaultBaseUrl;
    }

    public string Root { get; }
    public string RuntimeDir => Path.Combine(Root, "runtime");
    private string ModelsDir => Path.Combine(Root, "models");

    /// <summary>"win-x64" or "win-arm64": the ONNX Runtime build this process can load.</summary>
    public static string RuntimeId => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64",
    };

    /// <summary>A WinLens target language as the packs name it; null where no model can serve it.</summary>
    public static string? PackCode(string target) => target switch
    {
        // Opus-MT writes Simplified Chinese only; better untranslated than the wrong script.
        "zh-TW" => null,
        "iw" => "he",
        _ => target.Split('-')[0],
    };

    public string ModelDir(string pair) => Path.Combine(ModelsDir, pair);

    public bool IsInstalled(string pair) => File.Exists(Path.Combine(ModelDir(pair), OkMarker));

    public bool RuntimeInstalled => File.Exists(Path.Combine(RuntimeDir, OkMarker));

    /// <summary>Text in <paramref name="lang"/> can be read (translated from).</summary>
    public bool CanRead(string lang) => lang == English || IsInstalled($"{lang}-{English}");

    /// <summary>Text can be written in <paramref name="lang"/> (translated into).</summary>
    public bool CanWrite(string lang) => lang == English || IsInstalled($"{English}-{lang}");

    /// <summary>Languages with at least one direction installed, English excluded.</summary>
    public IReadOnlyList<string> InstalledLanguages()
    {
        if (!Directory.Exists(ModelsDir)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(ModelsDir)
            .Select(d => Path.GetFileName(d)!.Split('-'))
            .Where(p => p.Length == 2 && (p[0] == English) != (p[1] == English) && IsInstalled($"{p[0]}-{p[1]}"))
            .Select(p => p[0] == English ? p[1] : p[0])
            .Distinct()
            .OrderBy(l => l)
            .ToList();
    }

    /// <summary>True when anything at all could be translated offline.</summary>
    public bool AnyInstalled => RuntimeInstalled && InstalledLanguages().Count > 0;

    public async Task<PackManifest> GetManifestAsync(HttpClient http, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(_baseUrl + "manifest.json", ct);
        return await JsonSerializer.DeserializeAsync<PackManifest>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("empty manifest");
    }

    /// <summary>
    /// Downloads what <paramref name="lang"/> needs (the runtime the first time, then each
    /// direction the release has), reporting progress from 0 to 1 over the total size.
    /// </summary>
    public async Task InstallLanguageAsync(HttpClient http, PackManifest manifest, string lang,
        IProgress<double>? progress, CancellationToken ct)
    {
        var jobs = new List<(PackFile file, string dir)>();
        if (!RuntimeInstalled)
        {
            if (!manifest.Runtime.TryGetValue(RuntimeId, out var rt))
                throw new NotSupportedException($"no runtime for {RuntimeId}");
            jobs.Add((rt, RuntimeDir));
        }
        var pairs = new[] { $"{lang}-{English}", $"{English}-{lang}" }.Where(manifest.Models.ContainsKey).ToList();
        if (pairs.Count == 0)
            throw new NotSupportedException($"no model for {lang}");
        foreach (var pair in pairs.Where(p => !IsInstalled(p)))
            jobs.Add((manifest.Models[pair], ModelDir(pair)));

        long total = Math.Max(1, jobs.Sum(j => j.file.Size));
        long before = 0;
        foreach (var (file, dir) in jobs)
        {
            long offset = before;
            var sub = progress == null ? null : new Progress<long>(n => progress.Report(Math.Min(1.0, (offset + n) / (double)total)));
            await InstallAsync(http, file, dir, sub, ct);
            before += file.Size;
        }
        progress?.Report(1.0);
    }

    /// <summary>Removes every direction of <paramref name="lang"/>. The runtime stays.</summary>
    public void RemoveLanguage(string lang)
    {
        foreach (var pair in new[] { $"{lang}-{English}", $"{English}-{lang}" })
            TryDeleteDirectory(ModelDir(pair));
    }

    private async Task InstallAsync(HttpClient http, PackFile file, string targetDir, IProgress<long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var tmpZip = Path.Combine(Root, $"{Guid.NewGuid():N}.part");
        var tmpDir = Path.Combine(Root, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var response = await http.GetAsync(_baseUrl + file.File, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(tmpZip);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    sha.AppendData(buffer, 0, n);
                    read += n;
                    if (read > file.Size) throw new InvalidDataException($"{file.File} is larger than expected");
                    progress?.Report(read);
                }
                var hash = Convert.ToHexString(sha.GetHashAndReset());
                if (read != file.Size || !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{file.File} failed its checksum");
            }

            ZipFile.ExtractToDirectory(tmpZip, tmpDir);
            await File.WriteAllTextAsync(Path.Combine(tmpDir, OkMarker), file.Sha256, ct);

            // Swap in the finished folder in one move, so a cancelled or failed download
            // never leaves a half-written model behind that looks installed.
            TryDeleteDirectory(targetDir);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(tmpDir, targetDir);
        }
        finally
        {
            TryDeleteFile(tmpZip);
            TryDeleteDirectory(tmpDir);
        }
    }

    /// <summary>Leftovers of a download that was interrupted by a crash or power loss.</summary>
    public void CleanUpPartialDownloads()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var f in Directory.EnumerateFiles(Root, "*.part")) TryDeleteFile(f);
        foreach (var d in Directory.EnumerateDirectories(Root, "*.tmp")) TryDeleteDirectory(d);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
