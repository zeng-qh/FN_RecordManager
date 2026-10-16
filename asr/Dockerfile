# 本地语音转写边车：多架构（amd64 / arm64）
# 注意：faster-whisper 依赖 PyTorch，镜像较大；首次运行会按需下载 Whisper 模型。
FROM python:3.11-slim

ENV PYTHONUNBUFFERED=1 \
    ASR_DEVICE=cpu \
    ASR_COMPUTE=int8

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY asr_server.py .
EXPOSE 8000
# 模型缓存挂载点（建议挂卷持久化，避免重复下载）
VOLUME /root/.cache

CMD ["python", "asr_server.py", "--host", "0.0.0.0", "--port", "8000"]
