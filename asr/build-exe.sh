#!/bin/bash
# voicvault-asr Linux 一键打包（PyInstaller，产物 dist/voicvault-asr/）
# 需要：Python 3.10/3.11 与网络；Ubuntu/Debian 建议 apt install python3.11-venv
set -e
cd "$(dirname "$0")"

python3 -m venv .venv-build
. .venv-build/bin/activate

python -m pip install --upgrade pip
pip install -r requirements.txt pyinstaller

pyinstaller --noconfirm --clean --distpath dist --workpath build asr_server.spec

mkdir -p dist/voicvault-asr/models
[ -f models/campplus.onnx ] && cp models/campplus.onnx dist/voicvault-asr/models/
chmod +x dist/voicvault-asr/voicvault-asr

echo
echo "打包完成: dist/voicvault-asr/voicvault-asr"
echo "运行测试: cd dist/voicvault-asr && ./voicvault-asr --host 0.0.0.0 --port 8000"
echo "浏览器打开 http://127.0.0.1:8000/ 应显示「运行正常」状态页"
