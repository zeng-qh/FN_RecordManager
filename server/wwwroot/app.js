// 录音保险箱 VoiceVault 前端逻辑（纯原生 JS，无外部依赖）

const LS_SERVER = "vv_server";
const LS_TOKEN = "vv_token";
const LS_THEME = "vv_theme"; // dark / light / system（head 内联脚本已在渲染前应用过一次）

// ---- 明暗主题 ----
// 三档偏好：深色（默认，保持原有观感）/ 浅色 / 跟随系统。
// system 档跟随操作系统的明暗设置变化，系统切换时页面实时跟随。
const themeMedia = window.matchMedia("(prefers-color-scheme: light)");

function applyTheme() {
  const pref = localStorage.getItem(LS_THEME) || "dark";
  const eff = pref === "system" ? (themeMedia.matches ? "light" : "dark") : pref;
  document.documentElement.dataset.theme = eff;
  const sel = $("#cfgTheme"); // 设置弹窗开着时同步下拉显示
  if (sel) sel.value = pref;
}

// 设置弹窗里的主题下拉：切换立即生效并记住（无需点保存）
function initTheme() {
  applyTheme();
  $("#cfgTheme").onchange = (e) => {
    localStorage.setItem(LS_THEME, e.target.value);
    applyTheme();
  };
  themeMedia.addEventListener("change", () => {
    if ((localStorage.getItem(LS_THEME) || "dark") === "system") applyTheme();
  });
}

// URL ?token= 自动保存：部署完成后打开 http://<ip>:8080/?token=<令牌> 即免填直接进入。
// URL 值始终覆盖旧值（换令牌时新链接即时生效）；随后只从地址栏移除 token 参数，
// 保留其余参数（如发布平台的 sharecode——丢了会导致网关 403、刷新/分享失效）。
(() => {
  const t = new URLSearchParams(location.search).get("token");
  if (t) {
    localStorage.setItem(LS_TOKEN, t);
    const u = new URL(location.href);
    u.searchParams.delete("token");
    history.replaceState(null, "", u.pathname + u.search);
  }
})();

const state = {
  server: localStorage.getItem(LS_SERVER) || location.origin,
  token: localStorage.getItem(LS_TOKEN) || "",
  page: 1,
  pageSize: Number(localStorage.getItem("vv_pagesize")) || 20, // 用户偏好，设置保存后服务端持久化
  loading: false,
  view: "list", // 当前页面：list 录音列表 / transcribe 转写管理 / upload 录音上传 / speakers 声纹库
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

// 时长统一显示 时:分:秒（h:mm:ss）；不足 1 小时也带小时位，保证对齐与「预计耗时」口径一致
function fmtDuration(sec) {
  if (sec == null || !isFinite(sec) || sec <= 0) return null;
  const t = Math.round(sec);
  const h = Math.floor(t / 3600);
  const m = Math.floor((t % 3600) / 60);
  const s = t % 60;
  const p = (n) => String(n).padStart(2, "0");
  return `${h}:${p(m)}:${p(s)}`;
}

// 双时长显示：总时长（文件实际时长）+ 有声音时长（VAD 过滤静音后语音时长）。
// 两者接近（差 <10s）或无语音时长时只显示总时长，避免冗余噪音。
function fmtDurPair(total, speech) {
  const t = fmtDuration(total);
  if (!t) return null;
  const s = fmtDuration(speech);
  if (!s || Math.abs((total || 0) - (speech || 0)) < 10) return t;
  return `${t}（有声 ${s}）`;
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
  const dur = fmtDurPair(it.durationSec, it.speechSec);
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
    // 双时长渲染：初始显示与播放器校正后共用
    const renderDur = () => {
      const dur = fmtDurPair(rec.durationSec, rec.speechSec);
      $("#dMeta").textContent =
        `${fmtDate(rec.recordedAt)} ${fmtClock(rec.recordedAt)}`
        + `${dur ? " · 时长 " + dur : ""} · ${fmtSize(rec.size)} · 设备 ${rec.device}`;
    };
    renderDur();
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
    // 播放器元数据加载后校正时长：早期版本时长取「最后一段语音的结束时间」，
    // 尾部静音较长的录音（如微信挂机忘停）会偏短很多；此处以容器真实时长
    // 为准回写并刷新界面（差 >10s 才校正，避免与转写时长的小幅差异抖动）
    audio.onloadedmetadata = () => {
      const real = audio.duration;
      if (!isFinite(real) || real <= 0) return;
      if (Math.abs(real - (rec.durationSec || 0)) > 10) {
        rec.durationSec = real;
        renderDur(); // 以容器真实时长为准刷新显示（有声时长保持转写值）
        api("/api/recordings/durations", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify([{ id, durationSec: Math.round(real * 10) / 10 }]),
        }).catch(() => {});
      }
    };
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
  renderDetailSpeakers(data);
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

// ---- 说话人条（声纹注册入口）----
// data.speakers 由服务端动态认名后返回：label=聚类标签（说话人N），
// name=注册名（未注册为 null）。点 chip 弹出注册框：输入名字即把该
// 说话人的全部转写段作为声纹样本注册；已注册的可再次提交追加样本。
function renderDetailSpeakers(data) {
  const bar = $("#dSpeakers");
  if (!bar) return;
  const spks = (data && data.speakers) || [];
  if (!spks.length) { bar.hidden = true; bar.innerHTML = ""; return; }
  bar.hidden = false;
  bar.innerHTML = "";
  const tip = el("span", "spk-tip", "说话人：");
  bar.appendChild(tip);
  for (const s of spks) {
    const chip = el("button", "spk-chip" + (s.name ? " named" : ""));
    chip.title = s.name
      ? `已注册为「${s.name}」· 点击可从本条录音追加声纹样本`
      : "点击注册声纹：标注真名后全库自动认人";
    chip.innerHTML =
      `<span class="spk-name">${escapeHtml(s.name || s.label)}</span>` +
      (s.name ? `<span class="spk-sub">${escapeHtml(s.label)}</span>` : `<span class="spk-sub">📌 注册</span>`);
    chip.onclick = () => openSpkEnroll(detailId, s.label, s.name);
    bar.appendChild(chip);
  }
}

let spkEnrollCtx = null; // { recordId, label } 注册弹窗当前上下文

function openSpkEnroll(recordId, label, existingName) {
  spkEnrollCtx = { recordId, label };
  $("#spkTitle").textContent = "注册说话人";
  $("#spkHint").textContent =
    `把本条录音里的「${label}」标注成真名。将提取该说话人全部发言段的声纹；` +
    (existingName ? `当前已注册为「${existingName}」，另填新名可将其归入该人。` : "注册后全库自动认人。");
  $("#spkName").value = existingName || "";
  $("#spkModal").hidden = false;
  $("#spkName").focus();
}

function closeSpkModal() {
  $("#spkModal").hidden = true;
  spkEnrollCtx = null;
}

async function submitSpkEnroll() {
  if (!spkEnrollCtx) return;
  const name = $("#spkName").value.trim();
  if (!name) { $("#spkName").focus(); return; }
  const { recordId, label } = spkEnrollCtx;
  const btn = $("#spkSave");
  btn.disabled = true;
  btn.textContent = "提取声纹中…";
  try {
    const res = await api("/api/speakers", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name, recordId, speakerLabel: label }),
    });
    const d = await res.json().catch(() => ({}));
    if (res.ok) {
      setStatus(`已注册说话人「${name}」（声纹样本 ${d.seconds ?? "—"} 秒）✓`);
      closeSpkModal();
      // 重新拉取：服务端按新声纹库认名，文字稿立即显示注册名
      loadDetailTranscript(recordId);
      loadSpeakerLib(); // 设置页列表（若打开过）同步刷新
    } else {
      alert("注册失败：" + (d.error || `HTTP ${res.status}`));
    }
  } catch (e) {
    alert("注册失败：网络异常或服务器不可达。");
  } finally {
    btn.disabled = false;
    btn.textContent = "保存";
  }
}
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

