using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;

namespace WinLens.Services.Translation.Offline;

/// <summary>
/// Translates on this PC with the downloaded Opus-MT models. Every pair goes through
/// English (zh -> en -> it), so a language needs one pack instead of one per pair.
/// Models load on first use and are unloaded after a few idle minutes: they take
/// 100-200 MB of memory each, too much for a tray app to hold on to.
/// </summary>
internal sealed class OfflineProvider : ITranslationProvider, IDisposable
{
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(3);

    private readonly OfflinePacks _packs;
    private readonly Action<string> _log;
    private readonly Dictionary<string, MarianModel> _models = new();
    private readonly object _lock = new();
    private readonly Timer _idleTimer;
    private SessionOptions? _options;
    private int _busy;
    private bool _unloadRequested;

    public OfflineProvider(OfflinePacks packs, Action<string> log)
    {
        _packs = packs;
        _log = log;
        _idleTimer = new Timer(_ => Unload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Name => "Offline";

    public Task<string?[]> TranslateAsync(IReadOnlyList<TranslationRequest> items, string target, CancellationToken ct)
    {
        // CPU-bound: keep it off the caller's (UI) thread.
        return Task.Run(() => Translate(items, target, ct), ct);
    }

    private string?[] Translate(IReadOnlyList<TranslationRequest> items, string target, CancellationToken ct)
    {
        lock (_lock) _busy++;
        try
        {
            return TranslateCore(items, target, ct);
        }
        finally
        {
            lock (_lock)
            {
                if (--_busy == 0 && _unloadRequested) UnloadNow();
            }
            _idleTimer.Change(IdleUnload, Timeout.InfiniteTimeSpan);
        }
    }

    private string?[] TranslateCore(IReadOnlyList<TranslationRequest> items, string target, CancellationToken ct)
    {
        var results = new string?[items.Count];
        var tgt = OfflinePacks.PackCode(target);
        if (tgt == null || !_packs.RuntimeInstalled || !_packs.IsLanguageInstalled(tgt))
            return results;

        var installed = _packs.InstalledLanguages();
        var bySource = Enumerable.Range(0, items.Count)
            .GroupBy(i => SourceDetector.Detect(items[i].Text, items[i].SourceHint, installed));

        foreach (var group in bySource)
        {
            ct.ThrowIfCancellationRequested();
            var src = group.Key;
            var idx = group.ToArray();
            if (src == tgt)
            {
                // Already in the target language: nothing to do, as Google would answer.
                foreach (var i in idx) results[i] = items[i].Text;
                continue;
            }
            if (src != OfflinePacks.English && !_packs.IsLanguageInstalled(src))
                continue;

            try
            {
                var lines = idx.Select(i => items[i].Text).ToArray();
                if (src != OfflinePacks.English)
                    lines = Model($"{src}-{OfflinePacks.English}").Translate(lines, ct);
                if (tgt != OfflinePacks.English)
                    lines = Model($"{OfflinePacks.English}-{tgt}").Translate(lines, ct);
                for (int k = 0; k < idx.Length; k++)
                    results[idx[k]] = string.IsNullOrWhiteSpace(lines[k]) ? null : lines[k];
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A damaged model or a runtime that won't load: report as untranslated,
                // so the chain can fall back like for any other provider.
                _log($"offline {src}->{tgt}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return results;
    }

    private MarianModel Model(string pair)
    {
        lock (_lock)
        {
            if (_models.TryGetValue(pair, out var m)) return m;
            OnnxRuntimeLoader.Use(_packs.RuntimeDir);
            _options ??= CreateOptions();
            m = new MarianModel(_packs.ModelDir(pair), _options);
            _models[pair] = m;
            return m;
        }
    }

    private static SessionOptions CreateOptions()
    {
        var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        // Leave a core for the rest of the PC: translation runs while the user waits on
        // the overlay, but the game or browser behind it is still running.
        o.IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        return o;
    }

    /// <summary>
    /// Frees every loaded model (idle, or the packs on disk changed). While a translation
    /// is running this waits for it to finish instead of pulling the model from under it.
    /// </summary>
    public void Unload()
    {
        lock (_lock)
        {
            if (_busy > 0) { _unloadRequested = true; return; }
            UnloadNow();
        }
    }

    private void UnloadNow()
    {
        foreach (var m in _models.Values) m.Dispose();
        _models.Clear();
        _unloadRequested = false;
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        Unload();
        _options?.Dispose();
    }
}

/// <summary>
/// Points the ONNX Runtime bindings at the DLL in the offline folder. WinLens ships only
/// the managed half; the native one is downloaded with the first language pack.
/// </summary>
internal static class OnnxRuntimeLoader
{
    private static string? _dir;

    public static void Use(string dir)
    {
        if (Interlocked.CompareExchange(ref _dir, dir, null) != null)
            return;
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != "onnxruntime" || _dir == null) return IntPtr.Zero;
        var file = Path.Combine(_dir, OperatingSystem.IsWindows() ? "onnxruntime.dll" : "libonnxruntime.so");
        // Not there (tests, or a runtime from a NuGet package): the default probing applies.
        return File.Exists(file) && NativeLibrary.TryLoad(file, out var handle) ? handle : IntPtr.Zero;
    }
}
