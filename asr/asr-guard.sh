#!/bin/bash
# ASR 边车守护：进程退出后 3 秒自动拉起（演示环境保活）
cd /workspace/voicvault/asr || exit 1
export HF_ENDPOINT=https://hf-mirror.com
while true; do
  echo "[asr-guard] $(date '+%F %T') starting asr_server on :8000"
  python3.11 asr_server.py --host 127.0.0.1 --port 8000
  echo "[asr-guard] $(date '+%F %T') asr exited (code=$?), restart in 3s"
  sleep 3
done
