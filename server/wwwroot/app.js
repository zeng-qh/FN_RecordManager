// 录音保险箱 VoiceVault 前端逻辑（纯原生 JS，无外部依赖）

const LS_SERVER = "vv_server";
const LS_TOKEN = "vv_token";

const state = {
  server: localStorage.getItem(LS_SERVER) || location.origin,
  token: localStorage.getItem(LS_TOKEN) || "",
  page: 1,
  pageSize: Number(localStorage.getItem("vv_pagesize")) || 20, // 用户偏好，设置保存后服务端持久化
  loading: false,
  asrEnabled: true, // 与服务端默认开启一致（实际值以 /api/settings 回填为准）
  q: "",
  contact: "",   // 来源分类筛选（电话号码 / 微信联系人）
  month: "",     // 月份筛选（2026-11）
  selectMode: false,      // 多选管理模式
  selected: new Set(),    // 选中的录音 id
  selectedBytes: new Map(), // 选中录音 id → 字节大小（批量栏显示总大小用）
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
  const url = new URL(state.server.replace(/\/$/, "") + path);
  // 兜底：部分反向代理会剥离 Authorization 头，token 同时放在 query（服务端两处等价校验）
  if (state.token) url.searchParams.set("token", state.token);
  const res = await fetch(url, {
    ...opts,
    headers: { ...authHeader(), ...(opts.headers || {}) },
  });
  if (res.status === 401) {
    // 仅当设置弹窗未打开时才引导打开：
    // 否则 saveSettings 关闭弹窗后，refreshSettingsForm 的异步 401 回调
    // 会把弹窗重新打开，表现为「点保存后设置弹窗不关闭」
    if ($("#settingsModal").hidden) openSettings("令牌无效或为空，请重新设置");
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
  if (state.loading) return false;
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
    if (state.contact) params.set("contact", state.contact);
    const [fy, fm] = state.month ? state.month.split("-").map(Number) : [0, 0];
    if (fy) {
      params.set("from", Math.floor(new Date(fy, fm - 1, 1).getTime() / 1000));
      params.set("to", Math.floor(new Date(fy, fm, 1).getTime() / 1000) - 1); // 月末 23:59:59
    }
    // 统计与列表并行请求：总耗时取两者较大值而非相加，列表首屏更快
    const statsPromise = api(`/api/stats`)
      .then((r) => (r.ok ? r.json() : null))
      .catch(() => null);
    const res = await api(`/api/recordings?${params}`);
    const data = await res.json();
    renderItems(data.items || []);
    schedulePendingRefresh(data.items || []); // 有转写任务时自动刷新列表徽标进度
    const total = data.total || 0;
    const loaded = state.page * state.pageSize;
    if (data.items.length === 0 && reset) {
      $("#timeline").appendChild(el("div", "empty", "还没有备份的录音。先用手机端/上传器同步一些吧。"));
    }
    // 顶部统计条：并行请求此刻通常已就绪，不再额外等待一个往返
    const s = await statsPromise;
    if (s) {
      const dur = fmtDuration(s.totalDurationSec);
      setStatus(
        `共 ${total} 条 · 已转写 ${s.transcribed ?? "—"} · ` +
        `总时长 ${dur ?? "—"} · 占用 ${fmtSize(s.totalBytes ?? 0)}`
      );
    } else {
      setStatus(`共 ${total} 条 · 已加载 ${Math.min(loaded, total)} 条`);
    }
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
  return true;
}

// 有转写任务进行中时，列表自动轻量刷新（3s），让徽标百分比走动；无任务即停。
// 刷新后恢复滚动位置：正在翻看历史时不会被拉回顶部。
let pendingRefreshTimer = null;
function schedulePendingRefresh(items) {
  clearTimeout(pendingRefreshTimer);
  if (items.some((it) => it.transcriptStatus === "pending")) {
    pendingRefreshTimer = setTimeout(async () => {
      const y = window.scrollY;
      const ok = await loadRecordings(true);
      if (ok) window.scrollTo(0, y);
    }, 3000);
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

function statusBadge(it) {
  const status = it.transcriptStatus;
  if (status === "done") return '<span class="badge done">已转写</span>';
  if (status === "pending") {
    const p = Math.round(it.transcriptProgress || 0);
    return `<span class="badge pending">转写中 ${p}%</span>`;
  }
  if (status === "error") return '<span class="badge error">转写失败</span>';
  if (status === "idle") return '<span class="badge idle">待闲时转写</span>';
  return '<span class="badge">未转写</span>';
}

function renderItem(it) {
  const item = el("div", "item");
  item.dataset.id = it.id;
  item.dataset.size = it.size; // 批量「全选」时累计已选大小用
  if (state.selectMode && state.selected.has(it.id)) item.classList.add("selected");

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

  const badge = el("div", null, statusBadge(it));
  const ops = el("div", "ops");
  if (state.selectMode) {
    // 多选模式：复选框替代操作按钮，整卡点击切换选中
    const chk = el("input", "pick");
    chk.type = "checkbox";
    chk.checked = state.selected.has(it.id);
    // 勾选框自身走原生 change 事件同步状态：
    // 之前靠卡片 click + preventDefault 处理，浏览器的「取消默认行为后
    // 恢复勾选状态」会覆盖手动同步，导致勾了没反应（时好时坏）
    chk.onchange = () => {
      if (chk.checked) {
        state.selected.add(it.id);
        state.selectedBytes.set(it.id, it.size);
      } else {
        state.selected.delete(it.id);
        state.selectedBytes.delete(it.id);
      }
      item.classList.toggle("selected", chk.checked);
      syncBatchBar();
    };
    ops.appendChild(chk);
  } else {
    if (state.asrEnabled) {
      const tBtn = el("button", null, "转写");
      tBtn.onclick = () => triggerTranscribe(it.id);
      ops.appendChild(tBtn);
    }
    const vBtn = el("button", null, "文字稿");
    vBtn.onclick = () => openDetail(it.id);
    ops.appendChild(vBtn);
  }

  item.append(play, meta, badge, ops);
  // 点击条目：多选模式切换选中；普通模式打开详情（按钮/勾选框区除外）
  item.addEventListener("click", (e) => {
    if (e.target.closest("button") || e.target.closest("input.pick")) return;
    if (state.selectMode) {
      toggleSelect(it.id, item, it.size);
    } else {
      openDetail(it.id);
    }
  });
  return item;
}

// ---- 多选管理（批量删除 / 打包导出）----
// 点击卡片空白处切换选中：只更新当前卡片，不做全列表遍历（列表长时也即时响应）
function toggleSelect(id, itemEl, size) {
  if (state.selected.has(id)) {
    state.selected.delete(id);
    state.selectedBytes.delete(id);
  } else {
    state.selected.add(id);
    state.selectedBytes.set(id, size || 0);
  }
  if (itemEl) {
    const on = state.selected.has(id);
    itemEl.classList.toggle("selected", on);
    const chk = itemEl.querySelector("input.pick");
    if (chk) chk.checked = on;
  }
  syncBatchBar();
}

function syncBatchBar() {
  const bar = $("#batchBar");
  bar.hidden = !state.selectMode;
  let bytes = 0;
  for (const b of state.selectedBytes.values()) bytes += b;
  $("#batchCount").textContent =
    `已选 ${state.selected.size} 条 · ${fmtSize(bytes)}`;
  $("#manageBtn").classList.toggle("active", state.selectMode);
}

function setSelectMode(on) {
  state.selectMode = on;
  state.selected.clear();
  state.selectedBytes.clear();
  syncBatchBar();
  loadRecordings(true); // 重绘列表以切换复选框/操作按钮形态
}

async function batchDelete() {
  const ids = [...state.selected];
  if (!ids.length) return setStatus("请先勾选要删除的录音。");
  if (!confirm(`确定删除选中的 ${ids.length} 条录音？文件与文字稿将一并删除，不可恢复。`)) return;
  try {
    const res = await api("/api/recordings/batch-delete", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(ids),
    });
    if (!res.ok) throw new Error("HTTP " + res.status);
    const d = await res.json();
    setStatus(`已删除 ${d.deleted} 条录音。`);
    setSelectMode(false);
    loadRecordings(true);
  } catch (e) {
    if (e.message !== "unauthorized") setStatus("批量删除失败：" + e.message);
  }
}

function batchExport() {
  const ids = [...state.selected];
  if (!ids.length) return setStatus("请先勾选要导出的录音。");
  // 浏览器直连下载（?token= 等价凭证），避免大 ZIP 占用前端内存
  const url = `${state.server.replace(/\/$/, "")}/api/recordings/export/zip` +
    `?ids=${ids.join(",")}&token=${encodeURIComponent(state.token)}`;
  setStatus(`开始打包 ${ids.length} 条录音…`);
  const a = el("a");
  a.href = url;
  a.download = "voicevault.zip";
  document.body.appendChild(a);
  a.click();
  a.remove();
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
let detailSegEls = []; // [{start,end,el}] 用于播放进度高亮与滚动跟随

async function openDetail(id) {
  stopDetailPoll();
  detailId = id;
  detailTranscriptData = null;
  detailSegEls = [];
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
        renderPendingProgress(err.progress || 0, err.partial || "");
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

// 转写进行中：进度条 + 百分比 + 已转出的部分文字（边转写边显示）
function renderPendingProgress(progress, partial) {
  const box = $("#dTranscript");
  const pct = Math.max(1, Math.min(99, Math.round(progress || 0)));
  box.innerHTML =
    `<div class="t-progress">
       <div class="t-progress-bar"><div class="t-progress-fill" style="width:${pct}%"></div></div>
       <div class="t-progress-label">⏳ 转写中 ${pct}%${partial ? " · 已识别内容实时显示在下方" : ""}</div>
     </div>` +
    (partial
      ? `<div class="d-partial">${escapeHtml(partial)}</div>`
      : '<div class="d-loading">正在识别音频内容…</div>');
}

function renderDetailTranscript(data) {
  detailTranscriptData = data;
  detailSegEls = [];
  lastActiveSegEl = null; // 重渲染后旧元素引用失效，重置高亮跟踪
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
      detailSegEls.push({ start: seg.start, end: seg.end, el: line });
      box.appendChild(line);
    }
  } else if (data.text) {
    box.appendChild(el("div", "d-plain", escapeHtml(data.text)));
  } else {
    box.appendChild(el("div", "d-empty", "（无文字内容）"));
  }
}

// 播放进度 → 文字稿高亮 + 滚动跟随（仅当高亮段变化时滚动，避免抖动）
let lastActiveSegEl = null;
function syncTranscriptHighlight(curSec) {
  const hit = detailSegEls.find((s) => curSec >= s.start && curSec < s.end);
  const target = hit ? hit.el : null;
  if (target === lastActiveSegEl) return;
  if (lastActiveSegEl) lastActiveSegEl.classList.remove("active");
  if (target) {
    target.classList.add("active");
    target.scrollIntoView({ block: "nearest" });
  }
  lastActiveSegEl = target;
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
      } else if (res.status === 404) {
        // 仍在转写：刷新进度条与已转出的部分文字
        const err = await res.json().catch(() => ({}));
        if (err.status === "pending") renderPendingProgress(err.progress || 0, err.partial || "");
      }
    } catch (e) { /* 网络抖动，继续轮询 */ }
  }, 1500);
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
  const btn = $("#dCopy");
  const flash = () => {
    btn.textContent = "已复制 ✓";
    setTimeout(() => { btn.textContent = "复制全文"; }, 1500);
  };
  try {
    await navigator.clipboard.writeText(text);
    setStatus("文字稿已复制到剪贴板。");
    flash();
  } catch (e) {
    // 非 HTTPS 环境降级
    const ta = el("textarea");
    ta.value = text;
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand("copy"); setStatus("文字稿已复制到剪贴板。"); flash(); }
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