// 秒传探测：命中（库内已有同哈希文件）返回 { id, sha }，未命中/失败返回 null。
// id 供秒传后对原记录做时长兜底回填（原记录可能尚无时长，影响预计耗时估算）。
async function serverHasSha(sha) {
  try {
    const res = await api("/api/upload/exists", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify([sha]),
    });
    if (!res.ok) return null;
    const d = await res.json();
    const hit = (d.existing || []).find((x) => (x.sha || "").toLowerCase() === sha);
    return hit ? { id: hit.id, sha: hit.sha } : null;
  } catch {
    return null; // 探测失败不阻塞上传，走正常流程
  }
}

// ---- 网页端上传（弹层 + 512KB 分片 + 断点续传）----
// uploadId 由服务端按 (device|relPath|size|mtime) 派生：同一文件中断后重新
// 上传时 init 返回已收分片，只补缺失部分——刷新页面也不丢进度。
// 1MB 分片：公网/弱网上行（~100KB/s）也能在反向代理连接超时（通常 30s）内
// 传完单片，避免分片反复被掐断导致"卡住"的观感；局域网下效率影响可忽略。
// 分片 512KB：弱网上行 30s 内更易传完（平台反代约 30s 会掐断长请求）
const CHUNK_SIZE = 512 * 1024;

// 上传为独立页面（无弹窗）：队列常驻显示，切到其他页面后任务仍在后台继续跑

// ---- 上传完成动画：全部成功后对钩描边 → 自动隐藏覆盖层（页面保留，可继续添加文件） ----
let uploadDoneTimers = [];
function clearUploadDoneTimers() {
  uploadDoneTimers.forEach(clearTimeout);
  uploadDoneTimers = [];
}

// 队列里没有进行中任务、且无失败项（至少一条成功）时：播对钩动画后自动隐藏。
// 有失败项时保持覆盖层不出现，保留「续传」入口；显示前二次确认，防止刚加的新文件被打断。
function maybeFinishUploads() {
  if (!uploadQueue.length) return;
  if (uploadQueue.some((it) => it.status === "pending" || it.status === "uploading")) return;
  if (uploadQueue.some((it) => it.status === "failed")) return;
  clearUploadDoneTimers();
  uploadDoneTimers.push(setTimeout(() => {
    if (uploadQueue.some((it) => it.status !== "done")) return;
    const done = $("#uploadDone");
    if (!done) return;
    done.hidden = false; // display 切换会重置并重播 CSS 描边动画
    uploadDoneTimers.push(setTimeout(() => { done.hidden = true; }, 2200));
  }, 600));
}

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

