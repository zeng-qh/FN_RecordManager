// 录音保险箱 VoiceVault 前端逻辑（纯原生 JS，无外部依赖）

const LS_SERVER = "vv_server";
const LS_TOKEN = "vv_token";

const state = {
  server: localStorage.getItem(LS_SERVER) || location.origin,
  token: localStorage.getItem(LS_TOKEN) || "",
  page: 1,
  pageSize: 50,
  loading: false,
  asrEnabled: false,
  q: "",
};

const $ = (sel) => document.querySelector(sel);
const el = (tag, cls, html) => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html != null) e.innerHTML = html;
  return e;
};

function authHeader() {
  return { Authorization: "Bearer " + state.token };
}

async function api(path, opts = {}) {
  const url = state.server.replace(/\/$/, "") + path;
  const res = await fetch(url, {
    ...opts,
    headers: { ...authHeader(), ...(opts.headers || {}) },
  });
  if (res.status === 401) {
    openSettings("令牌无效或为空，请重新设置");
    throw new Error("unauthorized");
  }
  return res;
}

function fmtSize(bytes) {
  if (bytes < 1024) return bytes + " B";
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + " KB";
  return (bytes / 1024 / 1024).toFixed(1) + " MB";
}

function fmtDate(unix) {
  const d = new Date(unix * 1000);
  const wd = ["日", "一", "二", "三", "四", "五", "六"][d.getDay()];
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} 周${wd}`;
}

function fmtClock(unix) {
  const d = new Date(unix * 1000);
  const p = (n) => String(n).padStart(2, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}`;
}

function fmtSec(s) {
  const m = Math.floor(s / 60);
  const sec = Math.floor(s % 60);
  return `${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}`;
}