// ---- SHA-256（秒传预校验用）----
// 安全上下文（HTTPS/localhost）走 WebCrypto；局域网 http 下 crypto.subtle
// 不可用，退回纯 JS 分块流式实现（逐块 slice 读取，不整读进内存）。
const SHA256_K = new Uint32Array([
  0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
  0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
  0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
  0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
  0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
  0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
  0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
  0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2,
]);

// 处理一个 64 字节块（data 为 DataView，offset 为块起始字节）
function sha256Block(state, dv, offset) {
  const w = new Uint32Array(64);
  for (let i = 0; i < 16; i++) w[i] = dv.getUint32(offset + i * 4);
  for (let i = 16; i < 64; i++) {
    const x = w[i - 15], y = w[i - 2];
    const s0 = ((x >>> 7) | (x << 25)) ^ ((x >>> 18) | (x << 14)) ^ (x >>> 3);
    const s1 = ((y >>> 17) | (y << 15)) ^ ((y >>> 19) | (y << 13)) ^ (y >>> 10);
    w[i] = (w[i - 16] + s0 + w[i - 7] + s1) | 0;
  }
  let a = state[0], b = state[1], c = state[2], d = state[3];
  let e = state[4], f = state[5], g = state[6], h = state[7];
  for (let i = 0; i < 64; i++) {
    const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
    const ch = (e & f) ^ (~e & g);
    const t1 = (h + S1 + ch + SHA256_K[i] + w[i]) | 0;
    const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
    const maj = (a & b) ^ (a & c) ^ (b & c);
    const t2 = (S0 + maj) | 0;
    h = g; g = f; f = e; e = (d + t1) | 0;
    d = c; c = b; b = a; a = (t1 + t2) | 0;
  }
  state[0] = (state[0] + a) | 0; state[1] = (state[1] + b) | 0;
  state[2] = (state[2] + c) | 0; state[3] = (state[3] + d) | 0;
  state[4] = (state[4] + e) | 0; state[5] = (state[5] + f) | 0;
  state[6] = (state[6] + g) | 0; state[7] = (state[7] + h) | 0;
}

