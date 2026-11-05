# 本地语音转写边车：多架构（amd64 / arm64）
# faster-whisper 基于 CTranslate2（非 PyTorch），slim 基底 + int8 量化已较精简；
# alpine/musl 无官方 wheel，勿改。首次运行会按需下载 Whisper 模型（建议挂卷缓存）。
# syntax=docker/dockerfile:1
FROM python:3.11-slim

ENV PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    ASR_DEVICE=cpu \
    ASR_COMPUTE=int8 \
    CT2_NUM_THREADS=3 \
    OMP_NUM_THREADS=3 \
    MKL_NUM_THREADS=3

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir --no-compile -r requirements.txt

# 说话人分段声纹模型（3D-Speaker CAM++，约 27MB）构建时下载进镜像，
# GitHub 直连失败自动换镜像通道；下载失败不阻塞构建（运行时优雅降级为无说话人标注）。
RUN mkdir -p /app/models && python - <<'PYEOF'
import os, shutil, sys, urllib.request

dst = "/app/models/campplus.onnx"
urls = [
    "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/"
    "3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx",
    "https://gh-proxy.com/https://github.com/k2-fsa/sherpa-onnx/releases/download/"
    "speaker-recongition-models/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx",
]
for u in urls:
    try:
        print("downloading", u, flush=True)
        with urllib.request.urlopen(u, timeout=180) as r, open(dst, "wb") as f:
            shutil.copyfileobj(r, f)
        if os.path.getsize(dst) > 10_000_000:
            print("model ok:", os.path.getsize(dst), "bytes", flush=True)
            sys.exit(0)
        os.remove(dst)
    except Exception as e:
        print("download failed:", e, flush=True)
print("WARN: diarization model missing — speaker diarization disabled at runtime", flush=True)
PYEOF

COPY asr_server.py .
EXPOSE 8000
# 模型缓存挂载点（建议挂卷持久化，避免重复下载）
VOLUME /root/.cache

CMD ["python", "asr_server.py", "--host", "0.0.0.0", "--port", "8000"]
