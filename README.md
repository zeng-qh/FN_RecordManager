# VoiceVault · 录音保险箱

把手机录音**自动备份到私有 NAS**，并支持**本地语音转文字**预览。数据不出你的设备，
跑在树莓派 4B / x86 上皆可，主打隐私优先。

> 背景：小米/华为/OPPO 等国产安卓的录音目录比较规范（小米为 `MIUI/sound_recorder/`，
> 多为 m4a）。本项目的目标，是像「相册备份」一样，把「录音」这条线补齐，形成完整的
> 手机数据备份矩阵。

## 架构

```
┌─────────────┐  增量同步(multipart+SHA256)   ┌──────────────────────────┐
│  手机端      │ ───────────────────────────▶ │  VoiceVault 服务端        │
│ 安卓App/     │                              │ (.NET 10 Minimal API)    │
│ Python上传器 │ ◀──────── 列表/播放/转写 ──── │  · 存储: /data/recordings │
└─────────────┘                              │  · 元数据: SQLite        │
                                             │  · 网页端: 内置单页 UI    │
                                             └───────────┬──────────────┘
                                                         │ 可选
                                             ┌───────────▼──────────────┐
                                             │  ASR 边车 (默认关闭)      │
                                             │  Python + faster-whisper  │
                                             │  本地推理，文字稿回写      │
                                             └──────────────────────────┘
```

## 组件

| 目录 | 说明 | 技术栈 |
|------|------|--------|
| `server/` | NAS 端服务：接收上传、SQLite 元数据、网页端、ASR 接口 | C# .NET 10 Minimal API |
| `client-cli/` | 跨平台上传器（电脑/树莓派/Termux 均可跑） | Python |
| `client-android/` | 安卓 App 源码（WorkManager 后台同步） | Kotlin |
| `asr/` | 可选本地语音转写边车 | Python + faster-whisper |
| `.github/workflows/` | 多架构镜像构建 + APK 构建 | GitHub Actions |

## 快速开始

### 1) 服务端（Docker，推荐）

```bash
echo "VAULT_TOKEN=你的强令牌" > .env
docker compose up -d
# 浏览器打开 http://<nas-ip>:8080 ，在设置里填入上面的令牌
```

镜像同时提供 `linux/amd64` 与 `linux/arm64`，树莓派 4B 与 x86 通用。
也可自行构建：`docker build -t voicvault-server ./server`。

环境变量：

| 变量 | 默认 | 说明 |
|------|------|------|
| `VAULT_TOKEN` | `dev-token-change-me` | 访问令牌（**务必修改**） |
| `VAULT_DATADIR` | `/data` | 录音/元数据/转写存储根目录（建议挂卷） |
| `VAULT_PORT` | `8080` | 监听端口 |
| `VAULT_BIND` | `0.0.0.0` | 监听地址 |
| `ASR__ENABLED` | `false` | 是否启用本地转写 |
| `ASR__ENDPOINT` | `http://localhost:8000` | ASR 边车地址 |
| `ASR__MODEL` | `small` | Whisper 模型规模 |
| `ASR__SpeakerDiarization` | `false` | 说话人分段 |

### 2) 上传器（不装 App 也能用）

```bash
pip install -r client-cli/requirements.txt
python3 client-cli/voicvault_sync.py \
    --server http://<nas-ip>:8080 --token 你的令牌 \
    --dir ~/MIUI/sound_recorder --once
# 守护模式：--daemon --interval 900
```

### 3) 安卓 App

源码在 `client-android/`，由 GitHub Actions 出 APK（手动触发，Artifacts 下载）；
或用 Android Studio 打开构建。详见该目录 `README.md`。

## API 概览（Bearer Token 鉴权）

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/health` | 探活（免鉴权） |
| POST | `/api/sync/diff` | 增量求差，返回待上传清单 |
| POST | `/api/upload` | 上传单个录音（校验 SHA-256） |
| GET | `/api/recordings` | 列表（分页/时间范围/关键词） |
| GET | `/api/recordings/{id}` | 详情 |
| GET | `/api/recordings/{id}/file` | 音频流（支持 Range，`?token=` 等价凭证） |
| GET/POST | `/api/recordings/{id}/transcript` `/transcribe` | 文字稿获取 / 触发转写 |
| GET/PUT | `/api/settings` | 读取 / 修改 ASR 设置 |

## 隐私
- 录音与转写文本只存你自己的 NAS，不经过任何第三方云。
- 转写（若启用）全程在 NAS 本地用 faster-whisper 推理。

## 路线图
- [x] **Phase 1** 自动备份：安卓 App + 上传器 + 服务端 + 网页端浏览/播放（已完成）
- [ ] **Phase 2** 本地转写：ASR 边车接入、文字稿展示、关键词检索、说话人分段、会议纪要摘要
- [ ] 微信/飞书类「录音文件」目录适配、更多机型录音路径识别

## 许可证
MIT（示例项目，生产使用前请自行评估安全与备份可靠性）。
