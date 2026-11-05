"""
VoiceVault 本地语音转写边车（ASR）
=================================
基于 faster-whisper 的本地化语音转文字服务。数据不出 NAS，纯本地推理。
由 VoiceVault 服务端在「启用 ASR」时通过 HTTP 调用，平时可不启动。

端点：
  GET  /health
      → { "status": "ok", "diarize": true|false }  diarize=声纹模型是否就绪
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

说话人分段（diarize=true 时）：
  对每个转写段用 3D-Speaker CAM++ 声纹模型提取 192 维 embedding，
  再做凝聚聚类（平均链接 + 余弦相似度），按各说话人首次发言顺序标注
  「说话人N」。模型缺失时优雅降级：不输出 speaker 字段（不再给出
  按停顿猜测的虚假编号）。若声纹聚出的簇数收敛为 1（电话/免提通话
  双方共用信道压缩，频谱声纹可能不可分），自动回退用段级基频 F0
  （音高）二分——男女对话 F0 差通常 >30%，仅在分离度显著时才采用。
  模型文件默认找 ./models/campplus.onnx，可用环境变量覆盖：
    ASR_DIARIZE_MODEL        声纹模型 onnx 路径
    ASR_DIARIZE_THRESHOLD    聚类合并阈值（同人余弦越高，默认 0.65）
    ASR_DIARIZE_MAX_SPEAKERS 说话人数封顶，超出强制合并最相似簇（默认 8）
    ASR_DIARIZE_F0_DELTA     F0 回退的最小 log 音高差（默认 0.15 ≈ 两簇
                             中位音高比 1.16x，低于此视为同一人不硬分）

运行：
  pip install -r requirements.txt
  python asr_server.py --host 0.0.0.0 --port 8000
或：
  docker build -t voicvault-asr . && docker run -p 8000:8000 -v $PWD/models:/root/.cache voicvault-asr
"""
import argparse
import av  # faster-whisper 依赖 PyAV；此处用于读取音频容器真实时长/解码声纹样本
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


# ---- 说话人分段（声纹 embedding + 聚类）----
# 声纹模型（3D-Speaker CAM++，192 维，27MB）为可选项：文件缺失时 diarize
# 优雅降级为「不标注说话人」，转写本身不受影响。
_speaker_extractor = None
_speaker_extractor_lock = threading.Lock()

# 模型路径解析顺序：环境变量 → 脚本目录 models/ → /app/models/（Docker 内）
_DIARIZE_MODEL_CANDIDATES = [
    os.environ.get("ASR_DIARIZE_MODEL") or "",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "models", "campplus.onnx"),
    "/app/models/campplus.onnx",
]
_DIARIZE_THRESHOLD = float(os.environ.get("ASR_DIARIZE_THRESHOLD", "0.42"))
_DIARIZE_MAX_SPEAKERS = int(os.environ.get("ASR_DIARIZE_MAX_SPEAKERS", "8"))
# F0 回退分裂的最小分离度（log 域两簇均值差）：0.15 ≈ 两簇中位音高比 1.16x。
# 男女对话典型差 0.3+（比值 1.35x+）；单人语调波动切开通常 <0.12。
_DIARIZE_F0_DELTA = float(os.environ.get("ASR_DIARIZE_F0_DELTA", "0.15"))
# 单段时长下限：过短（感叹词/抢话）的段声纹不可靠，跳过后继承前段标签
_DIARIZE_MIN_SEG_SEC = 0.8
# 解码音频上限（秒）：声纹分段需整条解码进内存，超长录音不标注（转写不受影响）
_DIARIZE_MAX_AUDIO_SEC = 6 * 3600


def _diarize_model_path() -> str:
    for p in _DIARIZE_MODEL_CANDIDATES:
        if p and os.path.isfile(p) and os.path.getsize(p) > 1_000_000:
            return p
    return ""


def _diarize_ready() -> bool:
    """声纹模型是否就绪（文件存在即视为就绪，实际加载失败时再降级）。"""
    try:
        import sherpa_onnx  # noqa: F401  依赖可选，未安装视为不就绪

        return bool(_diarize_model_path())
    except ImportError:
        return False


def _get_speaker_extractor():
    """懒加载声纹 embedding 模型；不可用返回 None（调用方降级）。"""
    global _speaker_extractor
    if _speaker_extractor is not None:
        return _speaker_extractor
    path = _diarize_model_path()
    if not path:
        return None
    with _speaker_extractor_lock:
        if _speaker_extractor is None:
            try:
                import sherpa_onnx

                config = sherpa_onnx.SpeakerEmbeddingExtractorConfig()
                config.model = path
                config.num_threads = 2
                _speaker_extractor = sherpa_onnx.SpeakerEmbeddingExtractor(config)
                print(f"[asr] diarization model loaded: {path}", flush=True)
            except Exception:
                traceback.print_exc()
                return None
    return _speaker_extractor


