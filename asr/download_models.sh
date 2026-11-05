#!/bin/bash
# 下载说话人分段声纹模型（3D-Speaker CAM++，约 27MB）。
# Docker 镜像构建时已内置；本地裸跑边车（pip install -r requirements.txt）时执行本脚本。
# 缺失时边车可正常转写，但说话人分段优雅降级为「不标注说话人」。
set -e
DIR="$(cd "$(dirname "$0")" && pwd)/models"
mkdir -p "$DIR"
OUT="$DIR/campplus.onnx"

URLS=(
  "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx"
  "https://gh-proxy.com/https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx"
)

for u in "${URLS[@]}"; do
  echo "downloading: $u"
  if curl -fsSL --retry 2 --max-time 600 -o "$OUT" "$u" \
     && [ "$(stat -c%s "$OUT" 2>/dev/null || echo 0)" -gt 10000000 ]; then
    echo "saved: $OUT ($(du -h "$OUT" | cut -f1))"
    exit 0
  fi
  rm -f "$OUT"
done

echo "download failed：所有通道均不可用。说话人分段将不可用（转写不受影响）。" >&2
exit 1
