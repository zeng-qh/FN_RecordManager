"""
VoiceVault 本地语音转写边车（ASR）
=================================
基于 faster-whisper 的本地化语音转文字服务。数据不出 NAS，纯本地推理。
由 VoiceVault 服务端在「启用 ASR」时通过 HTTP 调用，平时可不启动。

端点：
  GET  /health
  POST /transcribe            同步转写（兼容旧客户端，长连接等完整结果）
      multipart: file=音频, model=模型规模, diarize=bool
      → { "text": "...", "segments": [{start,end,text,speaker?}], "language": "zh" }
  POST /transcribe-async      异步转写：立即返回，后台推理
      multipart: file=音频, model=模型规模, diarize=bool, task_id=str
      → { "task_id": "..." }
  GET  /transcribe-status/{task_id}   查询进度（轮询用）
      → { "status": "running|done|error", "progress": 0-100,
          "partial": "已转出的部分文本", "language": "zh",
          "segments": [...], "text": "...", "error": "..." }

运行：
  pip install -r requirements.txt
  python asr_server.py --host 0.0.0.0 --port 8000
或：
  docker build -t voicvault-asr . && docker run -p 8000:8000 -v $PWD/models:/root/.cache voicvault-asr
"""
import argparse
import av  # faster-whisper 依赖 PyAV；此处用于读取音频容器真实时长
import os
import tempfile
import threading
import time
import traceback

import uvicorn
from fastapi import FastAPI, File, Form, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

app = FastAPI(title="VoiceVault ASR")
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])

_model_cache = {}
_s2t_converter = None
# task_id -> {status, progress, partial, language, segments, text, error, finished_at}
_jobs: dict = {}
_jobs_lock = threading.Lock()
JOB_TTL_SEC = 600  # 完成后保留 10 分钟供补拉，过期由清理线程回收


def _janitor():
    """后台清理线程：定期回收过期任务，避免 _jobs 无界增长。"""
    while True:
        time.sleep(60)
        cutoff = time.time() - JOB_TTL_SEC
        with _jobs_lock:
            expired = [k for k, v in _jobs.items()
                       if v.get("finished_at") and v["finished_at"] < cutoff]
            for k in expired:
                _jobs.pop(k, None)


threading.Thread(target=_janitor, daemon=True).start()


def to_simplified(text: str) -> str:
    """繁体 → 简体（opencc t2s；对英文/数字等非中文内容恒等无损）。"""
    global _s2t_converter
    if _s2t_converter is None:
        from opencc import OpenCC

        _s2t_converter = OpenCC("t2s")
    return _s2t_converter.convert(text)


def get_model(model_size: str):
    """懒加载并缓存 Whisper 模型（首次会下载，之后复用）。"""
    if model_size in _model_cache:
        return _model_cache[model_size]
    from faster_whisper import WhisperModel

    # 树莓派 4B(arm64) 用 int8 量化更省内存；x86 可酌情调高
    device = os.environ.get("ASR_DEVICE", "cpu")
    compute_type = os.environ.get("ASR_COMPUTE", "int8")
    model = WhisperModel(model_size, device=device, compute_type=compute_type)
    _model_cache[model_size] = model
    return model


@app.get("/health")
def health():
    return {"status": "ok"}


