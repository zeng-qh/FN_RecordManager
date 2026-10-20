# VoiceVault · 录音保险箱

把手机录音**自动备份到私有 NAS**，并支持**本地语音转文字**预览（左播放 / 右文字稿）。
数据不出你的设备，跑在树莓派 4B / x86 上皆可，主打隐私优先。

> 背景：小米/华为/OPPO 等国产安卓的录音目录比较规范（小米为 `MIUI/sound_recorder/`，
> 多为 m4a）。本项目的目标，是像「相册备份」一样，把「录音」这条线补齐，形成完整的
> 手机数据备份矩阵。

## 架构

```
┌─────────────┐  增量同步(multipart+SHA256)   ┌──────────────────────────┐
│  手机端      │ ───────────────────────────▶ │  VoiceVault 服务端        │
│ 安卓App/     │                              │ (.NET 10 Minimal API)    │
│ Python上传器 │ ◀──── 列表/播放/文字稿 ────── │  · 存储: /data/recordings │
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
| `server/` | NAS 端服务：接收上传、SQLite 元数据、网页端、后台转写队列 | C# .NET 10 Minimal API |
| `client-cli/` | 跨平台上传器（电脑/树莓派/Termux 均可跑） | Python |
| `client-android/` | 安卓 App 源码（WorkManager 后台同步） | Kotlin |
| `asr/` | 可选本地语音转写边车 | Python + faster-whisper |
| `.github/workflows/` | 多架构镜像构建 + APK 构建 | GitHub Actions |

---

## 一、服务端部署（NAS，任选其一）

### 方式 A：Docker Compose（推荐）

```bash
echo "VAULT_TOKEN=你的强令牌" > .env
docker compose up -d
# 浏览器打开 http://<nas-ip>:8080 ，在设置里填入上面的令牌
```

### 方式 B：docker run

```bash
docker run -d --name voicvault \
  -e VAULT_TOKEN=你的强令牌 \
  -v voicvault-data:/data \
  -p 8080:8080 \
  --restart unless-stopped \
  ghcr.io/zeng-qh/voicvault-server:latest
```

镜像同时提供 `linux/amd64` 与 `linux/arm64`，**树莓派 4B 与 x86 通用**。
也可自行构建：`docker build -t voicvault-server ./server`。

### 方式 C：裸机 .NET（Windows / Linux 直接跑）

```bash
# 需要 .NET 10 SDK（或发布后的自包含产物）
cd server
VAULT_TOKEN=你的强令牌 VAULT_DATADIR=/data/voicvault dotnet run -c Release
# Windows PowerShell: $env:VAULT_TOKEN="你的强令牌"; dotnet run -c Release
```

### 方式 D：systemd 常驻（Linux 无 Docker）

```ini
# /etc/systemd/system/voicvault.service
[Unit]
Description=VoiceVault recording server
After=network.target

[Service]
Environment=VAULT_TOKEN=你的强令牌
Environment=VAULT_DATADIR=/data/voicvault
WorkingDirectory=/opt/voicvault/server
ExecStart=/usr/bin/dotnet /opt/voicvault/server/VoiceVault.Server.dll
Restart=always

[Install]
WantedBy=multi-user.target
```

`systemctl enable --now voicvault` 即可。

### 环境变量（所有部署方式通用）

| 变量 | 默认 | 说明 |
|------|------|------|
| `VAULT_TOKEN` | `dev-token-change-me` | 访问令牌（**务必修改**） |
| `VAULT_DATADIR` | `/data` | 录音/元数据/转写存储根目录（建议挂卷） |
| `VAULT_PORT` | `8080` | 监听端口 |
| `VAULT_BIND` | `0.0.0.0` | 监听地址 |
| `VAULT_ASR__ENABLED` | `false` | 是否启用本地转写 |
| `VAULT_ASR__MODE` | `Off` | 转写时机：`Off` 关闭 / `Realtime` 实时 / `Idle` 闲时 |
| `VAULT_ASR__IDLESTARTHOUR` | `22` | 闲时窗口起始小时（0–23，本地时区） |
| `VAULT_ASR__IDLEENDHOUR` | `7` | 闲时窗口结束小时（0–23，可小于起始=跨午夜） |
| `VAULT_ASR__ENDPOINT` | `http://localhost:8000` | ASR 边车地址 |
| `VAULT_ASR__MODEL` | `small` | Whisper 模型规模 |
| `VAULT_ASR__SPEAKERDIARIZATION` | `false` | 说话人分段 |

