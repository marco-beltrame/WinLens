import numpy as np, onnxruntime as ort
from pathlib import Path
from optimum.exporters.onnx import main_export
from transformers import AutoTokenizer, AutoConfig
mid="Helsinki-NLP/opus-mt-en-it"
raw=Path("raw"); main_export(mid, output=raw, task="text2text-generation-with-past")
print(sorted(p.name for p in raw.glob("*.onnx")))
tok=AutoTokenizer.from_pretrained(mid); c=AutoConfig.from_pretrained(mid)
H,D=c.decoder_attention_heads,c.d_model//c.decoder_attention_heads
ids=tok("The server is temporarily unavailable.").input_ids
enc=ort.InferenceSession(str(raw/"encoder_model.onnx"))
input_ids=np.array([ids],dtype=np.int64); mask=np.ones_like(input_ids)
hidden=enc.run(["last_hidden_state"],{"input_ids":input_ids,"attention_mask":mask})[0]
def pick(l):
    l=l[0,-1].copy(); l[c.pad_token_id]=-np.inf; return int(np.argmax(l))
# variant A: merged
dec=ort.InferenceSession(str(raw/"decoder_model_merged.onnx"))
print("merged inputs", [(i.name,i.shape) for i in dec.get_inputs()])
print("merged outputs", [(o.name,o.shape) for o in dec.get_outputs()][:5])
for plen in (0,1):
    try:
        pn=[i.name for i in dec.get_inputs() if i.name.startswith("past_key_values.")]
        past={n:np.zeros((1,H,plen,D),dtype=np.float32) for n in pn}
        on=["logits"]+["present."+n[len("past_key_values."):] for n in pn]
        t,out=c.decoder_start_token_id,[]
        for step in range(40):
            feed=dict(past); feed.update({"input_ids":np.array([[t]],dtype=np.int64),"encoder_attention_mask":mask,"encoder_hidden_states":hidden,"use_cache_branch":np.array([step>0])})
            res=dec.run(on,feed)
            if step<2: print("  step",step,[(n,r.shape) for n,r in zip(on,res)][:5])
            t=pick(res[0])
            if t==c.eos_token_id: break
            out.append(t); past=dict(zip(pn,res[1:]))
        print("MERGED plen",plen, tok.decode(out,skip_special_tokens=True))
    except Exception as e: print("MERGED plen",plen,"FAIL",str(e)[:400])
# variant B: decoder + decoder_with_past
d0=ort.InferenceSession(str(raw/"decoder_model.onnx")); d1=ort.InferenceSession(str(raw/"decoder_with_past_model.onnx"))
print("d0 in", [i.name for i in d0.get_inputs()], "out", [o.name for o in d0.get_outputs()][:5])
print("d1 in", [i.name for i in d1.get_inputs()][:8])
try:
    on0=[o.name for o in d0.get_outputs()]
    res=d0.run(on0,{"input_ids":np.array([[c.decoder_start_token_id]],dtype=np.int64),"encoder_attention_mask":mask,"encoder_hidden_states":hidden})
    pres=dict(zip(on0,res)); t=pick(res[0]); out=[t]
    pn=[i.name for i in d1.get_inputs() if i.name.startswith("past_key_values.")]
    enc_kv={n:pres["present."+n[len("past_key_values."):]] for n in pn if ".encoder." in n}
    dec_kv={n:pres["present."+n[len("past_key_values."):]] for n in pn if ".decoder." in n}
    on1=[o.name for o in d1.get_outputs()]
    for step in range(40):
        feed={"input_ids":np.array([[t]],dtype=np.int64),"encoder_attention_mask":mask, **enc_kv, **dec_kv}
        if "encoder_hidden_states" in [i.name for i in d1.get_inputs()]: feed["encoder_hidden_states"]=hidden
        res=d1.run(on1,feed); r=dict(zip(on1,res)); t=pick(res[0])
        if t==c.eos_token_id: break
        out.append(t)
        dec_kv={n:r["present."+n[len("past_key_values."):]] for n in dec_kv}
    print("SPLIT", tok.decode(out,skip_special_tokens=True))
except Exception as e: print("SPLIT FAIL", str(e)[:400])
