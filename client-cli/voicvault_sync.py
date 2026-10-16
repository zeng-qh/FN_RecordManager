#!/usr/bin/env python3
"""
VoiceVault 跨平台上传器
======================
扫描本地录音目录（默认小米 MIUI/sound_recorder/），增量同步到 VoiceVault 服务端。

特性：
  - 增量：先调 /api/sync/diff，仅上传服务端缺失的文件（按 SHA-256 去重）
  - 断点友好：重复运行只会补传新文件
  - 跨平台：电脑、树莓派、Android Termux 均可运行
  - 守护模式：--daemon 按间隔轮询

用法示例：
  python3 voicvault_sync.py --server http://192.168.1.10:8080 --token YOUR_TOKEN \\
      --dir ~/MIUI/sound_recorder --once
  python3 voicvault_sync.py --server https://nas.local --token $VAULT_TOKEN \\
      --dir /sdcard/MIUI/sound_recorder --daemon --interval 900
"""
import argparse
import hashlib
import os
import sys
import time

try:
    import requests
except ImportError:
    sys.exit("缺少依赖，请先执行：pip install -r requirements.txt")

SUPPORTED_EXT = (".m4a", ".mp3", ".wav", ".flac", ".ogg", ".aac", ".amr")


def sha256_of(path: str, chunk: int = 1 << 20) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(chunk), b""):
            h.update(block)
    return h.hexdigest()


def scan(dir_path: str):
    """返回 [(rel_path, abs_path, size, mtime)]，rel_path 使用 / 分隔。"""
    result = []
    for root, _dirs, files in os.walk(dir_path):
        for name in files:
            if not name.lower().endswith(SUPPORTED_EXT):
                continue
            abs_path = os.path.join(root, name)
            rel = os.path.relpath(abs_path, dir_path).replace(os.sep, "/")
            try:
                st = os.stat(abs_path)
                result.append((rel, abs_path, st.st_size, int(st.st_mtime)))
            except OSError:
                continue
    return result


def diff(server: str, token: str, device: str, files):
    payload = {
        "device": device,
        "files": [
            {"relPath": r, "size": s, "mtime": m, "sha256": h}
            for (r, _p, s, m, h) in files
        ],
    }
    r = requests.post(
        server.rstrip("/") + "/api/sync/diff",
        json=payload,
        headers={"Authorization": "Bearer " + token},
        timeout=60,
    )
    r.raise_for_status()
    return r.json()


def upload(server: str, token: str, device: str, rel: str, abs_path: str, size: int, mtime: int):
    with open(abs_path, "rb") as f:
        files = {"file": (os.path.basename(rel), f, "application/octet-stream")}
        data = {
            "relPath": rel,
            "device": device,
            "size": str(size),
            "mtime": str(mtime),
            "sha256": sha256_of(abs_path),
        }
        r = requests.post(
            server.rstrip("/") + "/api/upload",
            data=data,
            files=files,
            headers={"Authorization": "Bearer " + token},
            timeout=600,
        )
        r.raise_for_status()
        return r.json()


def run_once(server: str, token: str, dir_path: str, device: str):
    print(f"[scan] 扫描目录：{dir_path}")
    entries = scan(dir_path)
    if not entries:
        print("[scan] 未发现支持的音频文件。")
        return 0, 0

    files = [(r, p, s, m, sha256_of(p)) for (r, p, s, m) in entries]
    print(f"[scan] 共发现 {len(files)} 个文件，正在向服务端求差…")

    resp = diff(server, token, device, files)
    to_upload = set(resp.get("toUpload", []) or resp.get("ToUpload", []))
    server_has = resp.get("serverHas", []) or resp.get("ServerHas", [])
    print(f"[diff] 服务端已存在 {len(server_has)} 个，待上传 {len(to_upload)} 个。")

    ok = 0
    fail = 0
    for (rel, abs_path, size, mtime, _h) in files:
        if rel not in to_upload:
            continue
        try:
            upload(server, token, device, rel, abs_path, size, mtime)
            ok += 1
            print(f"[up  ] ✅ {rel} ({size/1024/1024:.2f} MB)")
        except Exception as e:  # noqa: BLE001
            fail += 1
            print(f"[up  ] ❌ {rel} 失败：{e}")

    print(f"[done] 上传成功 {ok} 个，失败 {fail} 个。")
    return ok, fail


def main():
    ap = argparse.ArgumentParser(description="VoiceVault 录音增量备份上传器")
    ap.add_argument("--server", default=os.environ.get("VAULT_SERVER", "http://localhost:8080"),
                    help="服务端地址，或环境变量 VAULT_SERVER")
    ap.add_argument("--token", default=os.environ.get("VAULT_TOKEN", ""),
                    help="访问令牌，或环境变量 VAULT_TOKEN")
    ap.add_argument("--dir", required=True, help="待备份的录音目录（如 MIUI/sound_recorder）")
    ap.add_argument("--device", default=os.environ.get("VAULT_DEVICE", "phone"),
                    help="设备标识，默认 phone")
    ap.add_argument("--once", action="store_true", help="只同步一次后退出")
    ap.add_argument("--daemon", action="store_true", help="守护模式，按间隔循环")
    ap.add_argument("--interval", type=int, default=900, help="守护模式间隔秒数，默认 900")
    args = ap.parse_args()

    if not args.token:
        ap.error("必须提供 --token（或设置环境变量 VAULT_TOKEN）")
    if not os.path.isdir(args.dir):
        ap.error(f"目录不存在：{args.dir}")

    if args.once or not args.daemon:
        run_once(args.server, args.token, args.dir, args.device)
        return

    print(f"[daemon] 每 {args.interval}s 同步一次，Ctrl+C 退出。")
    try:
        while True:
            run_once(args.server, args.token, args.dir, args.device)
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("\n[daemon] 已停止。")


if __name__ == "__main__":
    main()
