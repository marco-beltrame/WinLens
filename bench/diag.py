import json, os, sys, numpy as np, onnx, onnxruntime as ort
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
for f in sorted(raw.glob("*.onnx")): print("raw", f.name, mb(f))
q=Path("q"); q.mkdir(exist_ok=True)
def Q(src,dst): quantize_dynamic(str(src), str(dst), weight_type=QuantType.QInt8, reduce_range=True); return dst
Q(raw/"encoder_model.onnx", q/"enc.onnx")
Q(raw/"decoder_model_merged.onnx", q/"merged_q.onnx")
Q(raw/"decoder_model.onnx", q/"d0.onnx"); Q(raw/"decoder_with_past_model.onnx", q/"d1.onnx")
try:
    m=merge_decoders(str(q/"d0.onnx"), str(q/"d1.onnx"), save_path=str(q/"merged_after.onnx"))
except Exception as e: print("merge after quant FAIL", str(e)[:300])
for f in sorted(q.glob("*.onnx")): print("q", f.name, mb(f))
tok=AutoTokenizer.from_pretrained(mid); c=AutoConfig.from_pretrained(mid)
cfg={"padId":c.pad_token_id,"eosId":c.eos_token_id,"decoderStartId":c.decoder_start_token_id,"heads":c.decoder_attention_heads,"headDim":c.d_model//c.decoder_attention_heads}
enc=ort.InferenceSession(str(q/"enc.onnx"))
for name in ["merged_q.onnx","merged_after.onnx"]:
    if not (q/name).exists(): continue
    dec=ort.InferenceSession(str(q/name))
    for t in ["The server is temporarily unavailable. Please try again later.","This item cannot be sold to merchants."]:
        print(name, tok.decode(greedy(enc,dec,cfg,tok(t).input_ids),skip_special_tokens=True))
# count quantized ops inside merged
g=onnx.load(str(q/"merged_q.onnx"))
def ops(graph,acc):
    for n in graph.node:
        acc[n.op_type]=acc.get(n.op_type,0)+1
        for a in n.attribute:
            if a.g and a.g.node: ops(a.g,acc)
    return acc
print("merged_q ops", {k:v for k,v in ops(g.graph,{}).items() if k in ("MatMul","MatMulInteger","DynamicQuantizeLinear","If","Gather")})
