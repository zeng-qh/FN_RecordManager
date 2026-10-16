"""
VoiceVault 本地语音转写边车（ASR）
=================================
基于 faster-whisper 的本地化语音转文字服务。数据不出 NAS，纯本地推理。
由 VoiceVault 服务端在「启用 ASR」时通过 HTTP 调用，平时可不启动。

端点：
  GET  /health
  POST /transcribe   multipart: file=音频, model=模型规模, diarize=bool
      → { "text": "...", "segments": [{start,end,text,speaker?}], "language": "zh" }

运行：
  pip install -r requirements.txt
  python asr_server.py --host 0.0.0.0 --port 8000
或：
  docker build -t voicvault-asr . && docker run -p 8000:8000 -v $PWD/models:/root/.cache voicvault-asr
"""
import argparse
import os
import tempfile

import uvicorn
from fastapi import FastAPI, File, Form, UploadFile
from fastapi.middleware.cors import CORSMiddleware

app = FastAPI(title="VoiceVault ASR")
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])

_model_cache = {}


def get_model(model_size: str):
    """懒加载并缓存 Whisper 模型（首次会下载，之后复用）。"""
    if model_size in _model_cache:
        return _model_cache[model_size]
    from faster_whisper import WhisperModel

    # 树莓派 4B(arm64) 用 int8 量化更省内存；x86 可酌情调高
    device = os.environ.get("ASR_DEVICE", "cpu")
    compute_type = os.environ.get("ASR_COMPUTE", "int8")
    model = WhisperModel(model_size, device=device, compute_type=compute_type)
    _model_cache[model_size] = model
    return model


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/transcribe")
def transcribe(
    file: UploadFile = File(...),
    model: str = Form("small"),
    diarize: bool = Form(False),
):
    model_inst = get_model(model)

    suffix = os.path.splitext(file.filename or "audio.m4a")[1] or ".m4a"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        tmp.write(file.file.read())
        tmp_path = tmp.name

    try:
        segments, info = model_inst.transcribe(
            tmp_path, beam_size=5, vad_filter=True, language=None
        )
        out_segments = []
        speaker_idx = 0
        prev_end = 0.0
        for seg in segments:
            seg_dict = {
                "start": round(seg.start, 2),
                "end": round(seg.end, 2),
                "text": seg.text.strip(),
            }
            # 简易说话人标注：仅作占位，真正分段需 pyannote（需 HF token）
            if diarize:
                if seg.start - prev_end > 1.5:  # 间隔较大视为换人
                    speaker_idx += 1
                seg_dict["speaker"] = f"说话人{speaker_idx + 1}"
                prev_end = seg.end
            out_segments.append(seg_dict)

        text = " ".join(s["text"] for s in out_segments)
        return {
            "text": text,
            "segments": out_segments,
            "language": info.language,
        }
    finally:
        try:
            os.remove(tmp_path)
        except OSError:
            pass


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8000)
    args = parser.parse_args()
    uvicorn.run(app, host=args.host, port=args.port)
