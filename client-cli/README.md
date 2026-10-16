# VoiceVault 上传器（跨平台）

扫描本地录音目录并增量同步到 VoiceVault 服务端。可在电脑、树莓派、Android Termux 中运行。

## 安装

```bash
pip install -r requirements.txt
```

## 用法

```bash
# 一次性同步（小米录音目录示例）
python3 voicvault_sync.py \
    --server http://192.168.1.10:8080 \
    --token YOUR_TOKEN \
    --dir ~/MIUI/sound_recorder \
    --once

# 守护模式：每 15 分钟自动同步一次
python3 voicvault_sync.py \
    --server https://nas.local \
    --token $VAULT_TOKEN \
    --dir /sdcard/MIUI/sound_recorder \
    --daemon --interval 900
```

也可通过环境变量传入：`VAULT_SERVER` / `VAULT_TOKEN` / `VAULT_DEVICE`。

## 工作原理

1. 递归扫描目录，收集音频文件（`.m4a/.mp3/.wav/.flac/.ogg/.aac/.amr`）。
2. 计算每个文件的 SHA-256、大小、修改时间。
3. 调 `POST /api/sync/diff` 求差，服务端按 `(device, sha256)` 去重。
4. 仅上传缺失文件，重复运行只补传增量。

## 小米手机目录

小米系统录音机默认保存在 `MIUI/sound_recorder/`（Android 11+ 多在
`/storage/emulated/0/MIUI/sound_recorder/`）。在 Termux 中可直接访问该路径。
若系统限制存储权限，可用系统"文件"App 把录音目录复制到 Termux 可访问位置，或用 SAF 授权。