> **设置持久化**：网页端「设置」保存的 ASR 配置会写入 `<DataDir>/settings.json`，
> 重启容器/进程不丢；存在该文件时，其中 ASR 部分优先于上面的环境变量默认值。

---

## 树莓派 4B（2GB 内存）优化部署

本项目针对 2GB 内存的小设备做了针对性优化，开箱即可在树莓派 4B 上稳定运行。

### 内存预算（仅运行服务端，不开转写）

| 组件 | 常驻内存 | 说明 |
|---|---|---|
| 服务端 .NET（空闲） | ~50–80 MB | GC 堆硬上限约 600 MB（见下） |
| 上传 / 播放峰值 | +50–150 MB | 全程流式处理，不把整段录音读进内存 |
| 系统 + fnOS | ~400–600 MB | 树莓派 OS 基础占用 |
| ASR 边车（默认关闭） | 0 | 不启动就不占 |
| **合计（仅服务端）** | **~600–800 MB** | 2GB 设备仍有约 1.2GB 余量 |

> 一旦开启转写（tiny + int8），边车会再占 ~300–500 MB，**small/medium 模型在 2GB 上基本会 OOM，切勿使用**。

### 已内置的优化（无需额外配置）

1. **GC 堆硬上限 + Workstation GC**：镜像内已设 `DOTNET_GCHeapHardLimitPercent=70`、`DOTNET_gcServer=0`，小内存设备不会超额分配堆内存。
2. **全球化 invariant 模式**：`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` 省下 ICU 库约 20–30 MB；不影响中文录音名、转写文本的存储与 `LIKE` 搜索（已实测）。
3. **SQLite WAL 模式**：写不阻塞读、崩溃恢复更安全；容器 `stop`（SIGTERM）时自动 checkpoint，数据零丢失（已实测 `integrity_check = ok`）。
4. **流式上传 / 转写**：长录音按流读取落盘，修掉了原先把整段音频读进内存的隐患，从根上避免大文件 OOM。
5. **大文件上传上限提到 2GB**（原默认仅 134 MB，长录音会被拒）。
6. **ASR 边车 int8 量化 + 线程限制**：`CT2_NUM_THREADS/OMP/MKL = 3`，避免打满四核、减少内存争抢。

### 部署要点

- 默认 `mem_limit: 896m`，与 GC 堆上限配合，OOM 风险极低。
- ASR 边车默认**不启动**，需要转写时：

  ```bash
  docker compose --profile asr up -d
  ```

  然后在网页端「设置」里打开 ASR，并选择「实时转写」或「闲时转写」即可。
- 树莓派上 `VAULT_ASR_MODEL` 保持 `tiny`（compose 已默认）；x86 设备可改 `small`/`medium`。
- **小内存设备强烈推荐「闲时转写」**：设置一个空闲时段（如 22→7），
  白天只备份不转写，夜里自动消化——转写时的 CPU/内存峰值不会撞上日常使用，
  也给系统留出充足余量，进一步降低 OOM 风险。

### 优雅关闭

`docker stop` 会向进程发 SIGTERM，服务端已配置：
- `ShutdownTimeout = 30s`（默认仅 5s，长转写会被强杀）；
- 停止时先排空转写队列、取消进行中的任务，再释放 SQLite 连接；
- 日志会打印 `Application stopping… / stopped`，方便确认资源已正确释放。

---

## 二、手机端接入（任选其一）

### 方式 A：安卓 App（推荐，全自动）

功能：SAF 授权录音目录、每 15 分钟后台增量同步、SHA-256 去重、通知栏进度、
**断网自动退避重试**（网络恢复后继续上传）。

1. **获取 APK**：GitHub Actions（`.github/workflows/android.yml`）构建后在
   Artifacts 下载 `app-release-unsigned.apk`，用你的签名密钥
   （`apksigner sign --ks xxx.keystore ...`）签名后安装；
   或用 Android Studio 打开 `client-android/` 自行构建。
2. **配置**：打开 App → 填服务器地址与令牌 → 「选择录音目录」选中
   `MIUI/sound_recorder` → 「保存并启动自动同步」。

### 方式 B：Termux + Python 上传器（不装 App）

```bash
# Termux 中：允许访问手机存储
termux-setup-storage
pkg install python
pip install requests

python3 voicvault_sync.py \
    --server http://<nas-ip>:8080 --token 你的令牌 \
    --dir /sdcard/MIUI/sound_recorder \
    --daemon --interval 900
```

