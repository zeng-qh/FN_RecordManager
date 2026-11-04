# 本地语音转写边车：多架构（amd64 / arm64）
# faster-whisper 基于 CTranslate2（非 PyTorch），slim 基底 + int8 量化已较精简；
# alpine/musl 无官方 wheel，勿改。首次运行会按需下载 Whisper 模型（建议挂卷缓存）。
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

COPY asr_server.py .
EXPOSE 8000
# 模型缓存挂载点（建议挂卷持久化，避免重复下载）
VOLUME /root/.cache

CMD ["python", "asr_server.py", "--host", "0.0.0.0", "--port", "8000"]