// 录音时长：超过 1 小时显示 h:mm:ss
function fmtDuration(sec) {
  if (sec == null || !isFinite(sec) || sec <= 0) return null;
  const t = Math.round(sec);
  const h = Math.floor(t / 3600);
  const m = Math.floor((t % 3600) / 60);
  const s = t % 60;
  const p = (n) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${p(m)}:${p(s)}` : `${p(m)}:${p(s)}`;
}

// ---- 加载与渲染 ----
async function loadRecordings(reset = true) {
  if (state.loading) return;
  state.loading = true;
  if (reset) {
    state.page = 1;
    $("#timeline").innerHTML = "";
  }
  setStatus("加载中…");
  try {
    const params = new URLSearchParams({
      page: state.page,
      pageSize: state.pageSize,
      q: state.q,
    });
    const res = await api(`/api/recordings?${params}`);
    const data = await res.json();
    renderItems(data.items || []);
    const total = data.total || 0;
    const loaded = state.page * state.pageSize;
    if (data.items.length === 0 && reset) {
      $("#timeline").appendChild(el("div", "empty", "还没有备份的录音。先用手机端/上传器同步一些吧。"));
    }
    setStatus(`共 ${total} 条 · 已加载 ${Math.min(loaded, total)} 条`);
    if (loaded < total) {
      ensureLoadMore(total, loaded);
    } else {
      removeLoadMore();
    }
  } catch (e) {
    if (e.message !== "unauthorized") setStatus("加载失败：" + e.message);
  } finally {
    state.loading = false;
  }
}

function renderItems(items) {
  const timeline = $("#timeline");
  let lastDay = null;
  let group = null;
  for (const it of items) {
    const day = fmtDate(it.recordedAt);
    if (day !== lastDay) {
      group = el("div", "day-group");
      group.appendChild(el("div", "day-title", day));
      timeline.appendChild(group);
      lastDay = day;
    }
    group.appendChild(renderItem(it));
  }
}

function statusBadge(status) {
  if (status === "done") return '<span class="badge done">已转写</span>';
  if (status === "pending") return '<span class="badge pending">转写中</span>';
  if (status === "error") return '<span class="badge error">转写失败</span>';
  if (status === "idle") return '<span class="badge idle">待闲时转写</span>';
  return '<span class="badge">未转写</span>';
}

function renderItem(it) {
  const item = el("div", "item");
  item.dataset.id = it.id;

  const play = el("button", "play", "▶");
  play.title = "播放";
  play.onclick = () => playRecording(it);

  const meta = el("div", "meta");
  meta.appendChild(el("div", "name", escapeHtml(it.title || it.fileName)));
  const dur = fmtDuration(it.durationSec);
  meta.appendChild(
    el("div", "sub",
      `${fmtClock(it.recordedAt)} · ${dur ? dur + " · " : ""}${fmtSize(it.size)} · ${it.device}`)
  );

  const badge = el("div", null, statusBadge(it.transcriptStatus));
  const ops = el("div", "ops");
  if (state.asrEnabled) {
    const tBtn = el("button", null, "转写");
    tBtn.onclick = () => triggerTranscribe(it.id);
    ops.appendChild(tBtn);
  }
  const vBtn = el("button", null, "文字稿");
  vBtn.onclick = () => openDetail(it.id);
  ops.appendChild(vBtn);

  item.append(play, meta, badge, ops);
  // 点击条目任意非按钮区域 → 打开详情（左播放 / 右文字稿）
  item.addEventListener("click", (e) => {
    if (e.target.closest("button")) return;
    openDetail(it.id);
  });
  return item;
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])
  );
}

// ---- 播放 ----
function playRecording(it) {
  const audio = $("#audio");
  const base = state.server.replace(/\/$/, "");
  audio.src = `${base}/api/recordings/${it.id}/file?token=${encodeURIComponent(state.token)}`;
  audio.play().catch(() => {});
  $("#playerBar").hidden = false;
  $("#nowPlaying").textContent = it.title || it.fileName;
}

// ---- 详情页：左播放 / 右文字稿 ----
let detailId = null;
let detailRec = null; // 当前详情录音（保存标题/备注用）
let detailPollTimer = null;
let detailTranscriptData = null;

async function openDetail(id) {
  stopDetailPoll();
  detailId = id;
  detailTranscriptData = null;
  try {
    const res = await api(`/api/recordings/${id}`);
    if (!res.ok) { setStatus("详情获取失败（HTTP " + res.status + "）"); return; }
    const rec = await res.json();

    $("#dTitle").textContent = rec.title || rec.fileName;
    const dur = fmtDuration(rec.durationSec);
    $("#dMeta").textContent =
      `${fmtDate(rec.recordedAt)} ${fmtClock(rec.recordedAt)}`
      + `${dur ? " · 时长 " + dur : ""} · ${fmtSize(rec.size)} · 设备 ${rec.device}`;
    $("#dInfo").innerHTML =
      `<div>文件名：${escapeHtml(rec.fileName || "-")}</div>` +
      `<div>路径：${escapeHtml(rec.relPath || "-")}</div>` +
      `<div>大小：${fmtSize(rec.size)} · SHA-256：${escapeHtml((rec.sha256 || "").slice(0, 16))}…</div>`;

    // 标题/备注编辑区回填
    detailRec = rec;
    $("#dEditTitle").value = rec.title || "";
    $("#dNoteEdit").value = rec.note || "";

    const base = state.server.replace(/\/$/, "");
    const url = `${base}/api/recordings/${id}/file?token=${encodeURIComponent(state.token)}`;
    const audio = $("#dAudio");
    audio.src = url;
    const dl = $("#dDownload");
    dl.href = url;
    dl.setAttribute("download", rec.fileName || `recording-${id}`);
    $("#dState").textContent = "就绪";
    $("#dPlay").textContent = "▶";

    $("#detailModal").hidden = false;
    loadDetailTranscript(id);
  } catch (e) {
    setStatus("详情加载失败：" + e.message);
  }
}

function closeDetail() {
  const audio = $("#dAudio");
  if (audio) audio.pause();
  stopDetailPoll();
  detailId = null;
  $("#detailModal").hidden = true;
}

function stopDetailPoll() {
  if (detailPollTimer) { clearInterval(detailPollTimer); detailPollTimer = null; }
}

async function loadDetailTranscript(id) {
  const box = $("#dTranscript");
  box.innerHTML = '<div class="d-loading">加载文字稿…</div>';
  try {
    const res = await api(`/api/recordings/${id}/transcript`);
    if (res.status === 404) {
      const err = await res.json().catch(() => ({}));
      const st = err.status || "none";
      if (st === "pending") {
        box.innerHTML = '<div class="d-loading">⏳ 转写进行中，完成后自动显示…</div>';
        startDetailPoll(id);
      } else if (st === "idle") {
        box.innerHTML = '<div class="d-empty">已加入闲时转写队列。<br/>将在设定的空闲时段（服务器本地时区）内自动转写，或点右上「立即转写」马上处理。</div>';
      } else if (st === "error") {
        box.innerHTML = '<div class="d-empty">上次转写失败。可点右上「立即转写」重试。</div>';
      } else {
        box.innerHTML = '<div class="d-empty">尚未转写。<br/>可在设置里开启「实时/闲时转写」，或点右上「立即转写」。</div>';
      }
      return;
    }
    if (!res.ok) {
      box.innerHTML = '<div class="d-empty">文字稿获取失败（HTTP ' + res.status + "）</div>";
      return;
    }
    renderDetailTranscript(await res.json());
  } catch (e) {
    box.innerHTML = '<div class="d-empty">网络异常，请稍后重试。</div>';
  }
}

function renderDetailTranscript(data) {
  detailTranscriptData = data;
  const box = $("#dTranscript");
  box.innerHTML = "";
  if (data.language) box.appendChild(el("div", "d-lang", "识别语言：" + escapeHtml(data.language)));
  if (data.segments && data.segments.length) {
    for (const seg of data.segments) {
      const line = el("span", "seg");
      const spk = seg.speaker ? `<span class="spk">[${escapeHtml(seg.speaker)}]</span>` : "";
      line.innerHTML = `<span class="ts">${fmtSec(seg.start)}</span>${spk}${escapeHtml(seg.text || "")}`;
      line.onclick = () => {
        const audio = $("#dAudio");
        if (audio && audio.src) { audio.currentTime = seg.start; audio.play().catch(() => {}); }
      };
      box.appendChild(line);
    }
  } else if (data.text) {
    box.appendChild(el("div", "d-plain", escapeHtml(data.text)));
  } else {
    box.appendChild(el("div", "d-empty", "（无文字内容）"));
  }
}

function startDetailPoll(id) {
  stopDetailPoll();
  let tries = 0;
  detailPollTimer = setInterval(async () => {
    tries += 1;
    if (detailId !== id || tries > 600) { stopDetailPoll(); return; }
    try {
      const res = await api(`/api/recordings/${id}/transcript`);
      if (res.ok) {
        stopDetailPoll();
        renderDetailTranscript(await res.json());
        loadRecordings(true); // 顺带刷新列表徽标
      }
    } catch (e) { /* 网络抖动，继续轮询 */ }
  }, 3000);
}

function transcriptPlainText(d) {
  if (d && d.segments && d.segments.length) {
    return d.segments
      .map((s) => (s.speaker ? `[${s.speaker}] ` : "") + `${fmtSec(s.start)} ${s.text || ""}`)
      .join("\n");
  }
  return (d && d.text) || "";
}

async function copyTranscript() {
  const text = transcriptPlainText(detailTranscriptData);
  if (!text) { alert("暂无可复制的文字稿。"); return; }
  try {
    await navigator.clipboard.writeText(text);
    setStatus("文字稿已复制到剪贴板。");
  } catch (e) {
    // 非 HTTPS 环境降级
    const ta = el("textarea");
    ta.value = text;
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand("copy"); setStatus("文字稿已复制到剪贴板。"); }
    catch (e2) { alert("复制失败，请手动选择文本。"); }
    ta.remove();
  }
}

async function transcribeFromDetail() {
  if (!detailId) return;
  const id = detailId;
  try {
    const res = await api(`/api/recordings/${id}/transcribe`, { method: "POST" });
    if (res.ok) {
      $("#dTranscript").innerHTML = '<div class="d-loading">⏳ 已加入转写队列，完成后自动显示…</div>';
      startDetailPoll(id);
    } else {
      const d = await res.json().catch(() => ({}));
      if (d.error === "asr disabled") {
        alert("ASR 未启用：请先在设置里开启「启用语音转文字」，并确认 NAS 上已部署 ASR 边车。");
      } else {
        alert("无法转写：" + (d.error || res.status));
      }
    }
  } catch (e) {}
}

async function triggerTranscribe(id) {
  if (!confirm("提交转写任务？将在 NAS 本地运行 ASR。")) return;
  try {
    const res = await api(`/api/recordings/${id}/transcribe`, { method: "POST" });
    if (res.ok) {
      alert("已提交，转写完成后可点「文字稿」查看。");
      loadRecordings(true);
    } else {
      const d = await res.json().catch(() => ({}));
      alert("无法转写：" + (d.error || res.status));
    }
  } catch (e) {}
}

// ---- 网页端上传（弹层 + 4MB 分片 + 断点续传）----
// uploadId 由服务端按 (device|relPath|size|mtime) 派生：同一文件中断后重新
// 上传时 init 返回已收分片，只补缺失部分——刷新页面也不丢进度。
const CHUNK_SIZE = 4 * 1024 * 1024;

function openUpload() {
  $("#uploadModal").hidden = false;
}
function closeUpload() {
  $("#uploadModal").hidden = true; // 仅隐藏 UI，后台队列继续跑
}

$("#uploadBtn").onclick = openUpload;
$("#closeUpload").onclick = closeUpload;
$("#uploadModal").addEventListener("click", (e) => {
  if (e.target.id === "uploadModal") closeUpload();
});

const dropZone = $("#dropZone");
dropZone.onclick = () => $("#uploadInput").click();
dropZone.addEventListener("dragover", (e) => { e.preventDefault(); dropZone.classList.add("drag"); });
dropZone.addEventListener("dragleave", () => dropZone.classList.remove("drag"));
dropZone.addEventListener("drop", (e) => {
  e.preventDefault();
  dropZone.classList.remove("drag");
  addUploadItems(Array.from(e.dataTransfer?.files || []));
});
$("#uploadInput").onchange = (e) => {
  addUploadItems(Array.from(e.target.files || []));
  e.target.value = "";
};

const uploadQueue = []; // {file, el, bar, label, status: pending|uploading|done|failed}

function addUploadItems(files) {
  if (!files.length) return;
  for (const f of files) {
    const row = el("div", "upload-item");
    const info = el("div", "u-info");
    info.appendChild(el("div", "u-name", escapeHtml(f.name)));
    info.appendChild(el("div", "u-sub", fmtSize(f.size)));
    const bar = el("div", "u-bar");
    const fill = el("div", "u-fill");
    bar.appendChild(fill);
    const label = el("div", "u-label", "排队中");
    row.append(info, bar, label);
    $("#uploadQueue").appendChild(row);
    const item = { file: f, fill, label, status: "pending" };
    uploadQueue.push(item);
    runUploadItem(item);
  }
}

function setItemState(item, cls, text) {
  item.status = cls;
  item.label.className = "u-label " + (cls === "done" ? "ok" : cls === "failed" ? "err" : "");
  item.label.textContent = text;
}

async function runUploadItem(item) {
  try {
    setItemState(item, "uploading", "初始化…");
    const f = item.file;
    const totalChunks = Math.ceil(f.size / CHUNK_SIZE) || 1;
    const mtime = Math.floor((f.lastModified || Date.now()) / 1000);

    // init：命中已有会话时返回已收分片，实现断点续传
    const initRes = await api("/api/upload/init", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        relPath: f.name, device: "web", mtime,
        size: f.size, chunkSize: CHUNK_SIZE, totalChunks,
      }),
    });
    if (!initRes.ok) throw new Error("init HTTP " + initRes.status);
    const init = await initRes.json();
    const received = new Set(init.received || []);
    let doneCount = received.size;
    const drawProgress = () => {
      const pct = Math.round((doneCount / totalChunks) * 100);
      item.fill.style.width = pct + "%";
      setItemState(item, "uploading", `上传中 ${pct}%${received.size ? "（续传）" : ""}`);
    };
    drawProgress();

    for (let i = 0; i < totalChunks; i++) {
      if (received.has(i)) continue; // 断点续传：跳过已传分片
      const blob = f.slice(i * CHUNK_SIZE, Math.min((i + 1) * CHUNK_SIZE, f.size));
      await putChunkWithRetry(init.uploadId, i, blob);
      doneCount += 1;
      drawProgress();
    }

    setItemState(item, "uploading", "合并校验…");
    const compRes = await api(`/api/upload/complete/${init.uploadId}`, { method: "POST" });
    if (!compRes.ok) {
      const d = await compRes.json().catch(() => ({}));
      throw new Error(d.error || "complete HTTP " + compRes.status);
    }
    item.fill.style.width = "100%";
    setItemState(item, "done", "✓ 已上传" + (received.size ? "（断点续传完成）" : ""));
    setStatus(`已上传：${f.name}`);
    loadRecordings(true);
  } catch (err) {
    setItemState(item, "failed", "");
    const retry = el("button", "u-retry", "续传");
    retry.title = String(err.message || err);
    retry.onclick = () => {
      retry.remove();
      runUploadItem(item); // 重新 init 即从断点继续
    };
    item.label.appendChild(retry);
    setStatus(`上传中断：${item.file.name}，可点「续传」从断点继续。`);
  }
}

async function putChunkWithRetry(uploadId, index, blob, retries = 3) {
  for (let attempt = 0; ; attempt++) {
    try {
      const res = await api(`/api/upload/chunk/${uploadId}/${index}`, {
        method: "PUT",
        headers: { "Content-Type": "application/octet-stream" },
        body: blob,
      });
      if (!res.ok) throw new Error("chunk HTTP " + res.status);
      return await res.json();
    } catch (err) {
      if (attempt >= retries) throw err;
      await new Promise((r) => setTimeout(r, 1000 * 2 ** attempt)); // 指数退避 1s/2s/4s
    }
  }
}

// ---- 删除录音（详情页）----
async function deleteFromDetail() {
  if (!detailId) return;
  const id = detailId;
  if (!confirm("确定删除这条录音？录音文件与文字稿将一并删除，不可恢复。")) return;
  try {
    const res = await api(`/api/recordings/${id}`, { method: "DELETE" });
    if (res.ok) {
      closeDetail();
      setStatus("录音已删除。");
      loadRecordings(true);
    } else {
      const d = await res.json().catch(() => ({}));
      alert("删除失败：" + (d.error || res.status));
    }
  } catch (e) {
    alert("删除失败：" + e.message);
  }
}

// ---- 保存标题/备注（PATCH）----
async function saveDetailMeta() {
  if (!detailId || !detailRec) return;
  const title = $("#dEditTitle").value.trim();
  const note = $("#dNoteEdit").value.trim();
  try {
    const res = await api(`/api/recordings/${detailId}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title, note }),
    });
    if (res.ok) {
      const rec = await res.json();
      detailRec = rec;
      $("#dTitle").textContent = rec.title || rec.fileName;
      setStatus("标题/备注已保存。");
      loadRecordings(true);
    } else {
      const d = await res.json().catch(() => ({}));
      alert("保存失败：" + (d.error || res.status));
    }
  } catch (e) {
    alert("保存失败：" + e.message);
  }
}