async function sha256Hex(file) {
  // WebCrypto 路径（HTTPS/localhost）；超大文件一次读入内存不划算，跳过预校验
  if (typeof crypto !== "undefined" && crypto.subtle) {
    if (file.size > 512 * 1024 * 1024) return null;
    const h = await crypto.subtle.digest("SHA-256", await file.arrayBuffer());
    return [...new Uint8Array(h)].map((b) => b.toString(16).padStart(2, "0")).join("");
  }
  // 纯 JS 回退：4MB 一块流式处理（录音文件一般 < 100MB，速度可接受）
  const state = new Uint32Array([
    0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
    0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
  ]);
  const CHUNK = 4 * 1024 * 1024;
  let tail = new Uint8Array(0); // 尾部不足 64 字节的部分，跨块累积
  for (let off = 0; off < file.size; off += CHUNK) {
    const part = new Uint8Array(
      await file.slice(off, Math.min(off + CHUNK, file.size)).arrayBuffer());
    const all = new Uint8Array(tail.length + part.length);
    all.set(tail); all.set(part, tail.length);
    const blocks = Math.floor(all.length / 64);
    const dv = new DataView(all.buffer);
    for (let i = 0; i < blocks; i++) sha256Block(state, dv, i * 64);
    tail = all.slice(blocks * 64);
  }
  // 填充：0x80 + 0x00 至 56 字节对齐 + 8 字节大端位长
  const padLen = tail.length < 56 ? 56 - tail.length : 120 - tail.length;
  const last = new Uint8Array(tail.length + padLen + 8);
  last.set(tail);
  last[tail.length] = 0x80;
  const ldv = new DataView(last.buffer);
  const totalBits = file.size * 8;
  ldv.setUint32(last.length - 8, Math.floor(totalBits / 4294967296));
  ldv.setUint32(last.length - 4, totalBits >>> 0); // totalBits < 2^53，>>>0 取低 32 位
  for (let i = 0; i < last.length; i += 64) sha256Block(state, ldv, i);
  return [...state].map((x) => (x >>> 0).toString(16).padStart(8, "0")).join("");
}