### 各机型录音目录参考

| 机型 | 常见录音目录 |
|------|--------------|
| 小米 / 红米 | `MIUI/sound_recorder/`（通话录音在 `MIUI/sound_recorder/call/`） |
| 华为 / 荣耀 | `Recordings/`（通话录音 `Recordings/CallRecord/`） |
| OPPO / 一加 | `Recordings/` |
| vivo / iQOO | `Recordings/` |
| 三星 | `Recordings/Voice/` |

App 里通过 SAF 选中对应目录即可；上传器则把 `--dir` 指向该路径。

### 方式 C：电脑 / 树莓派接入

把录音拷贝（或同步盘落盘）到电脑/树莓派某个目录后，用上传器备份：

```bash
pip install -r client-cli/requirements.txt
python3 client-cli/voicvault_sync.py \
    --server http://<nas-ip>:8080 --token 你的令牌 \
    --dir /path/to/recordings --once
# 守护模式：--daemon --interval 900
```

### 断网重传机制（上传器 / 安卓 App 均具备）

- **单文件重试**：上传失败按指数退避重试（默认 3 次：3s→6s→12s，
  `--retries` / `--retry-wait` 可调）；4xx 鉴权/参数错误不重试。
- **断网等待**：守护模式启动每轮前先探活服务端，断网时阻塞等待，
  **网络恢复后自动续传**，不丢文件。
- **快速补传**：守护模式本轮有失败时，60s 后（`--retry-interval`）立即重传，
  而不是等满整个同步间隔。
- **兜底去重**：所有传输按 SHA-256 判重，重传已存在的文件会被服务端自动跳过。

---

## 三、网页端

浏览器打开 `http://<nas-ip>:8080`（首次在设置里填令牌）：

- **时间线浏览**：按日期分组，自动分页加载。
- **搜索**：按文件名/标题实时过滤。
- **网页上传**：顶栏 ⬆️ 直接选择（可多选）音频文件上传，无需命令行；
  SHA-256 由服务端流式计算，重复文件按 `(device, sha256)` 自动去重。
- **删除录音**：详情页「🗑 删除录音」，录音文件与文字稿一并清理（需确认）。
- **快速播放**：点 ▶ 在底部播放条播放，支持拖拽进度（HTTP Range）。
- **录音详情**：点击任意条目打开详情页 —— **左侧播放器**（大按钮、进度时长、
  文件信息、下载原文件），**右侧文字稿**（分段显示、点击文字跳播对应位置、
  一键复制全文）。窄屏自动上下堆叠。
- **转写状态**：列表徽标显示 未转写/待闲时/转写中/已转写/转写失败；
  详情页转写完成后自动刷新。
- **设置持久化**：ASR/转写时机等设置保存到 `settings.json`，重启不丢。

## 四、本地语音转文字（ASR，可选）

1. **部署 ASR 边车**（在 NAS 上）：

```bash
cd asr
docker build -t voicvault-asr .
docker run -d --name voicvault-asr -p 8000:8000 --restart unless-stopped \
  -v voicvault-asr-models:/root/.cache voicvault-asr
```

2. **网页端设置里开启**：「启用语音转文字」→ 填边车地址（默认
   `http://localhost:8000`）→ 选模型规模 → 选择「转写时机」→ 保存。
3. **转写时机（三种模式，用户自定）**：
   - **关闭**：不自动转写，仅在你点详情页「立即转写」时手动触发。
   - **实时转写**：每个上传完成的录音立即进入后台队列（串行执行，不打满 NAS 资源）。
   - **闲时转写（推荐给树莓派等小内存设备）**：你设定一个空闲时段
     （如 `22:00 → 次日 07:00`，按服务器本地时区）。上传时只备份、不转写，
     标记为「待闲时」；后台调度器每分钟巡检，**仅在空闲时段内**把这些录音
     批量入队转写。白天 NAS 几乎零转写负载，夜里自动消化积压。
     > 已在进行中的转写会自然收尾，不会强行中断；时段结束只是停止**启动新任务**。
4. **手动触发**：任何模式下，详情页点「立即转写」都会马上执行（不受闲时窗口限制）；
   设置页「转写全部未处理」可把历史积压一次性入队。
5. 转写全程在 NAS 本地推理（faster-whisper），**文本不出你的机器**。

**「先保存、闲时再转写」典型流程**（闲时模式，窗口 22→7）：

