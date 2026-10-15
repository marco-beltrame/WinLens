import os, sys, numpy as np, onnx, onnxruntime as ort, time
from pathlib import Path
sys.path.insert(0, "tools/offline-models")
from reference import greedy
from optimum.exporters.onnx import main_export
from optimum.onnx import merge_decoders
from onnxruntime.quantization import quantize_dynamic, QuantType
from transformers import AutoTokenizer, AutoConfig
mid="Helsinki-NLP/opus-mt-en-it"
raw=Path("raw"); main_export(mid, output=raw, task="text2text-generation-with-past")
mb=lambda p: round(os.path.getsize(p)/1e6,1)
q=Path("q"); q.mkdir(exist_ok=True)
def Q(src,dst,**kw): quantize_dynamic(str(src), str(dst), weight_type=QuantType.QInt8, reduce_range=True, **kw); return dst
Q(raw/"encoder_model.onnx", q/"enc.onnx")
try: Q(raw/"decoder_model_merged.onnx", q/"merged_sub.onnx", extra_options={"EnableSubgraph": True})
except Exception as e: print("subgraph FAIL", str(e)[:300])
Q(raw/"decoder_model.onnx", q/"d0.onnx"); Q(raw/"decoder_with_past_model.onnx", q/"d1.onnx")
try: merge_decoders(str(q/"d0.onnx"), str(q/"d1.onnx"), save_path=str(q/"merged_after.onnx"), strict=False)
except Exception as e: print("merge FAIL", str(e)[:300])
for f in sorted(q.glob("*.onnx")): print("q", f.name, mb(f))
tok=AutoTokenizer.from_pretrained(mid); c=AutoConfig.from_pretrained(mid)
cfg={"padId":c.pad_token_id,"eosId":c.eos_token_id,"decoderStartId":c.decoder_start_token_id,"heads":c.decoder_attention_heads,"headDim":c.d_model//c.decoder_attention_heads}
enc=ort.InferenceSession(str(q/"enc.onnx"))
texts=["The server is temporarily unavailable. Please try again later.","This item cannot be sold to merchants.","Are you sure you want to delete this file permanently?"]
for name in ["merged_sub.onnx","merged_after.onnx"]:
    if not (q/name).exists(): continue
    dec=ort.InferenceSession(str(q/name))
    print(name, "inputs", len(dec.get_inputs()), "outputs", len(dec.get_outputs()))
    t0=time.perf_counter()
    for t in texts: print(" ", name, tok.decode(greedy(enc,dec,cfg,tok(t).input_ids),skip_special_tokens=True))
    print("  time", round(time.perf_counter()-t0,2))