// 秒传探测：命中（库内已有同哈希文件）返回 true
async function serverHasSha(sha) {
  try {
    const res = await api("/api/upload/exists", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify([sha]),
    });
    if (!res.ok) return false;
    const d = await res.json();
    return (d.existing || []).some((s) => s.toLowerCase() === sha);
  } catch {
    return false; // 探测失败不阻塞上传，走正常流程
  }
}

// ---- 网页端上传（弹层 + 512KB 分片 + 断点续传）----
// uploadId 由服务端按 (device|relPath|size|mtime) 派生：同一文件中断后重新
// 上传时 init 返回已收分片，只补缺失部分——刷新页面也不丢进度。
// 1MB 分片：公网/弱网上行（~100KB/s）也能在反向代理连接超时（通常 30s）内
// 传完单片，避免分片反复被掐断导致"卡住"的观感；局域网下效率影响可忽略。
// 分片 512KB：弱网上行 30s 内更易传完（平台反代约 30s 会掐断长请求）
const CHUNK_SIZE = 512 * 1024;

function openUpload() {
  $("#uploadDone").hidden = true; // 复位完成动画层
  $("#uploadModal").hidden = false;
}
function closeUpload() {
  $("#uploadModal").hidden = true; // 仅隐藏 UI，后台队列继续跑
  clearUploadDoneTimers();
  $("#uploadDone").hidden = true;
}

