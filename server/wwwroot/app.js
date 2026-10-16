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
  vBtn.onclick = () => toggleTranscript(it.id, vBtn);
  ops.appendChild(vBtn);

  item.append(play, meta, badge, ops);
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

// ---- 文字稿 ----
async function toggleTranscript(id, btn) {
  const item = btn.closest(".item");
  const existing = item.querySelector(".transcript");
  if (existing) {
    existing.remove();
    btn.textContent = "文字稿";
    return;
  }
  btn.textContent = "加载中…";
  try {
    const res = await api(`/api/recordings/${id}/transcript`);
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      btn.textContent = "文字稿";
      alert("文字稿尚未生成（状态：" + (err.status || "unknown") + "）");
      return;
    }
    const data = await res.json();
    item.appendChild(renderTranscript(data));
    btn.textContent = "收起";
  } catch (e) {
    btn.textContent = "文字稿";
  }
}

function renderTranscript(data) {
  const box = el("div", "transcript");
  if (data.language) box.appendChild(el("div", "sub", "语言：" + data.language));
  if (data.segments && data.segments.length) {
    for (const seg of data.segments) {
      const line = el("span", "seg");
      const spk = seg.speaker ? `<span class="spk">[${escapeHtml(seg.speaker)}]</span>` : "";
      line.innerHTML = `<span class="ts">${fmtSec(seg.start)}</span>${spk}${escapeHtml(seg.text || "")}`;
      line.onclick = () => {
        const audio = $("#audio");
        if (audio) audio.currentTime = seg.start;
      };
      box.appendChild(line);
    }
  } else if (data.text) {
    box.appendChild(el("div", null, escapeHtml(data.text)));
  } else {
    box.appendChild(el("div", "sub", "（无文字内容）"));
  }
  return box;
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

  // 若启用 ASR，把开关与参数写回服务端
  const asr = {
    enabled: $("#cfgAsrEnabled").checked,
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
