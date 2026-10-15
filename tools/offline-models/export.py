"""
Builds the offline translation packs WinLens downloads (see .github/workflows/offline-models.yml).

For every direction in models.json: export the Opus-MT model to ONNX, quantize it to int8,
keep only what the app reads (encoder.onnx, decoder.onnx, source.spm, target.spm,
vocab.json, model.json) and zip it. Also writes fixtures.json: token ids and greedy output
for a few sentences, decoded here with the same ONNX files, which the .NET test compares
against WinLens's own tokenizer and decoder loop.

models.json maps each direction to candidate models, tried in order.

Usage: python export.py <models.json> <out_dir>
"""
import hashlib, json, shutil, sys, zipfile
from pathlib import Path

import numpy as np
import onnxruntime as ort
from onnxruntime.quantization import QuantType, quantize_dynamic
from optimum.exporters.onnx import main_export
from transformers import AutoConfig, AutoTokenizer

# Short UI strings and full sentences, as they come off a screen.
SAMPLES = {
    "en": ["Settings", "The server is temporarily unavailable. Please try again later.",
           "This item cannot be sold to merchants.", "Are you sure you want to delete this file permanently?"],
    "zh": ["设置", "服务器暂时不可用，请稍后再试。", "该物品无法出售给商人。", "确定要永久删除此文件吗？"],
    "ja": ["設定", "サーバーは一時的に利用できません。", "このアイテムは商人に売却できません。", "このファイルを完全に削除してもよろしいですか？"],
    "ko": ["설정", "서버를 일시적으로 사용할 수 없습니다.", "이 아이템은 상인에게 판매할 수 없습니다.", "이 파일을 영구적으로 삭제하시겠습니까?"],
    "it": ["Impostazioni", "Il server non è temporaneamente disponibile. Riprova più tardi.", "Questo oggetto non può essere venduto ai mercanti."],
    "de": ["Einstellungen", "Der Server ist vorübergehend nicht erreichbar. Bitte versuche es später erneut."],
    "fr": ["Paramètres", "Le serveur est temporairement indisponible. Veuillez réessayer plus tard."],
    "es": ["Ajustes", "El servidor no está disponible temporalmente. Inténtalo de nuevo más tarde."],
    "pt": ["Configurações", "O servidor está temporariamente indisponível. Tente novamente mais tarde."],
    "ru": ["Настройки", "Сервер временно недоступен. Повторите попытку позже."],
}
DEFAULT_SAMPLES = ["Inventory is full.", "Press any key to continue"]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def greedy(enc, dec, cfg, ids, max_new=64):
    """The decoder loop of MarianModel.cs, one sentence at a time."""
    input_ids = np.array([ids], dtype=np.int64)
    mask = np.ones_like(input_ids)
    hidden = enc.run(["last_hidden_state"], {"input_ids": input_ids, "attention_mask": mask})[0]
    past_names = [i.name for i in dec.get_inputs() if i.name.startswith("past_key_values.")]
    # The merged decoder ignores the past on its first step, but its shapes must still be
    # valid: one dummy position, as optimum feeds it.
    past = {n: np.zeros((1, cfg["heads"], 1, cfg["headDim"]), dtype=np.float32) for n in past_names}
    out_names = ["logits"] + ["present." + n[len("past_key_values."):] for n in past_names]
    token, out = cfg["decoderStartId"], []
    for step in range(max_new):
        feed = dict(past)
        feed.update({
            "input_ids": np.array([[token]], dtype=np.int64),
            "encoder_attention_mask": mask,
            "encoder_hidden_states": hidden,
            "use_cache_branch": np.array([step > 0]),
        })
        res = dec.run(out_names, feed)
        logits = res[0][0, -1].copy()
        logits[cfg["padId"]] = -np.inf
        token = int(np.argmax(logits))
        if token == cfg["eosId"]:
            break
        out.append(token)
        past = dict(zip(past_names, res[1:]))
    return out


