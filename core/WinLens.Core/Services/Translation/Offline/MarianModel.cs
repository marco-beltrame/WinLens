using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace WinLens.Services.Translation.Offline;

/// <summary>
/// What the models workflow writes next to each model as <c>model.json</c>: the token ids
/// and shapes the decoder loop needs, so nothing has to parse Hugging Face's config.
/// </summary>
internal sealed record MarianConfig(
    int PadId,
    int EosId,
    int UnkId,
    int DecoderStartId,
    int Layers,
    int Heads,
    int HeadDim,
    string? TargetPrefix = null)
{
    public static MarianConfig Load(string path) =>
        JsonSerializer.Deserialize<MarianConfig>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("empty model.json");
}

/// <summary>
/// One Opus-MT (Marian) translation direction, exported to ONNX and quantized to int8:
/// <c>encoder.onnx</c>, <c>decoder.onnx</c> (the merged decoder, with and without the
/// key/value cache), <c>source.spm</c>, <c>target.spm</c>, <c>vocab.json</c> and
/// <c>model.json</c>. Decoding is greedy, a whole batch of lines at once.
/// </summary>
internal sealed class MarianModel : IDisposable
{
    // Room for a translation to run longer than its source (CJK -> Latin roughly doubles
    // the token count), with a hard cap so a model stuck repeating itself stops.
    private const int MaxNewTokens = 256;
    private const int MaxSourceTokens = 256;
    private const int MaxBatch = 16;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly SentencePieceTokenizer _sourceSpm;
    private readonly Dictionary<string, int> _vocab;
    private readonly string[] _pieces;
    private readonly MarianConfig _cfg;
    private readonly string[] _pastNames;
    private readonly bool _hasCacheBranch;
    private readonly object _runLock = new();
    private readonly RunOptions _run = new();