def _decode_16k_mono_int16(path: str):
    """解码音频为 16k 单声道 int16 一维数组（声纹分段用）。"""
    import numpy as np

    container = av.open(path)
    resampler = av.AudioResampler(format="s16", layout="mono", rate=16000)
    chunks = []
    for frame in container.decode(container.streams.audio[0]):
        for rf in resampler.resample(frame):
            chunks.append(rf.to_ndarray().reshape(-1))
    if not chunks:
        return np.zeros(0, dtype=np.int16)
    return np.concatenate(chunks).astype(np.int16)


def _cluster_embeddings(embs, threshold: float, max_speakers: int):
    """凝聚聚类（平均链接 + 余弦相似度）：反复合并相似度最高的两簇，
    直到最高相似度低于阈值；簇数超过 max_speakers 时强制合并保底。
    收尾把碎簇（不足 min_keep 段的噪声簇）并入最相似的相邻簇，
    避免出现「说话人17（1段）」这类碎片编号。
    返回 段序 -> 簇序（簇按首次出现时间排序，0 起）。"""
    import numpy as np

    X = np.asarray(embs, dtype=np.float32)
    X = X / (np.linalg.norm(X, axis=1, keepdims=True) + 1e-9)

    def centers(gs):
        c = np.stack([X[g].mean(0) for g in gs])
        return c / (np.linalg.norm(c, axis=1, keepdims=True) + 1e-9)

    groups = [[i] for i in range(len(X))]
    while len(groups) > 1:
        c = centers(groups)
        sim = c @ c.T
        np.fill_diagonal(sim, -2.0)
        i, j = np.unravel_index(int(np.argmax(sim)), sim.shape)
        if sim[i, j] < threshold and len(groups) <= max_speakers:
            break
        groups[i] = groups[i] + groups[j]
        groups.pop(j)

    # 碎簇归并：段数过少的簇多为信道噪声/咳嗽等非人声差异，并入最相似簇
    min_keep = max(3, int(len(X) * 0.015))
    while len(groups) > 1:
        small = [k for k, g in enumerate(groups) if len(g) < min_keep]
        if not small:
            break
        k = min(small, key=lambda k: len(groups[k]))
        c = centers(groups)
        sims = c @ c[k]
        sims[k] = -2.0
        j = int(np.argmax(sims))
        groups[j] = groups[j] + groups[k]
        groups.pop(k)

    groups.sort(key=min)  # 按首次出现顺序编号（说话人1 = 最先发言者）
    labels = {}
    for k, g in enumerate(groups):
        for i in g:
            labels[i] = k
    return labels


def _f0_median(x: "np.ndarray", sr: int = 16000) -> float:
    """自相关法提取段级基频中位数（60–400Hz）；无足够有声帧返回 0。
    纯 numpy 实现（每 ~80ms 一帧，周期性强度 >0.5 才计入），用于声纹
    失效时的回退分簇：电话录音双方共用信道，频谱声纹可能不可分，但
    音高（男女声差异）不经过信道压缩消失。"""
    import numpy as np

    frame = int(sr * 0.04)   # 40ms 分析帧
    hop = int(sr * 0.02) * 4
    lag_min, lag_max = sr // 400, sr // 60
    f0s = []
    for i in range(0, len(x) - frame, hop):
        f = x[i:i + frame].astype(np.float32)
        if np.sqrt((f ** 2).mean()) < 100:  # 静音帧跳过
            continue
        f = f - f.mean()
        ac = np.correlate(f, f, "full")[frame - 1:]
        if ac[0] <= 0:
            continue
        ac /= ac[0]
        seg = ac[lag_min:lag_max]
        pk = int(np.argmax(seg)) + lag_min
        if ac[pk] > 0.5:
            f0s.append(sr / pk)
    return float(np.median(f0s)) if f0s else 0.0


