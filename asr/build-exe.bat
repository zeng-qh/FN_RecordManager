@echo off
REM ============================================================
REM  voicvault-asr Windows 一键打包（PyInstaller）
REM  需要：Python 3.10 / 3.11 64 位（python 命令可用，需联网装依赖）
REM  产物：dist\voicvault-asr\voicvault-asr.exe（整个目录随包分发）
REM  声纹模型：把 campplus.onnx 放到 dist\voicvault-asr\models\campplus.onnx
REM            （或执行 build-exe.sh 一样；也可运行 download_models.sh 下载）
REM ============================================================
cd /d %~dp0

where python >nul 2>nul || (echo [错误] 未找到 python，请先安装 Python 3.10/3.11 64 位 & exit /b 1)

python -m venv .venv-build || (echo [错误] 创建虚拟环境失败 & exit /b 1)
call .venv-build\Scripts\activate.bat

python -m pip install --upgrade pip
pip install -r requirements.txt pyinstaller || (echo [错误] 依赖安装失败 & exit /b 1)

pyinstaller --noconfirm --clean --distpath dist --workpath build asr_server.spec || (echo [错误] 打包失败 & exit /b 1)

if not exist dist\voicvault-asr\models mkdir dist\voicvault-asr\models
if exist models\campplus.onnx copy /y models\campplus.onnx dist\voicvault-asr\models\ >nul

echo.
echo ============================================================
echo  打包完成: dist\voicvault-asr\voicvault-asr.exe
echo  运行测试: cd dist\voicvault-asr && voicvault-asr.exe
echo  浏览器打开 http://127.0.0.1:8000/ 应显示「运行正常」状态页
echo ============================================================