```
白天 14:00  手机自动备份 10 条新录音  → 立即落盘保存，状态标记「待闲时」
白天 全天   浏览/播放/搜索全部可用，NAS 几乎零转写负载
夜里 22:00  闲时窗口开启            → 调度器自动把积压批量入队，串行转写
次日 07:00  窗口关闭                → 停止启动新任务（进行中的自然收尾）
早上打开    新录音文字稿已就绪，详情页直接阅读
```

> 环境变量亦可预设：`VAULT_ASR__MODE=Off|Realtime|Idle`、
> `VAULT_ASR__IDLESTARTHOUR`、`VAULT_ASR__IDLEENDHOUR`（小时 0–23，可跨午夜）。
> 网页端保存的设置持久化在 `<DataDir>/settings.json`，重启不丢、优先于环境变量。

## API 概览（Bearer Token 鉴权）

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/health` | 探活（免鉴权） |
| POST | `/api/sync/diff` | 增量求差，返回待上传清单 |
| POST | `/api/upload` | 上传单个录音（校验 SHA-256；按转写时机决定立即入队或标记待闲时） |
| GET | `/api/recordings` | 列表（分页/时间范围/关键词） |
| GET | `/api/recordings/{id}` | 详情 |
| DELETE | `/api/recordings/{id}` | 删除录音（含落盘文件与文字稿） |
| GET | `/api/recordings/{id}/file` | 音频流（支持 Range，`?token=` 等价凭证） |
| GET | `/api/recordings/{id}/transcript` | 文字稿（未就绪返回 404 + 状态，状态含 `idle` 待闲时） |
| POST | `/api/recordings/{id}/transcribe` | 立即触发转写（进后台队列，不受闲时窗口限制） |
| POST | `/api/transcribe/pending` | 批量转写：全部未完成录音一次性入队 |
| GET/PUT | `/api/settings` | 读取 / 修改 ASR 设置（`mode`、`idleStartHour` 等；PUT 落盘持久化） |

## 隐私

- 录音与转写文本只存你自己的 NAS，不经过任何第三方云。
- 转写（若启用）全程在 NAS 本地用 faster-whisper 推理。

## 路线图

- [x] **Phase 1** 自动备份：安卓 App + 上传器 + 服务端 + 网页端浏览/播放
- [x] **Phase 1.5** 断网重传、详情页（左播放/右文字稿）、后台自动转写
- [x] **Phase 1.8** 转写时机（关闭/实时/闲时）、网页上传、删除录音、
      设置持久化、批量转写、去重孤儿文件清理
- [ ] **Phase 2** 转写增强（见下方建议清单）
- [ ] 微信/飞书类「录音文件」目录适配、更多机型录音路径识别

## 建设性建议（按优先级）

**高价值 · 建议下一步做**

1. **文字稿全文检索（SQLite FTS5）**：目前搜索只匹配文件名/标题；
   建转写文本的 FTS5 虚表后，可直接搜「会议里说的那个词」并跳转到对应片段，
   是录音管理工具最有用的一步。
2. **录音时长元数据**：转写时顺带把总时长写库，列表/详情直接显示
   `03:25`，也方便按「时长 > 30 分钟」筛选。
3. **转写失败自动重试**：记录 attempts，闲时窗口内对 error 状态最多自动重试
   2 次（边车临时不可用时无需手动重试）。
4. **APK 签名流水线**：CI 增加 secrets 注入 keystore 的签名步骤，或直接产出
   可侧载的 `app-debug.apk`，省去手动 apksigner。

**体验增强 · 中优先级**

5. **转写完成通知**：Webhook / ntfy / Bark 推送「今日 N 条转写完成」。
6. **导出文字稿**：详情页一键导出 `.txt` / `.srt`（分段时间戳现成）。
7. **保留策略**：按天数/容量自动清理最旧录音（NAS 空间守护）。
8. **备注与重命名**：详情页编辑标题/备注，搜索权重最高。

**进阶 · 低优先级**

9. **会议纪要摘要**：转写文本接本地 LLM（Ollama）生成摘要与待办。
10. **通话/普通录音分类**：识别 `MIUI/sound_recorder/call/` 子目录打标签。
11. **只读分享链接**：带过期时间的单条录音+文字稿分享（家庭场景）。
12. **WorkManager 约束**：安卓端加「仅 WiFi / 充电时同步」开关。

## 许可证

MIT（示例项目，生产使用前请自行评估安全与备份可靠性）。
