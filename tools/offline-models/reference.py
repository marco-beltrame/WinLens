"""
The decoder loop of MarianModel.cs in Python, the reference WinLens is tested against.

As a script it adds "outIds" to every sentence of fixtures.json, decoded on this machine:
int8 kernels differ between CPUs, so the comparison must run where the .NET test runs.

Usage: python reference.py <offline root> <fixtures.json>
"""
import json, sys
from pathlib import Path

import numpy as np
import onnxruntime as ort


def greedy(enc, dec, cfg, ids):
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
    for step in range(min(256, len(ids) * 3 + 10)):
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
        # The encoder's key/values are computed on the first step only; later steps return
        # placeholders for them, so only the decoder's half of the cache moves on.
        for name, value in zip(past_names, res[1:]):
            if step == 0 or ".decoder." in name:
                past[name] = value
    return out


def main():
    root, fixtures_path = Path(sys.argv[1]), Path(sys.argv[2])
    fixtures = json.loads(fixtures_path.read_text())
    for pair, items in fixtures.items():
        pack = root / "models" / pair
        cfg = json.loads((pack / "model.json").read_text())
        enc = ort.InferenceSession(str(pack / "encoder.onnx"))
        dec = ort.InferenceSession(str(pack / "decoder.onnx"))
        for item in items:
            item["outIds"] = greedy(enc, dec, cfg, item["ids"])
    fixtures_path.write_text(json.dumps(fixtures, ensure_ascii=False, indent=1))


if __name__ == "__main__":
    main()
