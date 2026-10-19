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
  meta.appendChild(
    el("div", "sub", `${fmtClock(it.recordedAt)} · ${fmtSize(it.size)} · ${it.device}`)
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
    $("#dMeta").textContent =
      `${fmtDate(rec.recordedAt)} ${fmtClock(rec.recordedAt)} · ${fmtSize(rec.size)} · 设备 ${rec.device}`;
    $("#dInfo").innerHTML =
      `<div>文件名：${escapeHtml(rec.fileName || "-")}</div>` +
      `<div>路径：${escapeHtml(rec.relPath || "-")}</div>` +
      `<div>大小：${fmtSize(rec.size)} · SHA-256：${escapeHtml((rec.sha256 || "").slice(0, 16))}…</div>`;

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
      } else if (st === "error") {
        box.innerHTML = '<div class="d-empty">上次转写失败。可点右上「立即转写」重试。</div>';
      } else {
        box.innerHTML = '<div class="d-empty">尚未转写。<br/>可在设置里开启「自动转写新录音」，或点右上「立即转写」。</div>';
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
    $("#cfgAsrAuto").checked = !!(d.asr && d.asr.autoTranscribe);
    $("#cfgAsrEndpoint").value = d.asr?.endpoint || "";
    $("#cfgAsrModel").value = d.asr?.model || "small";
    $("#cfgAsrDiarize").checked = !!(d.asr && d.asr.speakerDiarization);
  } catch (e) {}
}

async function saveSettings() {
  state.server = $("#cfgServer").value.trim() || location.origin;
  state.token = $("#cfgToken").value.trim();
  localStorage.setItem(LS_SERVER, state.server);
  localStorage.setItem(LS_TOKEN, state.token);

  // 把 ASR 配置写回服务端（含自动转写开关）
  const asr = {
    enabled: $("#cfgAsrEnabled").checked,
    autoTranscribe: $("#cfgAsrAuto").checked,
    endpoint: $("#cfgAsrEndpoint").value.trim(),
    model: $("#cfgAsrModel").value,
    speakerDiarization: $("#cfgAsrDiarize").checked,
  };
  try {
    await api("/api/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(asr),
    });
    state.asrEnabled = asr.enabled;
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
