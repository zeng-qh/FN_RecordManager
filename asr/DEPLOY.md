# 转写边车独立部署指南

边车（voicvault-asr）是独立的本地转写服务，可以和主服务同机，也可以放在
局域网里另一台机器（甚至 Windows 电脑）上。**两种部署方式二选一**：

| 方式 | 适用场景 | 产物 |
|---|---|---|
| **Docker** | NAS / 服务器 / 树莓派，配 compose 一起管 | `ghcr.io/zeng-qh/voicvault-asr` 镜像（amd64 / arm64） |
| **二进制** | Windows 电脑、不想装 Docker 的机器、裸机直跑 | `voicvault-asr.exe` / `voicvault-asr`（PyInstaller onedir） |

部署完成后浏览器打开 `http://<边车主机>:8000/` 应显示状态页：
组件全绿「运行正常」，或红色「服务异常」+ 失败原因 + 修复建议。

---

## 方式一：Docker 部署（推荐 NAS / 服务器）

```bash
docker run -d --name voicvault-asr \
  -p 8000:8000 \
  -v ./asr-models:/root/.cache \
  --restart unless-stopped \
  ghcr.io/zeng-qh/voicvault-asr:latest
```

或直接用仓库根目录的 `docker-compose.yml`（`asr` 服务块已配好内存/线程限制）。
**注意**：仓库 compose 里边车默认不启动（`profiles: [asr]`），开启方式：

```bash
docker compose --profile asr up -d   # 主服务 + 边车一起启动
# 或主服务已在跑时单独补启边车：
docker compose up -d asr
```

启动边车后到网页设置页打开「语音转写」开关（默认关闭）。
声纹模型构建镜像时已内置，无需额外下载。

---

## 方式二：二进制部署（Windows / Linux）

### 1. 获取产物

**方式 A —— GitHub Actions 自动构建**（推荐）：
推标签 `sidecar-v1.1.0`（或手动触发 workflow）→ Actions 页面下载
`voicvault-asr-windows-x64` / `voicvault-asr-linux-x64`，或到 Releases 下 zip。
声纹模型已打进包内。

**方式 B —— 本地打包**：

```bat
REM Windows（需要 Python 3.10/3.11 64 位）
cd asr
build-exe.bat
REM 产物: dist\voicvault-asr\voicvault-asr.exe
```

```bash
# Linux
cd asr && ./build-exe.sh
# 产物: dist/voicvault-asr/voicvault-asr
```

### 2. 运行（Windows 示例）

```bat
cd dist\voicvault-asr
voicvault-asr.exe
```

默认监听 `0.0.0.0:8000`。自定义：

```bat
voicvault-asr.exe --host 0.0.0.0 --port 8000
```

浏览器打开 `http://127.0.0.1:8000/` 验证状态页。

### 3. 文件摆放约定

```
dist\voicvault-asr\          ← 程序目录（整个目录一起复制/搬家）
├─ voicvault-asr.exe         ← 主程序
├─ _internal\                ← 依赖（onedir 模式，勿单独拿出 exe）
└─ models\
   └─ campplus.onnx          ← 声纹模型（可选，缺失则不标注说话人）
```

模型查找顺序：环境变量 `ASR_DIARIZE_MODEL` → 程序旁 `models\campplus.onnx`
→ Docker 兜底路径。Whisper 模型首次转写时自动下载到 `~/.cache/huggingface`
（可用环境变量 `HF_HOME` 指到固定盘符，避免重装系统丢失）。

### 4. Windows 防火墙放行（局域网其他设备要访问时）

```bat
netsh advfirewall firewall add rule name="voicvault-asr" dir=in action=allow protocol=TCP localport=8000
```

### 5. Windows 开机自启（任务计划，隐藏窗口后台跑）

管理员 PowerShell：

```powershell
schtasks /Create /TN "voicvault-asr" /SC ONSTART /RU SYSTEM `
  /TR "D:\voicvault-asr\voicvault-asr.exe --host 0.0.0.0 --port 8000"
```

（或用 [NSSM](https://nssm.cc/) 注册为 Windows 服务，崩溃自动重启。）

Linux 下用 systemd：

```ini
# /etc/systemd/system/voicvault-asr.service
[Unit]
Description=VoiceVault ASR sidecar
After=network.target

[Service]
WorkingDirectory=/opt/voicvault-asr
ExecStart=/opt/voicvault-asr/voicvault-asr --host 0.0.0.0 --port 8000
Restart=always
RestartSec=3
MemoryMax=1G

[Install]
WantedBy=multi-user.target
```

`systemctl enable --now voicvault-asr`

---

## 接入主服务

主服务网页端「设置 → ASR Endpoint」填边车地址：

| 主服务位置 | Endpoint 填法 |
|---|---|
| 同一台机（容器化） | `http://asr:8000`（同 compose 栈）或 `http://host.docker.internal:8000` |
| 同一台机（二进制/裸跑） | `http://127.0.0.1:8000` |
| 局域网另一台机 | `http://<边车IP>:8000` |

声纹分离开关在「设置 → 说话人分段」，边车缺声纹模型时自动降级为不标注。

## 常见问题

- **状态页显示「服务异常」**：按页面给的修复建议操作（缺依赖/模型缺失都有对应提示）。
- **首次转写很慢**：Whisper 模型在下载（tiny ~75MB / small ~460MB），之后复用缓存。
- **Windows 杀毒软件误报 exe**：PyInstaller 打包的常见现象，加白名单即可。
- **2GB 小内存设备**：`VAULT_ASR__MODEL` 保持 tiny，不要用 small（OOM）。
