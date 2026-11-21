# PyInstaller 打包规范：voicvault-asr（Windows / Linux 通用）
# 打包命令（仓库任意目录执行均可，SPECPATH 自动定位）：
#   pyinstaller --noconfirm --clean --distpath dist --workpath build asr/asr_server.spec
# 产物：dist/voicvault-asr/voicvault-asr(.exe)（onedir 模式，随目录分发）
# 选 onedir 而非 onefile：依赖含 CTranslate2/PyAV/sherpa-onnx 共约 300MB，
# onefile 每次启动都要解压到临时目录，服务型进程用 onedir 启动更快、也更省盘。
import os
from PyInstaller.utils.hooks import collect_all, collect_dynamic_libs

datas = []
binaries = collect_dynamic_libs("ctranslate2") + collect_dynamic_libs("sherpa_onnx") + collect_dynamic_libs("av")
hiddenimports = []

# 重型依赖整包收集：faster-whisper/ctranslate2 有动态库与隐式导入，
# sherpa-onnx 的原生库、av 的编解码器 dll、opencc 的数据文件都需带进包里
for _pkg in ("faster_whisper", "ctranslate2", "sherpa_onnx", "av", "opencc",
             "huggingface_hub", "tokenizers"):
    _d, _b, _h = collect_all(_pkg)
    datas += _d
    binaries += _b
    hiddenimports += _h

a = Analysis(
    [os.path.join(SPECPATH, "asr_server.py")],
    pathex=[SPECPATH],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    runtime_hooks=[],
    excludes=["torch", "torchvision", "tkinter", "matplotlib", "PyQt5",
              "onnxruntime", "pandas", "scipy"],  # 未用到的大件全部排除
    noarchive=False,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="voicvault-asr",
    console=True,   # 服务型进程保留控制台便于看启动日志（Windows 下可用任务计划隐藏窗口）
    upx=False,      # UPX 对大型 .so/.dll 压缩易出兼容问题，不启用
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    name="voicvault-asr",
)