def build(pair, spec, out_dir, work):
    model_id, prefix = spec["model"], spec.get("prefix")
    src, _ = pair.split("-")
    tok = AutoTokenizer.from_pretrained(model_id)
    conf = AutoConfig.from_pretrained(model_id)
    codes = getattr(tok, "supported_language_codes", [])
    if codes and len(codes) > 1 and not prefix:
        raise ValueError(f"needs a target prefix, one of {codes[:20]}")
    if prefix and prefix not in tok.get_vocab():
        raise ValueError(f"{prefix} is not in the vocabulary ({codes[:20]})")
    raw = work / f"{pair}-raw"
    main_export(model_id, output=raw, task="text2text-generation-with-past")

    pack = work / pair
    shutil.rmtree(pack, ignore_errors=True)
    pack.mkdir(parents=True)
    quantize_dynamic(str(raw / "encoder_model.onnx"), str(pack / "encoder.onnx"), weight_type=QuantType.QInt8)
    quantize_dynamic(str(raw / "decoder_model_merged.onnx"), str(pack / "decoder.onnx"), weight_type=QuantType.QInt8)
    tok.save_pretrained(raw / "tok")
    for name in ("source.spm", "target.spm", "vocab.json"):
        shutil.copy(raw / "tok" / name, pack / name)

    heads = conf.decoder_attention_heads
    cfg = {
        "padId": conf.pad_token_id,
        "eosId": conf.eos_token_id,
        "unkId": tok.unk_token_id,
        "decoderStartId": conf.decoder_start_token_id,
        "layers": conf.decoder_layers,
        "heads": heads,
        "headDim": conf.d_model // heads,
        "targetPrefix": prefix,
    }
    (pack / "model.json").write_text(json.dumps(cfg, indent=2))
    shutil.rmtree(raw)

    enc = ort.InferenceSession(str(pack / "encoder.onnx"))
    dec = ort.InferenceSession(str(pack / "decoder.onnx"))
    fixtures = []
    for text in SAMPLES.get(src, DEFAULT_SAMPLES):
        ids = tok((prefix + " " if prefix else "") + text).input_ids
        out = greedy(enc, dec, cfg, ids)
        fixtures.append({"text": text, "ids": ids, "output": tok.decode(out, skip_special_tokens=True)})
        print(f"  {pair}: {text} -> {fixtures[-1]['output']}", flush=True)

    zip_path = out_dir / f"{pair}.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(pack.iterdir()):
            z.write(f, f.name)
    return {"file": zip_path.name, "size": zip_path.stat().st_size, "sha256": sha256(zip_path)}, fixtures


def main():
    models = json.loads(Path(sys.argv[1]).read_text())
    out_dir = Path(sys.argv[2]); out_dir.mkdir(parents=True, exist_ok=True)
    work = Path("work"); work.mkdir(exist_ok=True)
    manifest, fixtures = {"runtime": {}, "models": {}}, {}
    for pair, candidates in models.items():
        # The first candidate that exports cleanly wins; a pair none can serve is left out.
        for spec in candidates:
            print(f"== {pair} ({spec['model']})", flush=True)
            try:
                manifest["models"][pair], fixtures[pair] = build(pair, spec, out_dir, work)
                break
            except Exception as e:
                print(f"  SKIP {spec['model']}: {type(e).__name__}: {str(e)[:300]}", flush=True)
            finally:
                shutil.rmtree(Path.home() / ".cache/huggingface/hub", ignore_errors=True)
        else:
            print(f"  NO MODEL for {pair}", flush=True)
    for zip_path in sorted(out_dir.glob("runtime-*.zip")):
        rid = zip_path.stem[len("runtime-"):]
        manifest["runtime"][rid] = {"file": zip_path.name, "size": zip_path.stat().st_size, "sha256": sha256(zip_path)}
    if not manifest["models"]:
        sys.exit("no model could be built")
    (out_dir / "manifest.json").write_text(json.dumps(manifest, indent=2))
    (work / "fixtures.json").write_text(json.dumps(fixtures, ensure_ascii=False, indent=1))


if __name__ == "__main__":
    main()
