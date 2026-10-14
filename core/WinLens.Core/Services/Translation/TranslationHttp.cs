using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WinLens.Services.Translation;

/// <summary>
/// What the built-in providers share: one HttpClient, one cap on concurrent requests and
/// one log file (%TEMP%\winlens.log).
/// </summary>
internal sealed class TranslationHttp : IDisposable
{
    public HttpClient Client { get; }

    // A full-screen capture can still mean several requests; firing them all at the free
    // endpoints at once invites 429s (which degrade silently to untranslated text).
    private readonly SemaphoreSlim _gate = new(4);
    private readonly string _logPath;

    public TranslationHttp(HttpMessageHandler? handler, string? logPath)
    {
        Client = handler == null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WinLens/1.0");
        _logPath = logPath ?? Path.Combine(Path.GetTempPath(), "winlens.log");
    }

    /// <summary>Runs <paramref name="request"/> once a request slot is free.</summary>
    public async Task<T> GatedAsync<T>(Func<Task<T>> request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await request();
        }
        finally
        {
            // Dispose() may have run while this request was in flight.
            try { _gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    // Bound the log the same way App does for crash.log. A backend that is down or
    // rate-limiting writes a line per failed request, so an unbounded file grows fast
    // and unattended.
    private const long MaxLogBytes = 256 * 1024;
    private readonly object _logLock = new();
    private long _logBytes = -1; // -1 = size not read from disk yet

    public void Log(string line)
    {
        try
        {
            var entry = $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}";

            // Several requests run at once and each may log; AppendAllText opens the file
            // exclusively, so without this they would trip over each other.
            lock (_logLock)
            {
                if (_logBytes < 0)
                {
                    var info = new FileInfo(_logPath);
                    _logBytes = info.Exists ? info.Length : 0;
                }

                if (_logBytes + entry.Length > MaxLogBytes)
                {
                    // Start fresh rather than grow forever. Tracking the size in memory
                    // keeps this to one stat() per process instead of one per line.
                    File.Delete(_logPath);
                    _logBytes = 0;
                }

                File.AppendAllText(_logPath, entry);
                _logBytes += entry.Length;
            }
        }
        catch { /* logging must never throw */ }
    }

    public void Dispose()
    {
        Client.Dispose();
        _gate.Dispose();
    }
}