def _try_split_by_f0(audio: "np.ndarray", segments: list, min_seg_sec: float):
    """声纹聚成 1 簇时的基频回退：按段级 log-F0 做最优二分（最小化簇内
    方差和）。仅当分离度显著（两簇均值差 > _DIARIZE_F0_DELTA 且两簇
    各占总有效段 15% 以上）才采用，否则返回 None（维持 1 人，不硬分）。
    返回 段序 -> 0/1（短段/无有效 F0 的段继承时间最近的有效段标签）。"""
    import numpy as np

    f0s = []  # (段序, log-F0)
    for i, seg in enumerate(segments):
        a = int(max(0.0, seg["start"]) * 16000)
        b = int(seg["end"] * 16000)
        if b - a < min_seg_sec * 16000:
            continue
        f0 = _f0_median(audio[a:b])
        if f0 > 0:
            f0s.append((i, float(np.log(f0))))
    if len(f0s) < 6:
        return None

    vals = np.array([v for _, v in f0s])
    sv = np.sort(vals)
    n = len(sv)
    csum, csum2 = np.cumsum(sv), np.cumsum(sv ** 2)
    best_cut, best_cost, best_gap = -1, None, 0.0
    for cut in range(1, n):  # 左簇 = sv[:cut]
        m1, s1 = csum[cut - 1] / cut, csum2[cut - 1]
        m2, s2 = (csum[-1] - csum[cut - 1]) / (n - cut), csum2[-1] - csum2[cut - 1]
        cost = (s1 - csum[cut - 1] ** 2 / cut) + (s2 - (csum[-1] - csum[cut - 1]) ** 2 / (n - cut))
        if best_cost is None or cost < best_cost:
            best_cost, best_cut = cost, cut
            best_gap = m2 - m1
    if best_gap < _DIARIZE_F0_DELTA:
        return None
    lo_n = int(np.searchsorted(sv, (sv[best_cut - 1] + sv[best_cut]) / 2, side="right"))
    if lo_n < 0.15 * n or lo_n > 0.85 * n:
        return None

    thr = (sv[best_cut - 1] + sv[best_cut]) / 2
    label_by_seg = {i: (0 if v <= thr else 1) for i, v in f0s}
    out, last = {}, 0
    for i in range(len(segments)):
        if i in label_by_seg:
            last = label_by_seg[i]
        out[i] = last
    return out


def _assign_speakers(path: str, segments: list, extractor) -> None:
    """对转写段做声纹聚类并就地写入 speaker 字段；失败静默（不影响转写结果）。"""
    import numpy as np

    audio = _decode_16k_mono_int16(path)
    total_sec = audio.size / 16000.0
    if total_sec > _DIARIZE_MAX_AUDIO_SEC:
        print(f"[asr] audio too long for diarization ({total_sec:.0f}s), skip", flush=True)
        return

    # 逐段提取声纹：段过短跳过（事后继承前段标签）
    keyed = []  # (段序, embedding)
    for i, seg in enumerate(segments):
        a = int(max(0.0, seg["start"]) * 16000)
        b = int(seg["end"] * 16000)
        if b - a < _DIARIZE_MIN_SEG_SEC * 16000:
            continue
        stream = extractor.create_stream()
        stream.accept_waveform(16000, audio[a:b].astype(np.float32) / 32768.0)
        stream.input_finished()
        keyed.append((i, extractor.compute(stream)))

    if not keyed:
        return  # 全部段过短：不标注
    labels = _cluster_embeddings(
        [e for _, e in keyed], _DIARIZE_THRESHOLD, _DIARIZE_MAX_SPEAKERS)

    label_by_seg = {i: labels[k] for k, (i, _) in enumerate(keyed)}
    if len(set(labels.values())) == 1 and len(keyed) >= 8:
        # 声纹聚成 1 簇：免提/电话通话双方共用信道压缩时频谱声纹可能
        # 完全不可分（同人异人余弦都在 0.4-0.6）。尝试基频（音高）回退
        # 分裂——男女对话 F0 差通常 >30%，仅分离度显著时才采用。
        split = _try_split_by_f0(audio, segments, _DIARIZE_MIN_SEG_SEC)
        if split is not None:
            print("[asr] voiceprint collapsed to 1 cluster, "
                  "split into 2 speakers by F0", flush=True)
            label_by_seg = split

    # 有声纹的段按簇编号；过短段继承时间最近的前段（与真人对话抢话场景吻合）
    last = 0
    for i, seg in enumerate(segments):
        if i in label_by_seg:
            last = label_by_seg[i]
        seg["speaker"] = f"说话人{last + 1}"


@app.get("/health")
def health():
    return {"status": "ok", "diarize": _diarize_ready()}


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
        for seg in segments:  # 惰性生成器：产出一段推理一段
            seg_text = seg.text.strip()
            if zh:
                seg_text = to_simplified(seg_text)
            seg_dict = {
                "start": round(seg.start, 2),
                "end": round(seg.end, 2),
                "text": seg_text,
            }
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

        # 说话人分段：对已转出的段做声纹聚类（换的是声纹相似度，
        # 不再按停顿猜人）。模型缺失/解码失败时无 speaker 字段，转写照常返回。
        if diarize and out_segments:
            try:
                extractor = _get_speaker_extractor()
                if extractor is None:
                    print("[asr] diarization requested but model missing — "
                          "segments without speaker labels", flush=True)
                else:
                    _assign_speakers(tmp_path, out_segments, extractor)
                    n_spk = len({s["speaker"] for s in out_segments if "speaker" in s})
                    print(f"[asr] diarization done: {n_spk} speaker(s) "
                          f"over {len(out_segments)} segments", flush=True)
            except Exception:
                traceback.print_exc()  # 声纹失败不影响转写文本交付

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