// ---- 上传完成动画：全部成功后对钩描边 → 自动关闭弹窗 ----
let uploadDoneTimers = [];
function clearUploadDoneTimers() {
  uploadDoneTimers.forEach(clearTimeout);
  uploadDoneTimers = [];
}

// 队列里没有进行中任务、且无失败项（至少一条成功）时：播对钩动画并自动关窗。
// 有失败项时保持弹窗打开，保留「续传」入口；显示前二次确认，防止刚加的新文件被打断。
function maybeFinishUploads() {
  if (!uploadQueue.length) return;
  if (uploadQueue.some((it) => it.status === "pending" || it.status === "uploading")) return;
  if (uploadQueue.some((it) => it.status === "failed")) return;
  clearUploadDoneTimers();
  uploadDoneTimers.push(setTimeout(() => {
    if (uploadQueue.some((it) => it.status !== "done")) return;
    const done = $("#uploadDone");
    if (!done || $("#uploadModal").hidden) return;
    done.hidden = false; // display 切换会重置并重播 CSS 描边动画
    uploadDoneTimers.push(setTimeout(() => closeUpload(), 2200));
  }, 600));
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
    setItemState(item, "uploading", "校验中…");
    const f = item.file;

    // 秒传预校验：本地算 SHA-256 → 服务端已存在则直接跳过上传
    const sha = await sha256Hex(f);
    if (sha && (await serverHasSha(sha))) {
      item.fill.style.width = "100%";
      setItemState(item, "done", "⚡ 秒传（服务器已有相同文件）");
      setStatus(`秒传：${f.name}（服务器已有，未重复上传）`);
      maybeFinishUploads();
      loadRecordings(true);
      return;
    }

    const totalChunks = Math.ceil(f.size / CHUNK_SIZE) || 1;
    const mtime = Math.floor((f.lastModified || Date.now()) / 1000);

    // init：命中已有会话时返回已收分片，实现断点续传（30s 超时防挂起）
    const initRes = await api("/api/upload/init", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        relPath: f.name, device: "web", mtime,
        size: f.size, chunkSize: CHUNK_SIZE, totalChunks,
      }),
      signal: AbortSignal.timeout(30000),
    });
    if (!initRes.ok) throw new Error("init HTTP " + initRes.status);
    const init = await initRes.json();
    const received = new Set(init.received || []);
    const chunkBytes = (i) => Math.min(CHUNK_SIZE, f.size - i * CHUNK_SIZE);
    let uploadedBytes = 0;
    for (const i of received) uploadedBytes += chunkBytes(i);
    const drawProgress = (extraBytes = 0) => {
      const pct = Math.min(100, Math.round(((uploadedBytes + extraBytes) / f.size) * 100));
      item.fill.style.width = pct + "%";
      setItemState(item, "uploading", `上传中 ${pct}%${received.size ? "（续传）" : ""}`);
    };
    drawProgress();

    for (let i = 0; i < totalChunks; i++) {
      if (received.has(i)) continue; // 断点续传：跳过已传分片
      const blob = f.slice(i * CHUNK_SIZE, Math.min((i + 1) * CHUNK_SIZE, f.size));
      await putChunkWithRetry(init.uploadId, i, blob, (loaded) => drawProgress(loaded));
      uploadedBytes += blob.size;
      drawProgress();
    }

    setItemState(item, "uploading", "合并校验…");
    const compRes = await api(`/api/upload/complete/${init.uploadId}`, {
      method: "POST",
      signal: AbortSignal.timeout(60000), // 合并含 SHA-256 计算，大文件给足时间
    });
    if (!compRes.ok) {
      const d = await compRes.json().catch(() => ({}));
      throw new Error(d.error || "complete HTTP " + compRes.status);
    }
    // dedup=true：同内容文件服务器已有，仅去重命中（未重复存储，返回的 id 是原记录）
    const comp = await compRes.json().catch(() => ({}));
    item.fill.style.width = "100%";
    setItemState(item, "done", comp.dedup
      ? "✓ 已在服务器（去重跳过）"
      : "✓ 已上传" + (received.size ? "（断点续传完成）" : ""));
    setStatus(comp.dedup ? `服务器已有：${f.name}（已去重，未重复存储）` : `已上传：${f.name}`);
    maybeFinishUploads(); // 全部完成时播对钩动画并自动关窗
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