    public MarianModel(string dir, SessionOptions options)
    {
        _cfg = MarianConfig.Load(Path.Combine(dir, "model.json"));
        using (var spm = File.OpenRead(Path.Combine(dir, "source.spm")))
            _sourceSpm = SentencePieceTokenizer.Create(spm, false, false);

        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(dir, "vocab.json")))
            ?? throw new InvalidDataException("empty vocab.json");
        _pieces = new string[_vocab.Values.Max() + 1];
        foreach (var (piece, id) in _vocab)
            _pieces[id] = piece;

        _encoder = new InferenceSession(Path.Combine(dir, "encoder.onnx"), options);
        try
        {
            _decoder = new InferenceSession(Path.Combine(dir, "decoder.onnx"), options);
        }
        catch
        {
            _encoder.Dispose();
            throw;
        }
        _pastNames = _decoder.InputMetadata.Keys.Where(k => k.StartsWith("past_key_values.", StringComparison.Ordinal)).ToArray();
        _hasCacheBranch = _decoder.InputMetadata.ContainsKey("use_cache_branch");
    }

    /// <summary>Source text to the ids the encoder takes: optional target-language token, pieces, then &lt;/s&gt;.</summary>
    internal int[] Encode(string text)
    {
        var ids = new List<int>();
        if (_cfg.TargetPrefix is { Length: > 0 } prefix && _vocab.TryGetValue(prefix, out var p))
            ids.Add(p);
        foreach (var token in _sourceSpm.EncodeToTokens(text, out _))
        {
            ids.Add(_vocab.TryGetValue(token.Value, out var id) ? id : _cfg.UnkId);
            if (ids.Count >= MaxSourceTokens - 1)
                break;
        }
        ids.Add(_cfg.EosId);
        return ids.ToArray();
    }

    /// <summary>Target ids back to text: SentencePiece marks word starts with U+2581.</summary>
    internal string Decode(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (id == _cfg.EosId || id == _cfg.PadId || id == _cfg.UnkId || id < 0 || id >= _pieces.Length)
                continue;
            var piece = _pieces[id];
            if (piece == null || (piece.StartsWith('<') && piece.EndsWith('>')))
                continue;
            sb.Append(piece);
        }
        return sb.Replace('▁', ' ').ToString().Trim();
    }

    /// <summary>Translates <paramref name="lines"/>; the result is aligned with them.</summary>
    public string[] Translate(IReadOnlyList<string> lines, CancellationToken ct)
    {
        var results = new string[lines.Count];
        var encoded = lines.Select(Encode).ToArray();

        // Similar lengths batch together, so little of each batch is padding.
        var order = Enumerable.Range(0, lines.Count).OrderBy(i => encoded[i].Length).ToArray();
        for (int start = 0; start < order.Length; start += MaxBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = order.Skip(start).Take(MaxBatch).ToArray();
            var output = TranslateBatch(batch.Select(i => encoded[i]).ToArray(), ct);
            for (int k = 0; k < batch.Length; k++)
                results[batch[k]] = Decode(output[k]);
        }
        return results;
    }

    private List<int>[] TranslateBatch(int[][] sources, CancellationToken ct)
    {
        // An InferenceSession can run concurrently, but two screens translating at once on
        // a CPU only slow each other down; one at a time also bounds the memory used.
        lock (_runLock)
            return Greedy(sources, ct);
    }

    private List<int>[] Greedy(int[][] sources, CancellationToken ct)
    {
        int batch = sources.Length;
        int srcLen = sources.Max(s => s.Length);

        var inputIds = new long[batch * srcLen];
        var mask = new long[batch * srcLen];
        for (int b = 0; b < batch; b++)
            for (int t = 0; t < srcLen; t++)
            {
                bool real = t < sources[b].Length;
                inputIds[b * srcLen + t] = real ? sources[b][t] : _cfg.PadId;
                mask[b * srcLen + t] = real ? 1 : 0;
            }

        long[] srcShape = { batch, srcLen };
        using var idsValue = OrtValue.CreateTensorValueFromMemory(inputIds, srcShape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, srcShape);
        using var encOut = _encoder.Run(_run,
            new Dictionary<string, OrtValue> { ["input_ids"] = idsValue, ["attention_mask"] = maskValue },
            new[] { "last_hidden_state" });
        var hidden = encOut[0];

        var output = new List<int>[batch];
        for (int b = 0; b < batch; b++) output[b] = new List<int>();
        var done = new bool[batch];
        var next = new long[batch];
        Array.Fill(next, _cfg.DecoderStartId);

        // The first step runs without a cache (use_cache_branch=false). The past it ignores
        // must still have valid shapes: one zeroed position, as optimum feeds it.
        var past = new Dictionary<string, OrtValue>();
        var dummy = new float[batch * _cfg.Heads * _cfg.HeadDim];
        foreach (var name in _pastNames)
            past[name] = OrtValue.CreateTensorValueFromMemory(dummy, new long[] { batch, _cfg.Heads, 1, _cfg.HeadDim });
        var outputNames = new List<string> { "logits" };
        outputNames.AddRange(_pastNames.Select(n => "present." + n["past_key_values.".Length..]));

        int limit = Math.Min(MaxNewTokens, srcLen * 3 + 10);
        try
        {
            for (int step = 0; step < limit && done.Contains(false); step++)
            {
                ct.ThrowIfCancellationRequested();
                using var nextValue = OrtValue.CreateTensorValueFromMemory(next, new long[] { batch, 1 });
                using var branch = OrtValue.CreateTensorValueFromMemory(new[] { step > 0 }, new long[] { 1 });
                var inputs = new Dictionary<string, OrtValue>(past)
                {
                    ["input_ids"] = nextValue,
                    ["encoder_attention_mask"] = maskValue,
                    ["encoder_hidden_states"] = hidden,
                };
                if (_hasCacheBranch) inputs["use_cache_branch"] = branch;

                var outs = _decoder.Run(_run, inputs, outputNames);
                var logits = outs[0].GetTensorDataAsSpan<float>();
                int vocab = logits.Length / batch;
                for (int b = 0; b < batch; b++)
                {
                    if (done[b]) { next[b] = _cfg.PadId; continue; }
                    var row = logits.Slice(b * vocab, vocab);
                    int best = -1;
                    float bestScore = float.NegativeInfinity;
                    for (int v = 0; v < row.Length; v++)
                    {
                        // Marian never emits padding (bad_words_ids in its config).
                        if (v == _cfg.PadId) continue;
                        if (row[v] > bestScore) { bestScore = row[v]; best = v; }
                    }
                    next[b] = best;
                    if (best == _cfg.EosId) done[b] = true;
                    else output[b].Add(best);
                }

                // The cache this step produced is the next step's past. With the cache
                // branch on, the encoder's key/values come back unchanged; they are taken
                // from this step's output all the same, which keeps ownership simple.
                outs[0].Dispose();
                foreach (var v in past.Values) v.Dispose();
                past.Clear();
                for (int k = 0; k < _pastNames.Length; k++)
                    past[_pastNames[k]] = outs[k + 1];
            }
        }
        finally
        {
            foreach (var v in past.Values) v.Dispose();
        }
        return output;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _run.Dispose();
    }
}