// ---- 导出文字稿（TXT / SRT）----
async function exportTranscript(fmt) {
  if (!detailId) return;
  if (!detailTranscriptData) {
    alert("文字稿尚未生成，请先转写。");
    return;
  }
  try {
    const res = await api(`/api/recordings/${detailId}/transcript/export?format=${fmt}`);
    if (!res.ok) {
      const d = await res.json().catch(() => ({}));
      alert("导出失败：" + (d.error || res.status));
      return;
    }
    const blob = await res.blob();
    const name = `${(detailRec?.title || detailRec?.fileName || "transcript").replace(/[\\/:*?"<>|]/g, "_")}.${fmt}`;
    const url = URL.createObjectURL(blob);
    const a = el("a");
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
    setStatus(`文字稿已导出为 ${fmt.toUpperCase()}。`);
  } catch (e) {
    alert("导出失败：" + e.message);
  }
}

// ---- 批量转写（设置页）----
async function transcribeAll() {
  if (!state.asrEnabled) {
    alert("请先勾选「启用语音转文字」并保存。");
    return;
  }
  if (!confirm("把所有未转写的录音一次性加入队列？将串行执行（闲时模式下也会立即开始）。")) return;
  try {
    const res = await api("/api/transcribe/pending", { method: "POST" });
    if (res.ok) {
      const d = await res.json();
      setStatus(`已把 ${d.queued} 条录音加入转写队列。`);
      closeSettings();
      loadRecordings(true);
    } else {
      const d = await res.json().catch(() => ({}));
      alert("无法批量转写：" + (d.error || res.status));
    }
  } catch (e) {}
}