// 单片 PUT（XHR）：fetch 不支持上传进度，XHR 的 upload.onprogress 可提供字节级实时进度。
// token 同时放 query（与 api() 一致，兜底反代剥离 Authorization 头）。
function putChunkOnce(uploadId, index, blob, onProgress) {
  return new Promise((resolve, reject) => {
    const url = new URL(state.server.replace(/\/$/, "") + `/api/upload/chunk/${uploadId}/${index}`);
    if (state.token) url.searchParams.set("token", state.token);
    const xhr = new XMLHttpRequest();
    xhr.open("PUT", url);
    xhr.timeout = 60000; // 单片硬超时：弱网下不无限挂起
    xhr.responseType = "json";
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && onProgress) onProgress(e.loaded);
    };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) resolve(xhr.response || {});
      else reject(new Error("chunk HTTP " + xhr.status));
    };
    xhr.onerror = () => reject(new Error("network error"));
    xhr.ontimeout = () => reject(new Error("timeout"));
    xhr.send(blob);
  });
}

async function putChunkWithRetry(uploadId, index, blob, onProgress, retries = 3) {
  for (let attempt = 0; ; attempt++) {
    try {
      return await putChunkOnce(uploadId, index, blob, onProgress);
    } catch (err) {
      if (onProgress) onProgress(0); // 重试时进度回退归零
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

// 设置弹窗顶部：服务器状态水位（health 接口，无需敏感信息）
async function refreshServerStatus() {
  const box = $("#srvStatus");
  try {
    const res = await api("/api/health");
    if (!res.ok) throw new Error("HTTP " + res.status);
    const d = await res.json();
    const gb = d.diskFreeBytes ? (d.diskFreeBytes / 1024 ** 3).toFixed(0) + " GB" : "未知";
    box.textContent =
      `● 服务器正常 · 磁盘剩余 ${gb} · 录音 ${d.recordings ?? "—"} 条 · 占用 ${fmtSize(d.totalBytes ?? 0)}`;
    box.classList.add("ok");
  } catch (e) {
    box.textContent = "● 无法获取服务器状态（请检查地址与令牌）";
    box.classList.remove("ok");
  }
}

async function refreshSettingsForm() {
  // 服务器状态水位（磁盘 / 条数 / 占用），失败静默
  refreshServerStatus();
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
    if (d.pageSize) {
      state.pageSize = d.pageSize; // 以服务端持久化值为准
      localStorage.setItem("vv_pagesize", String(d.pageSize));
    }
    $("#cfgPageSize").value = String(state.pageSize);
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
    pageSize: Math.max(20, Math.min(100, Number($("#cfgPageSize").value) || 20)),
  };
  try {
    const res = await api("/api/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      const d = await res.json().catch(() => ({}));
      setStatus("设置保存失败：" + (d.error || "HTTP " + res.status));
      return; // 保存失败：保持弹窗打开，修正后可直接重试
    }
    state.asrEnabled = body.asr.enabled;
    // 每页条数：服务端持久化（settings.json）+ 本地缓存双保险
    state.pageSize = body.pageSize;
    localStorage.setItem("vv_pagesize", String(state.pageSize));
  } catch (err) {
    if (err.message === "unauthorized") {
      setStatus("令牌无效：请核对「访问令牌」与服务端 VAULT_TOKEN 一致后再保存。");
    } else {
      setStatus("设置保存失败：" + err.message + "（请检查服务器地址是否可达）");
    }
    return; // 保存失败：保持弹窗打开
  }

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

// 多选管理
$("#manageBtn").onclick = () => setSelectMode(!state.selectMode);
$("#batchCancel").onclick = () => setSelectMode(false);
$("#batchDelete").onclick = batchDelete;
$("#batchExport").onclick = batchExport;
$("#batchAll").onclick = () => {
  // 一次遍历完成：记录选中、勾选框状态、已选大小累计
  document.querySelectorAll(".item").forEach((n) => {
    const id = Number(n.dataset.id);
    state.selected.add(id);
    state.selectedBytes.set(id, Number(n.dataset.size) || 0);
    n.classList.add("selected");
    const chk = n.querySelector("input.pick");
    if (chk) chk.checked = true;
  });
  syncBatchBar();
};
$("#detailModal").addEventListener("click", (e) => {
  if (e.target.id === "detailModal") closeDetail();
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && !$("#detailModal").hidden) closeDetail();
});

// 详情页键盘快捷键：空格 播放/暂停，←/→ 快退/快进 5 秒
// （输入框聚焦时不劫持按键；列表页弹窗关闭时不生效）
document.addEventListener("keydown", (e) => {
  const modal = $("#detailModal");
  if (modal.hidden) return;
  const tag = document.activeElement?.tagName;
  if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;
  const audio = $("#dAudio");
  if (!audio || !audio.src) return;
  if (e.code === "Space") {
    e.preventDefault();
    if (audio.paused) audio.play().catch(() => {});
    else audio.pause();
  } else if (e.key === "ArrowLeft") {
    audio.currentTime = Math.max(0, audio.currentTime - 5);
  } else if (e.key === "ArrowRight") {
    if (isFinite(audio.duration)) {
      audio.currentTime = Math.min(audio.duration, audio.currentTime + 5);
    } else {
      audio.currentTime += 5;
    }
  }
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
  audio.onerror = () => {
    playBtn.textContent = "▶";
    $("#dState").textContent = "浏览器不支持该音频格式，可在 NAS 上下载后本地播放";
  };
  audio.ontimeupdate = () => {
    if (audio.duration && isFinite(audio.duration)) {
      $("#dState").textContent = `${fmtSec(audio.currentTime)} / ${fmtSec(audio.duration)}`;
    } else if (audio.currentTime) {
      $("#dState").textContent = fmtSec(audio.currentTime);
    }
    syncTranscriptHighlight(audio.currentTime);
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

// ---- 筛选器（来源分类 / 月份）----
async function loadFilters() {
  try {
    const cRes = await api("/api/contacts");
    if (cRes.ok) {
      const d = await cRes.json();
      const sel = $("#contactFilter");
      sel.innerHTML = '<option value="">全部来源</option>' +
        (d.items || []).map((c) => {
          const icon = c.kind === "phone" ? "📞" : "💬";
          return `<option value="${escapeHtml(c.contact)}">${icon} ${escapeHtml(c.contact)}（${c.count}）</option>`;
        }).join("");
      sel.value = state.contact;
    }
    const mRes = await api("/api/months");
    if (mRes.ok) {
      const d = await mRes.json();
      const sel = $("#monthFilter");
      sel.innerHTML = '<option value="">全部时间</option>' +
        (d.items || []).map((m) => `<option value="${m.month}">${m.month}（${m.count}）</option>`).join("");
      sel.value = state.month;
    }
  } catch (e) { /* 筛选器加载失败不阻塞列表 */ }
  syncClearBtn();
}

function syncClearBtn() {
  $("#clearFilters").hidden = !state.contact && !state.month;
}

$("#contactFilter").onchange = (e) => {
  state.contact = e.target.value;
  syncClearBtn();
  loadRecordings(true);
};
$("#monthFilter").onchange = (e) => {
  state.month = e.target.value;
  syncClearBtn();
  loadRecordings(true);
};
$("#clearFilters").onclick = () => {
  state.contact = "";
  state.month = "";
  $("#contactFilter").value = "";
  $("#monthFilter").value = "";
  syncClearBtn();
  loadRecordings(true);
};

// ---- 初始化 ----
(async function init() {
  if (!state.token) {
    openSettings("首次使用请填写访问令牌");
    return;
  }
  try {
    await refreshSettingsForm();
  } catch (e) {}
  loadFilters();
  loadRecordings(true);
})();
