#!/bin/bash
# 主服务守护：进程退出后 3 秒自动拉起（沙箱休眠回收后自愈）
cd /workspace/voicvault/server || exit 1
while true; do
  echo "[vv-guard] $(date '+%F %T') starting VoiceVault.Server on :8080"
  VAULT_DATADIR=/workspace/vv-demo-data VAULT_TOKEN=demo-token-2026 \
    VAULT_ASR__ENDPOINT=http://127.0.0.1:8001 VAULT_PORT=8080 \
    dotnet bin/Debug/net10.0/VoiceVault.Server.dll
  echo "[vv-guard] $(date '+%F %T') exited (code=$?), restart in 3s"
  sleep 3
done