// ---- 分页 ----
function ensureLoadMore(total, loaded) {
  removeLoadMore();
  const btn = el("button", "play", "加载更多");
  btn.className = "";
  btn.style.cssText = "display:block;margin:16px auto;background:var(--panel-2);border:1px solid var(--border);color:var(--text);padding:10px 20px;border-radius:10px;cursor:pointer;";
  btn.id = "loadMore";
  btn.textContent = `加载更多（已 ${loaded}/${total}）`;
  btn.onclick = () => {
    state.page += 1;
    loadRecordings(false);
  };
  $("#timeline").appendChild(btn);
}
function removeLoadMore() {
  const b = $("#loadMore");
  if (b) b.remove();
}

function setStatus(t) {
  $("#status").textContent = t;
}

// ---- 设置 ----
function openSettings(msg) {
  $("#cfgServer").value = state.server;
  $("#cfgToken").value = state.token;
  $("#settingsModal").hidden = false;
  if (msg) setStatus(msg);
  refreshSettingsForm();
}
function closeSettings() {
  $("#settingsModal").hidden = true;
}

async function refreshSettingsForm() {
  // 拉取 ASR 配置回填
  try {
    const res = await api("/api/settings");
    const d = await res.json();
    state.asrEnabled = !!(d.asr && d.asr.enabled);
    $("#cfgAsrEnabled").checked = state.asrEnabled;
    const mode = (d.asr?.mode || "Off").toString().toLowerCase();
    $("#cfgAsrMode").value = ["off", "realtime", "idle"].includes(mode) ? mode : "off";
    $("#cfgAsrIdleStart").value = d.asr?.idleStartHour ?? 22;
    $("#cfgAsrIdleEnd").value = d.asr?.idleEndHour ?? 7;
    $("#cfgAsrEndpoint").value = d.asr?.endpoint || "";
    $("#cfgAsrModel").value = d.asr?.model || "small";
    $("#cfgAsrDiarize").checked = !!(d.asr && d.asr.speakerDiarization);
    $("#cfgRetDays").value = d.retention?.days ?? 0;
    const gb = d.retention?.maxBytes ?? 0;
    $("#cfgRetGb").value = gb > 0 ? Math.max(1, Math.round(gb / (1024 ** 3))) : 0;
    syncIdleWindowBox();
  } catch (e) {}
}