def _run_job(task_id: str, tmp_path: str, model_size: str, diarize: bool):
    """后台线程：执行转写，逐段更新进度与部分文本。"""
    job = _jobs[task_id]

    def update(**kw):
        with _jobs_lock:
            job.update(kw)

    try:
        model_inst = get_model(model_size)
        update(status="running", progress=1)

        # 容器真实时长（含尾部静音）：faster-whisper 的 info.duration 在
        # vad_filter 开启时是「过滤掉静音后的时长」，尾部静音较长的录音
        # （如微信挂机忘停）会明显偏短，时长展示应以文件真实时长为准
        container_duration = 0.0
        try:
            with av.open(tmp_path) as c:
                container_duration = float(c.duration or 0.0) / 1_000_000  # 微秒→秒
        except Exception:
            container_duration = 0.0  # 读不出容器时长时由调用方回退末段 end

        segments, info = model_inst.transcribe(
            tmp_path, beam_size=5, vad_filter=True, language=None,
            # 引导普通话上下文：减少幻觉/方言偏置，简繁由后处理统一
            initial_prompt="以下是普通话的句子。",
        )
        zh = bool(info.language) and str(info.language).lower().startswith("zh")
        duration = float(getattr(info, "duration", 0.0) or 0.0)
        # vad_filter 开启时 info.duration = 过滤静音后保留的语音时长
        #（「有声音时间」）；容器真实时长见 container_duration（「总时长」）

        out_segments = []
        partial_parts = []
        speaker_idx = 0
        prev_end = 0.0
        for seg in segments:  # 惰性生成器：产出一段推理一段
            seg_text = seg.text.strip()
            if zh:
                seg_text = to_simplified(seg_text)
            seg_dict = {
                "start": round(seg.start, 2),
                "end": round(seg.end, 2),
                "text": seg_text,
            }
            # 简易说话人标注：仅作占位，真正分段需 pyannote（需 HF token）
            if diarize:
                if seg.start - prev_end > 1.5:  # 间隔较大视为换人
                    speaker_idx += 1
                seg_dict["speaker"] = f"说话人{speaker_idx + 1}"
                prev_end = seg.end
            out_segments.append(seg_dict)
            if seg_text:
                partial_parts.append(seg_text)

            # 进度按音频时间轴估算（VAD 过滤后末段可能略短于总时长）
            pct = min(99.0, (seg.end / duration * 100.0)) if duration > 0 else 0.0
            update(
                status="running",
                progress=round(max(pct, 1.0), 1),
                partial=" ".join(partial_parts),
                language=str(info.language or ""),
            )

        text = " ".join(s["text"] for s in out_segments)
        update(
            status="done",
            progress=100.0,
            partial=" ".join(partial_parts),
            language=str(info.language or ""),
            segments=out_segments,
            text=text,
            duration=container_duration,
            speech=duration,
            error=None,
        )
    except Exception:
        update(status="error", error=traceback.format_exc(limit=3))
    finally:
        try:
            os.remove(tmp_path)
        except OSError:
            pass
        with _jobs_lock:
            if task_id in _jobs:
                _jobs[task_id]["finished_at"] = time.time()  # 交由清理线程按 TTL 回收


@app.post("/transcribe")
def transcribe(
    file: UploadFile = File(...),
    model: str = Form("small"),
    diarize: bool = Form(False),
):
    """同步转写（兼容保留）：内部复用异步任务，长轮询至完成。"""
    task_id = f"sync-{time.time_ns()}"
    suffix = os.path.splitext(file.filename or "audio.m4a")[1] or ".m4a"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        tmp.write(file.file.read())
        tmp_path = tmp.name

    with _jobs_lock:
        _jobs[task_id] = {"status": "queued", "progress": 0.0, "partial": "",
                          "language": None, "segments": None, "text": None,
                          "duration": 0.0, "speech": 0.0, "error": None}
    threading.Thread(target=_run_job, args=(task_id, tmp_path, model, diarize), daemon=True).start()

    while True:
        time.sleep(0.5)
        with _jobs_lock:
            job = dict(_jobs.get(task_id) or {})
        if job.get("status") in ("done", "error"):
            with _jobs_lock:
                _jobs.pop(task_id, None)
            if job["status"] == "error":
                return JSONResponse(status_code=500, content={"error": job.get("error") or "asr failed"})
            return {
                "text": job.get("text") or "",
                "segments": job.get("segments") or [],
                "language": job.get("language"),
                "duration": job.get("duration", 0.0),
            "speech": job.get("speech", 0.0),
            }


@app.post("/transcribe-async")
def transcribe_async(
    file: UploadFile = File(...),
    model: str = Form("small"),
    diarize: bool = Form(False),
    task_id: str = Form(...),
):
    """异步转写：落盘音频后立即返回，后台线程推理，进度经 /transcribe-status 查询。"""
    suffix = os.path.splitext(file.filename or "audio.m4a")[1] or ".m4a"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        tmp.write(file.file.read())
        tmp_path = tmp.name

    with _jobs_lock:
        # 同任务重复提交：先清旧状态（调用方语义是重启该任务）
        _jobs[task_id] = {"status": "queued", "progress": 0.0, "partial": "",
                          "language": None, "segments": None, "text": None,
                          "duration": 0.0, "speech": 0.0, "error": None}
    threading.Thread(target=_run_job, args=(task_id, tmp_path, model, diarize), daemon=True).start()
    return {"task_id": task_id}


@app.get("/transcribe-status/{task_id}")
def transcribe_status(task_id: str):
    with _jobs_lock:
        job = _jobs.get(task_id)
        if job is None:
            return JSONResponse(status_code=404, content={"error": "task not found"})
        return {
            "status": job.get("status"),
            "progress": job.get("progress", 0.0),
            "partial": job.get("partial") or "",
            "language": job.get("language"),
            "segments": job.get("segments"),
            "text": job.get("text"),
            "duration": job.get("duration", 0.0),
            "speech": job.get("speech", 0.0),
            "error": job.get("error"),
        }


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8000)
    args = parser.parse_args()
    uvicorn.run(app, host=args.host, port=args.port)
