#!/bin/bash
# 二层守护：监测 asr-guard.sh 是否存活，死了就重新拉起（独立进程组）。
# 第一层 guard 负责 python 崩溃 3 秒内重启；本层负责 guard 自身被回收后 30 秒内复活。
while true; do
  if ! pgrep -f "asr-guard.s[h]" >/dev/null; then
    echo "[asr-watchdog] $(date '+%F %T') guard missing, restarting"
    (cd /workspace/voicvault/asr && setsid nohup bash asr-guard.sh >> /tmp/asr-guard.log 2>&1 < /dev/null &)
  fi
  sleep 30
done
