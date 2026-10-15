"""Offline MT benchmark: quality (chrF vs reference translations) and int8 CPU speed for a screen of lines."""
import json, os, sys, time, shutil, statistics, urllib.parse, urllib.request
from pathlib import Path
import sacrebleu
from onnxruntime.quantization import quantize_dynamic, QuantType
from optimum.onnxruntime import ORTModelForSeq2SeqLM
from transformers import AutoTokenizer

S = json.load(open(Path(__file__).with_name("sentences.json"), encoding="utf-8"))
PAIRS = [("zh","en"),("en","zh"),("it","en"),("en","it"),("ja","en"),("en","ja"),("zh","it"),("ja","zh")]
WORK = Path("work"); WORK.mkdir(exist_ok=True)
OUT = []

def google(lines, s, t):
    q = "\n".join(lines)
    url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&sl=%s&tl=%s&q=%s" % (s, "zh-CN" if t=="zh" else t, urllib.parse.quote(q))
    data = json.load(urllib.request.urlopen(url, timeout=30))
    text = "".join(seg[0] for seg in data[0] if seg[0])
    return [x.strip() for x in text.split("\n")]

_models = {}
def load(mid):
    if mid in _models: return _models[mid]
    d = WORK / mid.replace("/", "__"); q = d.with_name(d.name + "_q8")
    if not q.exists():
        m = ORTModelForSeq2SeqLM.from_pretrained(mid, export=True)
        m.save_pretrained(d); AutoTokenizer.from_pretrained(mid).save_pretrained(d)
        shutil.copytree(d, q)
        for f in q.glob("*.onnx"):
            quantize_dynamic(str(d / f.name), str(f), weight_type=QuantType.QInt8)
        shutil.rmtree(d)
    size = sum(f.stat().st_size for f in q.glob("*.onnx") if "with_past" not in f.name and "merged" not in f.name) + \
           sum(f.stat().st_size for f in q.glob("*with_past*.onnx"))
    m = ORTModelForSeq2SeqLM.from_pretrained(q); tok = AutoTokenizer.from_pretrained(q)
    _models[mid] = (m, tok, size)
    return _models[mid]

def run(mid, lines, s=None, t=None):
    m, tok, size = load(mid)
    kw = {}
    if "m2m100" in mid: tok.src_lang = s; kw["forced_bos_token_id"] = tok.get_lang_id(t)
    if "nllb" in mid:
        code = {"en":"eng_Latn","zh":"zho_Hans","it":"ita_Latn","ja":"jpn_Jpan"}
        tok.src_lang = code[s]; kw["forced_bos_token_id"] = tok.convert_tokens_to_ids(code[t])
    def go():
        enc = tok(lines, return_tensors="pt", padding=True)
        out = m.generate(**enc, num_beams=1, max_new_tokens=128, **kw)
        return tok.batch_decode(out, skip_special_tokens=True)
    go()
    times = []
    for _ in range(3):
        t0 = time.perf_counter(); res = go(); times.append(time.perf_counter() - t0)
    return res, statistics.median(times), size

def prefix(mid, t):
    # Multi-target opus models need a >>xxx<< token.
    if mid.endswith("en-zh") and "tc-big" not in mid: return ">>cmn_Hans<< "
    if "tc-big-en-zh" in mid: return ">>cmn_Hans<< "
    return ""

def opus_candidates(s, t):
    return [f"Helsinki-NLP/opus-mt-{s}-{t}", f"Helsinki-NLP/opus-mt-tc-big-{s}-{t}"]

def try_run(mid, lines, s, t):
    try:
        p = prefix(mid, t)
        return run(mid, [p + x for x in lines], s, t)
    except Exception as e:
        print(f"  skip {mid}: {type(e).__name__}: {str(e)[:120]}", flush=True)
        return None

def score(hyp, ref, t):
    tokenize = "zh" if t in ("zh", "ja") else "13a"
    return sacrebleu.corpus_chrf(hyp, [ref]).score, sacrebleu.corpus_bleu(hyp, [ref], tokenize=tokenize).score

for s, t in PAIRS:
    lines = S[s]
    ref = S[t]  # the sentence sets are parallel translations of each other
    print(f"\n===== {s}->{t} =====", flush=True)
    systems = {}
    if "en" in (s, t):
        for mid in opus_candidates(s, t):
            r = try_run(mid, lines, s, t)
            if r: systems[mid] = r
    else:
        a = next((r for mid in opus_candidates(s, "en") if (r := try_run(mid, lines, s, "en"))), None)
        if a:
            b = None
            for mid in opus_candidates("en", t):
                b = try_run(mid, a[0], "en", t)
                if b: break
            if b: systems["opus pivot via en"] = (b[0], a[1] + b[1], a[2] + b[2])
    for mid in ["facebook/m2m100_418M", "facebook/nllb-200-distilled-600M"]:
        r = try_run(mid, lines, s, t)
        if r: systems[mid] = r
    for name, (hyp, secs, size) in systems.items():
        chrf, bleu = score(hyp, ref, t)
        row = dict(pair=f"{s}-{t}", system=name, chrf=round(chrf,1), bleu=round(bleu,1), secs=round(secs,2), mb=round(size/1e6))
        OUT.append(row); print("RESULT", json.dumps(row), flush=True)
    for i, src in enumerate(lines):
        print(f"  [{i}] {src}\n      ref: {ref[i]}")
        for name, (hyp, _, _) in systems.items():
            print(f"      {name.split('/')[-1][:22]}: {hyp[i]}")
    sys.stdout.flush()

print("\n===== SUMMARY =====")
for r in OUT: print(f"{r['pair']:6} {r['system'][:40]:40} chrF {r['chrf']:5} BLEU {r['bleu']:5} {r['secs']:6}s {r['mb']:5}MB")