// 模式选择联动：仅闲时模式显示时间段输入
function syncIdleWindowBox() {
  const isIdle = $("#cfgAsrMode").value === "idle";
  $("#idleWindowBox").hidden = !isIdle;
  if (isIdle) updateIdlePreview();
}

// 把起止小时渲染成人类可读的空闲时段（支持跨午夜）
function updateIdlePreview() {
  const s = Number($("#cfgAsrIdleStart").value);
  const e = Number($("#cfgAsrIdleEnd").value);
  const box = $("#idlePreview");
  if (!Number.isFinite(s) || !Number.isFinite(e) || s < 0 || s > 23 || e < 0 || e > 23) {
    box.textContent = "小时需在 0–23 之间";
    return;
  }
  if (s === e) {
    box.textContent = "起始与结束相同，等于不限制时段（等同于实时转写）";
    return;
  }
  const fmt = (h) => String(h).padStart(2, "0") + ":00";
  if (s < e) {
    box.textContent = `每天 ${fmt(s)} – ${fmt(e)} 转写`;
  } else {
    box.textContent = `每天 ${fmt(s)} – 次日 ${fmt(e)} 转写（跨午夜）`;
  }
}

async function saveSettings() {
  state.server = $("#cfgServer").value.trim() || location.origin;
  state.token = $("#cfgToken").value.trim();
  localStorage.setItem(LS_SERVER, state.server);
  localStorage.setItem(LS_TOKEN, state.token);

  // 把设置写回服务端（ASR + 保留策略）
  const s = Math.max(0, Math.min(23, Number($("#cfgAsrIdleStart").value) || 0));
  const e = Math.max(0, Math.min(23, Number($("#cfgAsrIdleEnd").value) || 0));
  const retGb = Math.max(0, Number($("#cfgRetGb").value) || 0);
  const body = {
    asr: {
      enabled: $("#cfgAsrEnabled").checked,
      mode: $("#cfgAsrMode").value,
      idleStartHour: s,
      idleEndHour: e,
      endpoint: $("#cfgAsrEndpoint").value.trim(),
      model: $("#cfgAsrModel").value,
      speakerDiarization: $("#cfgAsrDiarize").checked,
    },
    retention: {
      days: Math.max(0, Number($("#cfgRetDays").value) || 0),
      maxBytes: Math.round(retGb * 1024 ** 3),
    },
  };
  try {
    await api("/api/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    state.asrEnabled = body.asr.enabled;
  } catch (e) {}

  closeSettings();
  loadRecordings(true);
}

async function testConnection() {
  const server = $("#cfgServer").value.trim() || location.origin;
  const token = $("#cfgToken").value.trim();
  try {
    const res = await fetch(server.replace(/\/$/, "") + "/api/health", {
      headers: { Authorization: "Bearer " + token },
    });
    if (res.ok) {
      const d = await res.json();
      alert("连接成功！状态：" + d.status + "，ASR：" + (d.asr ? "已启用" : "未启用"));
    } else {
      alert("连接失败，HTTP " + res.status);
    }
  } catch (e) {
    alert("连接失败：" + e.message);
  }
}

// ---- 事件绑定 ----
$("#refresh").onclick = () => loadRecordings(true);
$("#settingsBtn").onclick = () => openSettings();
$("#saveSettings").onclick = saveSettings;
$("#testConn").onclick = testConnection;
$("#cfgAsrMode").onchange = syncIdleWindowBox;
$("#cfgAsrIdleStart").oninput = updateIdlePreview;
$("#cfgAsrIdleEnd").oninput = updateIdlePreview;
$("#dDelete").onclick = deleteFromDetail;
$("#transcribeAllBtn").onclick = transcribeAll;
$("#dSaveMeta").onclick = saveDetailMeta;
$("#dExportTxt").onclick = () => exportTranscript("txt");
$("#dExportSrt").onclick = () => exportTranscript("srt");

// 详情弹层事件
$("#dClose").onclick = closeDetail;
$("#dCopy").onclick = copyTranscript;
$("#dTranscribe").onclick = transcribeFromDetail;
$("#detailModal").addEventListener("click", (e) => {
  if (e.target.id === "detailModal") closeDetail();
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && !$("#detailModal").hidden) closeDetail();
});