// 浏览器元数据读时长并回填服务端（服务端只填空缺值，不覆盖转写得到的精确时长）。
// 秒传命中 / 正常上传完成 / dedup 去重命中三条路径共用；用完即回收 objectURL。
function probeAndBackfill(file, id) {
  if (!id) return;
  const url = URL.createObjectURL(file);
  probeFileDuration(url)
    .then((dur) => {
      URL.revokeObjectURL(url);
      if (dur <= 0) return;
      return api("/api/recordings/durations", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify([{ id, durationSec: Math.round(dur * 10) / 10 }]),
      });
    })
    .catch(() => URL.revokeObjectURL(url));
}

async function runUploadItem(item) {
  try {
    setItemState(item, "uploading", "校验中…");
    const f = item.file;

    // 秒传预校验：本地算 SHA-256 → 服务端已存在则直接跳过上传
    const sha = await sha256Hex(f);
    const hit = sha ? await serverHasSha(sha) : null;
    if (hit) {
      item.fill.style.width = "100%";
      setItemState(item, "done", "⚡ 秒传（服务器已有相同文件）");
      setStatus(`秒传：${f.name}（服务器已有，未重复上传）`);
      maybeFinishUploads();
      loadRecordings(true);
      probeAndBackfill(f, hit.id); // 原记录时长兜底回填
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
    // 异步补写录音时长（浏览器元数据读出，供转写「预计耗时」估算；
    // 服务端只回填空缺值，不会覆盖转写得到的精确时长）
    probeAndBackfill(f, comp.id);
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
  applyTheme(); // 主题下拉回显当前偏好（可能被其他标签页改过）
  if (msg) setStatus(msg);
  refreshSettingsForm();
}
function closeSettings() {
  $("#settingsModal").hidden = true;
}

// ---- 声纹库页面（主界面独立视图：档案卡片 + 详情面板）----
let spkCurrent = null; // 当前选中的档案（详情面板渲染/改名/删除用）

// createdAt 兼容解析：服务端返回 ISO 字符串或时间戳（秒/毫秒），统一转毫秒
function parseTs(v) {
  if (typeof v === "number") return v < 1e12 ? v * 1000 : v;
  if (typeof v === "string") { const t = Date.parse(v); return isNaN(t) ? 0 : t; }
  return 0;
}
function spkRegDate(v) {
  const ms = parseTs(v);
  return ms ? "注册于 " + fmtDate(ms / 1000) : "";
}

function showView(name) {
  state.view = name;
  $("#viewList").hidden = name !== "list";
  $("#viewTranscribe").hidden = name !== "transcribe";
  $("#viewUpload").hidden = name !== "upload";
  $("#viewSpeakers").hidden = name !== "speakers";
  // 顶栏导航按钮高亮当前页
  $("#tBtn").classList.toggle("active", name === "transcribe");
  $("#speakersBtn").classList.toggle("active", name === "speakers");
  $("#uploadBtn").classList.toggle("active", name === "upload");
  // 搜索 / 多选管理只在录音列表页有意义；上传页没有可刷新的数据
  $("#search").hidden = name !== "list";
  $("#manageBtn").hidden = name !== "list";
  $("#refresh").hidden = name === "upload";
  // 离开列表页时退出多选模式（回列表时 loadRecordings 会按普通模式重绘）
  if (name !== "list" && state.selectMode) {
    state.selectMode = false;
    state.selected.clear();
    state.selectedBytes.clear();
    syncBatchBar();
  }
  // 转写页的 3s 轮询只在该页打开期间运行
  if (name === "transcribe") {
    tState.open = true;
    tState.probeTried.clear();
    refreshTranscribe(true);
  } else if (tState.open) {
    tState.open = false;
    stopTPoll();
  }
  if (name === "speakers") loadSpeakerLib();
  else if (name === "list") loadRecordings(true);
}

async function loadSpeakerLib() {
  const box = $("#spkPageList");
  if (!box) return;
  try {
    const res = await api("/api/speakers");
    if (!res.ok) throw new Error("HTTP " + res.status);
    const d = await res.json();
    const items = d.items || [];
    const nSamples = items.reduce((n, p) => n + (p.samples || []).length, 0);
    $("#spkPageStats").textContent = items.length
      ? `${items.length} 个档案 · ${nSamples} 个注册样本`
      : "";
    box.innerHTML = items.length
      ? ""
      : '<div class="spk-empty">暂无注册声纹。打开任一条已转写录音，点文字稿上方的说话人标签即可注册。</div>';
    let prev = null;
    for (const p of items) {
      const card = el("div", "spk-pcard");
      const sources = [...new Set((p.samples || []).map((s) => `#${s.recordId}`))]
        .filter((s) => s !== "#null")
        .join("、");
      card.innerHTML =
        `<span class="spk-avatar">🗣️</span>` +
        `<span class="spk-info"><b>${escapeHtml(p.name)}</b>` +
        `<small>${(p.samples || []).length} 个样本${sources ? " · 来自 " + escapeHtml(sources) : ""}</small></span>`;
      card.onclick = () => selectSpeakerProfile(p, card);
      if (spkCurrent && spkCurrent.id === p.id) {
        prev = p; // 重渲染后保持选中（改名/删除/注册样本刷新场景）
        card.classList.add("selected");
      }
      box.appendChild(card);
    }
    // 保持选中：用新数据重渲染详情面板；无选中时默认选第一个
    const target = prev || items[0];
    if (target) {
      const card = [...box.children][items.indexOf(target)];
      selectSpeakerProfile(target, card);
    } else {
      spkCurrent = null;
      $("#spkPageDetail").innerHTML =
        '<div class="spk-empty">暂无声纹档案。在录音详情页点说话人标签即可注册。</div>';
    }
  } catch (e) {
    box.innerHTML = '<div class="spk-empty">声纹库加载失败（请检查服务器连接）</div>';
  }
}

function selectSpeakerProfile(p, cardEl) {
  spkCurrent = p;
  // 选中态
  [...$("#spkPageList").children].forEach((c) => c.classList.remove("selected"));
  if (cardEl) cardEl.classList.add("selected");

  const panel = $("#spkPageDetail");
  const samples = p.samples || [];
  panel.innerHTML = `
    <div class="spk-pd-head">
      <span class="spk-avatar">🗣️</span>
      <div class="spk-pd-title"><b>${escapeHtml(p.name)}</b><small>${spkRegDate(p.createdAt)}</small></div>
      <div class="spk-pd-actions">
        <button id="spkRename">✏️ 改名</button>
        <button id="spkDelete" class="danger">🗑 删除</button>
      </div>
    </div>
    <h3>注册样本</h3>
    <div id="spkDetailSamples" class="spk-occ-list"></div>
    <h3>出现的录音</h3>
    <div class="idle-hint">按注册声纹比对全部记录的说话人簇（阈值内即列出）；点击跳转详情可继续标注或试听。</div>
    <div id="spkOccurrences" class="spk-occ-list"><div class="spk-empty">加载中…</div></div>`;

  const sBox = panel.querySelector("#spkDetailSamples");
  sBox.innerHTML = samples.length
    ? ""
    : '<div class="spk-empty">无样本（异常状态，建议删除后重新注册）</div>';
  for (const s of samples) {
    const r = el("div", "spk-occ-row");
    r.innerHTML =
      `<span class="spk-occ-name">${s.recordId ? "录音 #" + s.recordId : "外部提取"}</span>` +
      `<small>${escapeHtml(s.segLabel || "")}${s.recordId ? " · 点击打开详情" : ""}</small>`;
    if (s.recordId) r.onclick = () => openDetail(s.recordId);
    sBox.appendChild(r);
  }

  const oBox = panel.querySelector("#spkOccurrences");
  api(`/api/speakers/${p.id}/occurrences`)
    .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
    .then((d) => {
      if (spkCurrent !== p) return; // 已切换到其他档案，丢弃过期响应
      const items = d.items || [];
      oBox.innerHTML = items.length
        ? ""
        : '<div class="spk-empty">没有在其他录音里识别到该声音（相似度未达阈值）。</div>';
      for (const it of items) {
        const r = el("div", "spk-occ-row");
        r.innerHTML =
          `<span class="spk-occ-name">${escapeHtml(it.title || "录音 #" + it.recordId)}</span>` +
          `<small>${fmtDate(parseTs(it.recordedAt) / 1000)} · ${escapeHtml(it.label || "")} · 相似 ${(it.score * 100).toFixed(0)}%</small>`;
        r.onclick = () => openDetail(it.recordId);
        oBox.appendChild(r);
      }
    })
    .catch(() => {
      if (spkCurrent === p)
        oBox.innerHTML = '<div class="spk-empty">出现记录加载失败（请检查服务器连接）</div>';
    });

  panel.querySelector("#spkRename").onclick = async () => {
    const name = prompt("修改声纹名字（改名后全库记录立即按新名字显示）：", p.name);
    if (!name || name.trim() === p.name) return;
    try {
      const r2 = await api(`/api/speakers/${p.id}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: name.trim() }),
      });
      if (r2.ok) { setStatus(`声纹已改名：${p.name} → ${name.trim()}`); loadSpeakerLib(); }
      else { const e2 = await r2.json().catch(() => ({})); alert("改名失败：" + (e2.error || r2.status)); }
    } catch (e) { alert("改名失败：" + e.message); }
  };
  panel.querySelector("#spkDelete").onclick = async () => {
    if (!confirm(`删除声纹「${p.name}」？所有记录里该名字将恢复为「说话人N」。`)) return;
    try {
      const r2 = await api(`/api/speakers/${p.id}`, { method: "DELETE" });
      if (r2.ok) { setStatus(`已删除声纹「${p.name}」`); spkCurrent = null; loadSpeakerLib(); }
      else { const e2 = await r2.json().catch(() => ({})); alert("删除失败：" + (e2.error || r2.status)); }
    } catch (e) { alert("删除失败：" + e.message); }
  };
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
      `● 服务器正常 · v${d.version || "—"} · 磁盘剩余 ${gb} · 录音 ${d.recordings ?? "—"} 条 · 占用 ${fmtSize(d.totalBytes ?? 0)}`;
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
    // 说话人分段依赖边车声纹模型：未就绪时提示勾选无效，避免「勾了没效果」的困惑
    const dHint = $("#cfgAsrDiarizeHint");
    if (dHint) {
      const ok = !!d.diarizeReady;
      dHint.textContent = ok ? "✓ 声纹模型已就绪" : "⚠ 边车未安装声纹模型（勾选无效）";
      dHint.className = ok ? "diarize-ok" : "diarize-warn";
    }
    $("#cfgRetDays").value = d.retention?.days ?? 0;
    const gb = d.retention?.maxBytes ?? 0;
    $("#cfgRetGb").value = gb > 0 ? Math.max(1, Math.round(gb / (1024 ** 3))) : 0;
    // 手机无线导入（ADB）回填
    const ai = d.adbImport || {};
    $("#cfgAdbEnabled").checked = !!ai.enabled;
    $("#cfgAdbInterval").value = ai.intervalMinutes ?? 30;
    $("#cfgAdbHosts").value = ai.hosts || "";
    $("#cfgAdbDirs").value = ai.remoteDirs || "";
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
    adbImport: {
      enabled: $("#cfgAdbEnabled").checked,
      intervalMinutes: Math.max(5, Math.min(1440, Number($("#cfgAdbInterval").value) || 30)),
      hosts: $("#cfgAdbHosts").value.trim(),
      remoteDirs: $("#cfgAdbDirs").value.trim(),
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
// 刷新当前页面：此前固定刷录音列表，导致声纹库页点刷新「没反应」，现按视图分发
$("#refresh").onclick = () => {
  if (state.view === "transcribe") {
    tState.probeTried.clear();
    refreshTranscribe(true);
  } else if (state.view === "speakers") {
    loadSpeakerLib();
  } else {
    loadFilters();
    loadRecordings(true);
  }
};
$("#settingsBtn").onclick = () => openSettings();
$("#saveSettings").onclick = saveSettings;
initTheme(); // 明暗主题：绑定设置下拉 + 跟随系统监听

// 顶栏版本号：展示服务端部署版本（health 为匿名接口，走裸 fetch 静默失败，
// 不经 api() 以免 401 触发设置引导弹窗——首次使用未配令牌时不打扰）
(async () => {
  try {
    const res = await fetch(state.server.replace(/\/$/, "") + "/api/health");
    if (!res.ok) return;
    const d = await res.json();
    if (d.version) $("#brandVersion").textContent = " · v" + d.version;
  } catch (e) { /* 服务器不可达时顶栏不显示版本 */ }
})();
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

// 声纹注册弹窗：保存 / 取消 / 回车提交 / Esc 或点遮罩关闭
$("#spkSave").onclick = submitSpkEnroll;
$("#spkCancel").onclick = closeSpkModal;
$("#spkName").addEventListener("keydown", (e) => {
  if (e.key === "Enter") { e.preventDefault(); submitSpkEnroll(); }
});
$("#spkModal").addEventListener("click", (e) => {
  if (e.target.id === "spkModal") closeSpkModal();
});

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
  if (e.key === "Escape") {
    if (!$("#spkModal").hidden) closeSpkModal();
    else if (!$("#detailModal").hidden) closeDetail();
  }
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

// 顶栏导航：转写管理 / 录音上传 / 声纹库 页面切换（录音列表为默认页）
$("#tBtn").onclick = () => showView("transcribe");
$("#uploadBtn").onclick = () => showView("upload");
$("#speakersBtn").onclick = () => showView("speakers");

// 点击顶栏品牌（🎙️ 录音保险箱）回到首页：showView("list") 内部会刷新录音列表
$("#brand").onclick = () => showView("list");

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

// ---- 转写管理 ----
// 需求：一键转写全部未转写 / 按来源分组勾选转写 / 「不转写」标记（批量转写自动跳过）/
// 文件名或正则过滤 / 勾选后按时长估算预计耗时 / 实际耗时展示 / 时长统一 h:mm:ss。
// 现为独立页面（带分页）：进入页面即刷新，离开页面自动停止轮询（showView 负责）。
const tState = {
  open: false,            // 仅标记「转写管理页当前打开」，控制轮询与回填刷新
  view: "todo",           // todo / pending / done / skip / all
  page: 1,                // 当前页码（前端分页，过滤后条目切片分组）
  pageSize: Number(localStorage.getItem("vv_tpagesize")) || 50, // 每页条数偏好
  filter: "",             // 文件名/标题过滤（regex 开关启用正则）
  regex: false,
  items: [],
  counts: null,
  speed: 4.0,             // 速度因子（实时倍率），服务端按实际转写耗时自适应
  serverNow: 0,           // 服务端时间（秒），校准「已进行」计时（避免客户端时钟偏差）
  fetchedAt: 0,           // 本次拉取的本地时刻（秒）
  selected: new Set(),
  selDuration: new Map(), // id → 语音时长（批量栏累计/预计耗时；speechSec 缺省用 durationSec）
  pollTimer: null,
  probeTried: new Set(),  // 本次会话已尝试探测时长的 id（防失败反复重试）
  backfillBusy: false,
};

async function refreshTranscribe(resetSelection = false) {
  if (resetSelection) { tState.selected.clear(); tState.selDuration.clear(); }
  try {
    const res = await api(`/api/transcribe/list?status=${encodeURIComponent(tState.view)}`);
    if (!res.ok) throw new Error("HTTP " + res.status);
    const d = await res.json();
    tState.items = d.items || [];
    tState.counts = d.counts || {};
    tState.speed = d.speed > 0 ? d.speed : 4.0;
    tState.serverNow = d.now || Math.floor(Date.now() / 1000);
    tState.fetchedAt = Date.now() / 1000;
    // 清掉已不在当前视图的选中项（如转写完成后移出待转写视图）
    const ids = new Set(tState.items.map((it) => it.id));
    for (const id of [...tState.selected]) {
      if (!ids.has(id)) { tState.selected.delete(id); tState.selDuration.delete(id); }
    }
    renderTranscribe();
    scheduleTPoll();
    backfillDurations();
  } catch (e) {
    if (e.message !== "unauthorized") $("#tStats").textContent = "加载失败：" + e.message;
  }
}

// 弹窗打开且有转写任务进行中时 3s 轮询（进度/已进行/实际耗时实时更新）
function scheduleTPoll() {
  stopTPoll();
  if (!tState.open) return;
  const c = tState.counts || {};
  if ((c.pending || 0) > 0 || tState.view === "pending") {
    tState.pollTimer = setTimeout(() => refreshTranscribe(false), 3000);
  }
}
function stopTPoll() {
  if (tState.pollTimer) { clearTimeout(tState.pollTimer); tState.pollTimer = null; }
}

// 过滤：普通模式子串匹配（不分大小写）；正则模式整串按正则匹配（忽略大小写）
function tMatch(it) {
  const s = tState.filter.trim();
  if (!s) return true;
  const target = `${it.title || ""} ${it.fileName || ""}`;
  if (!tState.regex) return target.toLowerCase().includes(s.toLowerCase());
  try { return new RegExp(s, "i").test(target); } catch { return true; }
}
function tRegexInvalid() {
  if (!tState.regex || !tState.filter.trim()) return false;
  try { new RegExp(tState.filter.trim(), "i"); return false; } catch { return true; }
}

function renderTranscribe() {
  const c = tState.counts || {};
  const allEst = (c.todoDurationSec || 0) / tState.speed;
  $("#tStats").innerHTML =
    `待转写 <b>${c.todo ?? 0}</b> · 转写中 <b>${c.pending ?? 0}</b> · ` +
    `已转写 <b>${c.done ?? 0}</b> · 失败 <b>${c.error ?? 0}</b> · 不转写 <b>${c.skipped ?? 0}</b>` +
    `<span class="t-speed"> · 速度 ≈ ${tState.speed.toFixed(1)}x 实时</span>`;
  const allBtn = $("#tEnqueueAll");
  allBtn.textContent = (c.todo || 0) > 0
    ? `⚡ 一键转写全部（${c.todo} 条${allEst > 1 ? ` · 约 ${fmtDuration(allEst)}` : ""}）`
    : "⚡ 一键转写全部（无待转写）";
  allBtn.disabled = !(c.todo > 0);

  const invalid = tRegexInvalid();
  $("#tFilter").classList.toggle("err", invalid);
  $("#tFilterHint").textContent = invalid ? "正则表达式无效" : "";

  // 分页：过滤后的条目切片分组（页码越界时自动收敛到最后一页）
  const filtered = tState.items.filter(tMatch);
  const totalPages = Math.max(1, Math.ceil(filtered.length / tState.pageSize));
  if (tState.page > totalPages) tState.page = totalPages;
  if (tState.page < 1) tState.page = 1;
  $("#tPageInfo").textContent = `第 ${tState.page} / ${totalPages} 页 · 共 ${filtered.length} 条`;
  $("#tPrevPage").disabled = tState.page <= 1;
  $("#tNextPage").disabled = tState.page >= totalPages;

  const list = $("#tList");
  list.innerHTML = "";
  const items = filtered.slice((tState.page - 1) * tState.pageSize, tState.page * tState.pageSize);
  if (!items.length) {
    list.appendChild(el("div", "t-empty",
      tState.filter ? "没有匹配过滤条件的录音。" : "这里空空如也。"));
    syncTBatch();
    return;
  }
  // 按来源分组（未分类的归「未分类」）；组头可整组勾选/转写
  const groups = new Map();
  for (const it of items) {
    const key = it.contact || "";
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(it);
  }
  for (const [contact, arr] of groups) {
    const g = el("div", "t-group");
    const head = el("div", "t-group-head");
    const gchk = el("input", "pick");
    gchk.type = "checkbox";
    const allSel = arr.every((it) => tState.selected.has(it.id));
    gchk.checked = allSel;
    gchk.indeterminate = !allSel && arr.some((it) => tState.selected.has(it.id));
    gchk.onchange = () => {
      for (const it of arr) {
        if (gchk.checked) { tState.selected.add(it.id); tState.selDuration.set(it.id, it.speechSec || it.durationSec || 0); }
        else { tState.selected.delete(it.id); tState.selDuration.delete(it.id); }
      }
      renderTranscribe();
    };
    const icon = contact ? (arr[0].contactKind === "phone" ? "📞" : "💬") : "📁";
    const totalDur = arr.reduce((a, b) => a + (b.durationSec || 0), 0);
    head.appendChild(gchk);
    head.appendChild(el("div", "t-group-name",
      `${icon} ${escapeHtml(contact || "未分类")}` +
      `<small>${arr.length} 条 · ${fmtDuration(totalDur) || "时长未知"}</small>`));
    const gBtn = el("button", null, "⚡ 转写本组");
    gBtn.title = "把该组可转写的条目加入队列（已标记不转写的自动跳过）";
    gBtn.onclick = () => tEnqueue(arr.filter((it) => !it.noTranscribe).map((it) => it.id), arr.length);
    head.appendChild(gBtn);
    g.appendChild(head);
    for (const it of arr) g.appendChild(renderTRow(it));
    list.appendChild(g);
  }
  syncTBatch();
}

function renderTRow(it) {
  const row = el("div", "t-row" + (it.noTranscribe ? " skip" : ""));
  if (tState.selected.has(it.id)) row.classList.add("selected");
  const chk = el("input", "pick");
  chk.type = "checkbox";
  chk.checked = tState.selected.has(it.id);
  chk.onchange = () => {
    if (chk.checked) { tState.selected.add(it.id); tState.selDuration.set(it.id, it.speechSec || it.durationSec || 0); }
    else { tState.selected.delete(it.id); tState.selDuration.delete(it.id); }
    row.classList.toggle("selected", chk.checked);
    syncTBatch();
  };

  const meta = el("div", "t-meta");
  meta.appendChild(el("div", "t-name", escapeHtml(it.title || it.fileName)));

  // 状态行：时长 + （已进行/实际耗时/预计耗时）+ 徽标
  const bits = [it.durationSec ? (fmtDurPair(it.durationSec, it.speechSec) || "时长探测中…") : "时长探测中…"];
  if (it.transcriptStatus === "pending") {
    if (it.transcribeStartedAt > 0) {
      const elapsed = Math.max(0, (tState.serverNow + (Date.now() / 1000 - tState.fetchedAt)) - it.transcribeStartedAt);
      bits.push(`已进行 ${fmtDuration(elapsed)}`);
    }
  } else if (it.transcriptStatus === "done" && it.transcribeMs > 0) {
    bits.push(`实际耗时 ${fmtDuration(it.transcribeMs / 1000)}`);
  } else if (!it.noTranscribe && (it.speechSec || it.durationSec) && tState.speed > 0 &&
             ["none", "idle", "error"].includes(it.transcriptStatus || "none")) {
    bits.push(`预计 ${fmtDuration((it.speechSec || it.durationSec) / tState.speed)}`);
  }
  const badgeHtml = it.noTranscribe
    ? '<span class="badge skipb">🚫 不转写</span>'
    : statusBadge(it);
  meta.appendChild(el("div", "t-sub",
    `<span>${escapeHtml(bits.join(" · "))}</span> ${badgeHtml}`));

  const ops = el("div", "t-ops");
  const mark = el("button", null, it.noTranscribe ? "↩" : "🚫");
  mark.title = it.noTranscribe ? "取消「不转写」标记" : "标记为「不转写」（批量/一键转写将跳过）";
  mark.onclick = () => tSetSkip([it.id], !it.noTranscribe);
  ops.appendChild(mark);

  row.append(chk, meta, ops);
  return row;
}

// 批量栏：已选条数 + 总时长 + 按速度因子估算的预计耗时
function syncTBatch() {
  const bar = $("#tBatchBar");
  bar.hidden = tState.selected.size === 0;
  if (bar.hidden) return;
  let dur = 0;
  for (const v of tState.selDuration.values()) dur += v;
  const est = tState.speed > 0 ? dur / tState.speed : 0;
  $("#tBatchInfo").innerHTML =
    `已选 <b>${tState.selected.size}</b> 条 · 总时长 <b>${fmtDuration(dur) || "—"}</b>` +
    (dur > 0
      ? (est > 1 ? ` · 预计约 <b>${fmtDuration(est)}</b>` : " · 预计耗时不足 1 秒")
      : " · 预计耗时需先探测时长");
}

async function tEnqueue(ids, total = ids.length) {
  if (!ids.length) { setStatus("所选条目均已标记不转写或已转写，无需操作。"); return; }
  try {
    const res = await api("/api/transcribe/enqueue", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids }),
    });
    if (!res.ok) {
      const d = await res.json().catch(() => ({}));
      alert("无法转写：" + (d.error || res.status));
      return;
    }
    const d = await res.json();
    const skipNote = total > ids.length ? `（${total - ids.length} 条已标记不转写，自动跳过）` : "";
    setStatus(`已把 ${d.queued} 条录音加入转写队列${skipNote}${d.skipped ? ` · 另有 ${d.skipped} 条不可转写已忽略` : ""}。`);
    refreshTranscribe(true);
  } catch (e) {}
}

async function tEnqueueAll() {
  if (!state.asrEnabled) { alert("请先在设置里开启「启用语音转文字」并保存。"); return; }
  const c = tState.counts || {};
  if (!(c.todo > 0)) return;
  const est = (c.todoDurationSec || 0) / tState.speed;
  const estTxt = est > 1
    ? `，预计约 ${fmtDuration(est)}（按当前速度 ${tState.speed.toFixed(1)}x 估算；转写与 NAS 其他任务并行时可能更慢）`
    : "";
  if (!confirm(`把全部 ${c.todo} 条未转写录音加入队列？将串行执行${estTxt}。`)) return;
  try {
    const res = await api("/api/transcribe/enqueue", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ all: true }),
    });
    if (!res.ok) {
      const d = await res.json().catch(() => ({}));
      alert("无法批量转写：" + (d.error || res.status));
      return;
    }
    const d = await res.json();
    setStatus(`已把 ${d.queued} 条录音加入转写队列。`);
    refreshTranscribe(true);
  } catch (e) {}
}

async function tSetSkip(ids, value) {
  try {
    const res = await api("/api/transcribe/skip", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids, value }),
    });
    if (!res.ok) {
      const d = await res.json().catch(() => ({}));
      alert("操作失败：" + (d.error || res.status));
      return;
    }
    const d = await res.json();
    setStatus(value
      ? `已把 ${d.updated} 条录音标记为「不转写」。`
      : `已取消 ${d.updated} 条录音的「不转写」标记。`);
    refreshTranscribe(false);
  } catch (e) {}
}

// ---- 未转写录音的时长回填 ----
// 未转写录音没有时长（转写完才有精确值），而「预计耗时」估算需要它：
// 前端用 <audio> 元数据（preload=metadata，仅按 Range 拉头部）读出后写回服务端，
// 不依赖边车/容器卷挂载，浏览器解码器保证格式兼容。
function probeFileDuration(src) {
  return new Promise((resolve) => {
    const a = new Audio();
    const to = setTimeout(() => { a.src = ""; resolve(0); }, 8000);
    a.preload = "metadata";
    a.onloadedmetadata = () => { clearTimeout(to); resolve(isFinite(a.duration) ? a.duration : 0); };
    a.onerror = () => { clearTimeout(to); resolve(0); };
    a.src = src;
  });
}

async function backfillDurations() {
  const missing = tState.items
    .filter((it) => !it.durationSec && !tState.probeTried.has(it.id))
    .slice(0, 20);
  if (!missing.length || tState.backfillBusy) return;
  tState.backfillBusy = true;
  const base = state.server.replace(/\/$/, "");
  const patches = [];
  for (const it of missing) {
    tState.probeTried.add(it.id);
    try {
      const dur = await probeFileDuration(
        `${base}/api/recordings/${it.id}/file?token=${encodeURIComponent(state.token)}`);
      if (dur > 0) patches.push({ id: it.id, durationSec: Math.round(dur * 10) / 10 });
    } catch { /* 探测失败跳过 */ }
  }
  tState.backfillBusy = false;
  if (!patches.length) return;
  try {
    await api("/api/recordings/durations", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(patches),
    });
    if (tState.open) refreshTranscribe(false);
  } catch { /* 回填失败不影响使用 */ }
}

// ---- 转写管理事件绑定（独立页面）----
$("#tView").onchange = (e) => { tState.view = e.target.value; tState.page = 1; refreshTranscribe(true); };
$("#tPageSize").value = String(tState.pageSize);
$("#tPageSize").onchange = (e) => {
  tState.pageSize = Number(e.target.value) || 50;
  localStorage.setItem("vv_tpagesize", String(tState.pageSize));
  tState.page = 1;
  renderTranscribe();
};
$("#tPrevPage").onclick = () => {
  if ($("#tPrevPage").disabled) return;
  tState.page -= 1;
  renderTranscribe();
  $("#tList").scrollTop = 0;
};
$("#tNextPage").onclick = () => {
  if ($("#tNextPage").disabled) return;
  tState.page += 1;
  renderTranscribe();
  $("#tList").scrollTop = 0;
};
let tFilterTimer;
$("#tFilter").oninput = (e) => {
  clearTimeout(tFilterTimer);
  tFilterTimer = setTimeout(() => { tState.filter = e.target.value; tState.page = 1; renderTranscribe(); }, 200);
};
$("#tRegexChk").onchange = (e) => { tState.regex = e.target.checked; renderTranscribe(); };
$("#tEnqueueAll").onclick = tEnqueueAll;
$("#tBatchEnqueue").onclick = () => tEnqueue([...tState.selected]);
$("#tBatchSkip").onclick = () => tSetSkip([...tState.selected], true);
$("#tBatchUnskip").onclick = () => tSetSkip([...tState.selected], false);
$("#tBatchCancel").onclick = () => {
  tState.selected.clear();
  tState.selDuration.clear();
  renderTranscribe();
};
$("#tBatchAll").onclick = () => {
  // 全选当前过滤后的可见条目（时长口径与单选/组勾选一致：有声时长优先）
  tState.items.filter(tMatch).forEach((it) => {
    tState.selected.add(it.id);
    tState.selDuration.set(it.id, it.speechSec || it.durationSec || 0);
  });
  renderTranscribe();
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