// 详情页播放器状态
(function wireDetailPlayer() {
  const audio = $("#dAudio");
  const playBtn = $("#dPlay");
  playBtn.onclick = () => {
    if (!audio.src) return;
    if (audio.paused) audio.play().catch(() => {});
    else audio.pause();
  };
  audio.onplay = () => { playBtn.textContent = "⏸"; };
  audio.onpause = () => { playBtn.textContent = "▶"; };
  audio.onended = () => { playBtn.textContent = "▶"; $("#dState").textContent = "播放结束"; };
  audio.ontimeupdate = () => {
    if (audio.duration && isFinite(audio.duration)) {
      $("#dState").textContent = `${fmtSec(audio.currentTime)} / ${fmtSec(audio.duration)}`;
    } else if (audio.currentTime) {
      $("#dState").textContent = fmtSec(audio.currentTime);
    }
  };
})();

let searchTimer;
$("#search").oninput = (e) => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => {
    state.q = e.target.value.trim();
    loadRecordings(true);
  }, 300);
};
$("#settingsModal").addEventListener("click", (e) => {
  if (e.target.id === "settingsModal") closeSettings();
});

// ---- 初始化 ----
(async function init() {
  if (!state.token) {
    openSettings("首次使用请填写访问令牌");
    return;
  }
  try {
    await refreshSettingsForm();
  } catch (e) {}
  loadRecordings(true);
})();
