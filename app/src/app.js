'use strict';

/* ===================== 连接层 ===================== */

const PORT = (window.gamefps && window.gamefps.wsPort) || 8731;

let ws = null;
let reconnectTimer = null;
let latest = null;
let settings = null;

/** 用户是否正在设置页的某个输入控件上操作（热更新时用来抑制回填）。 */
function isEditingForm() {
  const el = document.activeElement;
  if (!el || !el.id || !el.id.startsWith('s-')) return false;
  const tag = el.tagName;
  return tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA';
}

function connect() {
  try {
    ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
  } catch {
    scheduleReconnect();
    return;
  }

  ws.onopen = () => {
    setConn(true);
    send({ type: 'hello' });
  };
  ws.onmessage = (ev) => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { return; }
    dispatch(msg);
  };
  ws.onclose = () => {
    setConn(false);
    scheduleReconnect();
  };
  ws.onerror = () => { try { ws.close(); } catch { /* 已断开 */ } };
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, 1200);
}

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

function setConn(ok) {
  document.getElementById('conn-dot').className = 'conn-dot ' + (ok ? 'on' : 'off');
  document.getElementById('conn-text').textContent = ok ? '已连接采集后端' : '未连接';
}

/* ===================== 消息分发 ===================== */

function dispatch(msg) {
  switch (msg.type) {
    case 'snapshot':
      latest = msg.data;
      render(latest);
      break;
    case 'settings':
      settings = msg.data;
      // 热更新模式下后端会把自己的改动广播回来；此时若用户正在某个输入框里打字，
      // 回填会打断输入（光标跳走、滑块回弹），所以有焦点时不回填。
      if (!isEditingForm()) fillSettings(settings);
      break;
    case 'sessions':
      renderHistory(Array.isArray(msg.data) ? msg.data : []);
      break;
    case 'hotkeyFailed': {
      const d = msg.data || {};
      const hint = $('save-hint');
      if (hint) {
        hint.textContent = `热键 ${d.hotkey} ${d.reason}`;
        hint.classList.add('warn-text');
        setTimeout(() => { hint.textContent = ''; hint.classList.remove('warn-text'); }, 4000);
      }
      break;
    }
    case 'candidates':
      renderCandidates(Array.isArray(msg.data) ? msg.data : []);
      break;
    case 'sessionFrames':
      drawDetailChart(msg.data);
      break;
    case 'sessionCompleted':
      // 一场游戏刚结束并已落盘，刷新历史列表
      send({ type: 'getSessions' });
      break;
    case 'focus':
      if (window.gamefps) window.gamefps.focusWindow();
      break;
  }
}

/* ===================== 渲染 ===================== */

const $ = (id) => document.getElementById(id);
const setText = (id, v) => { const el = $(id); if (el) el.textContent = v; };

function setBar(name, pct, label) {
  const val = Math.max(0, Math.min(100, Number(pct) || 0));
  const bar = $(name + '-bar');
  if (bar) bar.style.width = val.toFixed(1) + '%';
  if (label !== undefined) setText(name + '-val', label);
}

function render(s) {
  setText('game-name', s.gameName || '等待游戏运行…');
  setText('game-pid', s.gamePid ? 'PID ' + s.gamePid : '');

  setText('fps', s.fps > 0.5 ? s.fps.toFixed(0) : '--');
  setText('frame-time', s.avgFrameTimeMs > 0 ? s.avgFrameTimeMs.toFixed(2) : '--');
  setText('low1', s.low1PercentFps > 0.5 ? s.low1PercentFps.toFixed(0) : '--');
  setText('low01', s.low01PercentFps > 0.5 ? s.low01PercentFps.toFixed(0) : '--');
  setText('max-ft', s.maxFrameTimeMs > 0 ? s.maxFrameTimeMs.toFixed(1) + ' ms' : '--');
  setText('total-frames', s.totalFrames > 0 ? Number(s.totalFrames).toLocaleString('zh-CN') : '--');

  setBar('cpu', s.cpuUsagePercent, s.cpuUsagePercent > 0 ? s.cpuUsagePercent.toFixed(1) + '%' : '--%');
  setBar('gpu', s.gpuUsagePercent, s.gpuUsagePercent > 0 ? s.gpuUsagePercent.toFixed(1) + '%' : '--%');

  const vramPct = s.gpuMemoryTotalMB > 0 ? (s.gpuMemoryMB / s.gpuMemoryTotalMB) * 100 : 0;
  setBar('vram', vramPct,
    s.gpuMemoryMB > 0
      ? `${s.gpuMemoryMB.toFixed(0)} / ${s.gpuMemoryTotalMB > 0 ? s.gpuMemoryTotalMB.toFixed(0) : '?'} MB`
      : '-- MB');

  const memLabel = s.memoryTotalMB > 0
    ? `${s.memoryUsagePercent.toFixed(1)}% (${(s.memoryUsedMB / 1024).toFixed(1)}/${(s.memoryTotalMB / 1024).toFixed(1)} GB)`
    : '--%';
  setBar('mem', s.memoryUsagePercent, memLabel);

  // ---- CPU 副行：频率 / 功耗 / 温度 / 大小核 ----
  const cpuBits = [];
  if (s.cpuFrequencyMHz > 0) cpuBits.push((s.cpuFrequencyMHz / 1000).toFixed(2) + ' GHz');
  if (s.hasCpuPower && s.cpuPowerW > 0) cpuBits.push(s.cpuPowerW.toFixed(1) + ' W');
  if (s.hasCpuTemperature && s.cpuTemperatureC > 0) cpuBits.push(s.cpuTemperatureC.toFixed(0) + '°C');
  if (s.hasHeterogeneousCores) {
    const p = s.pCoreFrequencyMHz > 0
      ? `大核 ${s.pCoreUsagePercent.toFixed(0)}% ${(s.pCoreFrequencyMHz / 1000).toFixed(2)}G` : '';
    const e = s.eCoreFrequencyMHz > 0
      ? `小核 ${s.eCoreUsagePercent.toFixed(0)}% ${(s.eCoreFrequencyMHz / 1000).toFixed(2)}G` : '';
    const split = [p, e].filter(Boolean).join(' · ');
    if (split) cpuBits.push(split);
  }
  setText('cpu-sub', cpuBits.length ? cpuBits.join(' · ') : '--');

  // ---- GPU 副行：频率 / 功耗 / 温度 ----
  const gpuBits = [];
  if (s.gpuFrequencyMHz > 0) gpuBits.push(s.gpuFrequencyMHz.toFixed(0) + ' MHz');
  if (s.gpuPowerW > 0) gpuBits.push(s.gpuPowerW.toFixed(1) + ' W');
  if (s.gpuTemperatureC > 0) gpuBits.push(s.gpuTemperatureC.toFixed(0) + '°C');
  setText('gpu-sub', gpuBits.length ? gpuBits.join(' · ') : '--');

  const errBox = $('error-box');
  if (s.error && !errBox.dataset.shown) {
    errBox.textContent = s.error;
    errBox.classList.remove('hidden');
    errBox.dataset.shown = '1';
  } else if (!s.error) {
    errBox.classList.add('hidden');
    delete errBox.dataset.shown;
  }

  drawChart(s.recentFrameTimes || [], s.low1PercentHistory || [], s.low01PercentHistory || []);
  drawFrameTimeChart(s.recentFrameTimes || []);

  const sess = $('session-info');
  if (sess) {
    sess.textContent = s.sessionFrames > 0
      ? `· 本场已记录 ${Number(s.sessionFrames).toLocaleString('zh-CN')} 帧（自 ${s.sessionStartedAt}）`
      : '';
  }

  const bm = $('badge-manual');
  if (bm) {
    if (s.manualTarget) {
      bm.textContent = `锁定：${s.manualTargetName || '（未选）'}`;
      bm.classList.remove('hidden');
    } else {
      bm.classList.add('hidden');
    }
  }
}

/* ===================== 监测目标（手动选进程） ===================== */

let candidates = [];

function selectedTargetPid() {
  const sel = $('monitor-target-select');
  return sel ? (Number(sel.value) || 0) : 0;
}

/**
 * 重建监控页顶部的目标下拉框：
 * 第一项是"自动检测"，其余是当前正在渲染的进程。
 */
function renderCandidates(list) {
  candidates = list;
  const sel = $('monitor-target-select');
  if (!sel) return;

  const prev = sel.value;
  const lockedName = (settings && settings.manualTargetEnabled) ? settings.manualTargetName : '';

  const opts = ['<option value="">自动检测（跟随前台渲染进程）</option>'];
  for (const c of list) {
    const mark = c.isForeground ? ' · 前台' : '';
    opts.push(`<option value="${c.pid}">${esc(c.name)} · PID ${c.pid} · ${num(c.fps)} FPS${mark}</option>`);
  }

  // 已锁定但当前没在渲染：补一个占位项，让用户知道锁的是什么
  if (lockedName && !list.some((c) => c.name === lockedName)) {
    opts.push(`<option value="" disabled>${esc(lockedName)}（当前未渲染，等待启动）</option>`);
  }

  sel.innerHTML = opts.join('');

  if (prev && list.some((c) => String(c.pid) === prev)) {
    sel.value = prev;
  } else if (lockedName) {
    const m = list.find((c) => c.name === lockedName);
    sel.value = m ? String(m.pid) : '';
  } else {
    sel.value = '';
  }
}

/* ===================== 历史记录 ===================== */

let history = [];

/** 取数组的 p 分位（p=0.99 表示去掉最大的 1%）。 */
function percentile(arr, p) {
  if (!arr || !arr.length) return 0;
  const s = [...arr].sort((a, b) => a - b);
  const idx = Math.min(s.length - 1, Math.max(0, Math.floor(s.length * p)));
  return s[idx];
}

/**
 * 取一个"好看"的纵轴上界，让刻度是整齐的整数（步长 = 上界/4）。
 * 直接用 max 会被个别异常尖峰撑爆，正常数据全挤在底部看不出来。
 * table 可覆盖，因为帧率(FPS, 几百)和帧时间(ms, 几~几十)的量级差很多。
 */
const NICE_FPS = [60, 120, 160, 200, 240, 320, 400, 480, 600, 800, 1000, 1200, 1600, 2000, 3000];
const NICE_MS = [8, 10, 12, 16, 20, 24, 33, 40, 50, 66, 80, 100, 133, 160, 200, 250];

function niceCeil(v, minimum = 60, table = NICE_FPS) {
  const target = Math.max(v, minimum);
  for (const s of table) if (target <= s) return s;
  return Math.ceil(target / 1000) * 1000;
}

const esc = (s) => String(s ?? '').replace(/[&<>"]/g,
  (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

const num = (v, d = 0) => (typeof v === 'number' && isFinite(v)) ? v.toFixed(d) : '--';
const fmtInt = (v) => (typeof v === 'number' && isFinite(v)) ? Math.round(v).toLocaleString('zh-CN') : '--';

function fmtDuration(sec) {
  if (typeof sec !== 'number' || !isFinite(sec)) return '--';
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = Math.round(sec % 60);
  if (h > 0) return `${h}h${m}m`;
  if (m > 0) return `${m}m${s}s`;
  return `${s}s`;
}

function renderHistory(list) {
  history = list;
  const count = $('history-count');
  const listEl = $('history-list');
  const chartWrap = $('history-chart-wrap');
  if (!listEl) return;

  if (count) count.textContent = list.length ? `共 ${list.length} 条` : '';

  if (!list.length) {
    if (chartWrap) chartWrap.classList.add('hidden');
    listEl.innerHTML = '<div class="muted small">暂无记录 —— 玩一局游戏，退出后会自动出现在这里。</div>';
    return;
  }

  if (chartWrap) chartWrap.classList.remove('hidden');
  drawHistoryChart(list);

  listEl.innerHTML = list.map((r, i) => `
    <div class="hist-card" data-idx="${i}" title="点击查看该场详情与逐帧曲线">
      <button class="hist-del" data-idx="${i}" title="删除这条记录">✕</button>
      <div class="hist-head">
        <span class="hist-game">${esc(r.gameName || '未知')}</span>
        <span class="hist-time mono">${esc(r.endedAt || '')}</span>
      </div>
      <div class="hist-meta">${fmtDuration(r.durationSeconds)} · ${fmtInt(r.frameCount)} 帧${r.csvFile ? '' : ' · 无逐帧数据'}</div>
      <div class="hist-stats">
        <div class="hs"><span>平均</span><b class="c-avg">${num(r.averageFps)}</b></div>
        <div class="hs"><span>1% Low</span><b class="c-low1">${num(r.low1PercentFps)}</b></div>
        <div class="hs"><span>0.1% Low</span><b class="c-low01">${num(r.low01PercentFps)}</b></div>
        <div class="hs"><span>卡顿</span><b>${r.stutterCount ?? 0}</b></div>
      </div>
    </div>`).join('');

  listEl.querySelectorAll('.hist-card').forEach((el) => {
    el.addEventListener('click', () => {
      listEl.querySelectorAll('.hist-card').forEach((x) => x.classList.remove('active'));
      el.classList.add('active');
      showDetail(history[Number(el.dataset.idx)]);
    });
  });

  // 单条删除：别让点击冒泡到卡片（否则会顺带打开详情）
  listEl.querySelectorAll('.hist-del').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      const r = history[Number(btn.dataset.idx)];
      if (!r) return;
      const label = `${r.gameName || '未知'} · ${r.endedAt || ''}`;
      if (!confirm(`确定删除这条记录吗？\n\n${label}\n\n对应的逐帧 CSV 也会一起删除。`)) return;
      send({
        type: 'deleteSession',
        data: {
          startedAt: r.startedAt,
          endedAt: r.endedAt,
          gameName: r.gameName,
          csvFile: r.csvFile,
        },
      });
    });
  });
}

function showDetail(r) {
  if (!r) return;

  $('detail-title').textContent = `${r.gameName || '未知'} · ${r.endedAt || ''}`;

  const rows = [
    ['开始时间', r.startedAt],
    ['结束时间', r.endedAt],
    ['时长', fmtDuration(r.durationSeconds)],
    ['平均 FPS', num(r.averageFps)],
    ['中位 FPS', num(r.medianFps)],
    ['最高 / 最低 FPS', `${num(r.maxFps)} / ${num(r.minFps)}`],
    ['1% Low', num(r.low1PercentFps)],
    ['0.1% Low', num(r.low01PercentFps)],
    ['平均帧时间', `${num(r.avgFrameTimeMs, 2)} ms`],
    ['最慢帧', `${num(r.maxFrameTimeMs, 2)} ms`],
    ['卡顿帧数', String(r.stutterCount ?? 0)],
    ['总帧数', fmtInt(r.frameCount)],
    ['参与统计帧数', fmtInt(r.sampleCount)],
    ['逐帧 CSV', r.csvFile || '（未生成）'],
    ['游戏路径', r.gamePath || ''],
  ];

  $('detail-body').innerHTML = rows.map(([k, v]) =>
    `<div class="detail-item"><span>${esc(k)}</span><b>${esc(v === undefined || v === null ? '' : v)}</b></div>`
  ).join('');

  $('history-detail').classList.remove('hidden');

  // 逐帧曲线：向后端要这一场的 CSV 数据
  const wrap = $('detail-chart-wrap');
  const note = $('detail-chart-note');
  if (r.csvFile) {
    wrap.classList.remove('hidden');
    note.textContent = '正在加载逐帧数据…';
    send({ type: 'getSessionFrames', csvFile: r.csvFile });
  } else {
    wrap.classList.add('hidden');
  }

  $('history-detail').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

/* ===================== 历史对比曲线 ===================== */

function drawHistoryChart(list) {
  const canvas = $('history-chart');
  if (!canvas) return;

  // 最近 20 场，按时间正序（左旧右新）
  const recent = list.slice(0, 20).reverse();

  const dpr = window.devicePixelRatio || 1;
  const cssW = canvas.clientWidth || 900;
  const cssH = canvas.clientHeight || 210;
  if (canvas.width !== Math.round(cssW * dpr) || canvas.height !== Math.round(cssH * dpr)) {
    canvas.width = Math.round(cssW * dpr);
    canvas.height = Math.round(cssH * dpr);
  }

  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, cssW, cssH);

  const note = $('history-chart-note');
  if (note) note.textContent = `最近 ${recent.length} 场`;

  let peak = 0;
  for (const r of recent) peak = Math.max(peak, r.averageFps || 0);
  const yMax = niceCeil(peak, 60);

  const padL = 42, padR = 14, padT = 12, padB = 24;
  const w = Math.max(10, cssW - padL - padR);
  const h = Math.max(10, cssH - padT - padB);

  ctx.strokeStyle = chartPalette.grid;
  ctx.lineWidth = 1;
  ctx.font = '11px Consolas, monospace';
  ctx.fillStyle = chartPalette.tick;

  const steps = 4;
  for (let i = 0; i <= steps; i++) {
    const y = padT + (h * i) / steps;
    ctx.beginPath();
    ctx.moveTo(padL, y);
    ctx.lineTo(padL + w, y);
    ctx.stroke();
    ctx.fillText(String(Math.round((yMax * (steps - i)) / steps)), 6, y + 4);
  }

  const n = recent.length;
  const xAt = (i) => padL + (n === 1 ? w / 2 : (w * i) / (n - 1));
  const yAt = (v) => padT + h - (h * Math.min(Math.max(v, 0), yMax)) / yMax;

  const series = [
    { key: 'averageFps', color: '#6ef3c0', width: 2.2 },
    { key: 'low1PercentFps', color: '#ffb454', width: 1.8 },
    { key: 'low01PercentFps', color: '#b48cff', width: 1.6 },
  ];

  for (const s of series) {
    ctx.beginPath();
    for (let i = 0; i < n; i++) {
      const x = xAt(i), y = yAt(recent[i][s.key] || 0);
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.strokeStyle = s.color;
    ctx.lineWidth = s.width;
    ctx.lineJoin = 'round';
    ctx.stroke();

    for (let i = 0; i < n; i++) {
      ctx.beginPath();
      ctx.arc(xAt(i), yAt(recent[i][s.key] || 0), 3, 0, Math.PI * 2);
      ctx.fillStyle = s.color;
      ctx.fill();
    }
  }

  ctx.fillStyle = chartPalette.tick;
  if (n > 0) {
    ctx.textAlign = 'left';
    ctx.fillText((recent[0].endedAt || '').slice(5, 16), padL, padT + h + 17);
    if (n > 1) {
      ctx.textAlign = 'right';
      ctx.fillText((recent[n - 1].endedAt || '').slice(5, 16), padL + w, padT + h + 17);
    }
  }
  ctx.textAlign = 'left';
}

/* ===================== 单场逐帧曲线 ===================== */

function drawDetailChart(data) {
  const canvas = $('detail-chart');
  const note = $('detail-chart-note');
  if (!canvas) return;

  const frames = (data && data.frameTimes) || [];
  if (frames.length < 2) {
    if (note) note.textContent = '这一场没有可用的逐帧数据（可能当时未开启 CSV 记录）。';
    canvas.getContext('2d').clearRect(0, 0, canvas.width, canvas.height);
    return;
  }

  const fps = frames.map((ms) => (ms > 0 ? 1000 / ms : 0));
  const peak = Math.max(...fps);
  const low = Math.min(...fps);

  // 纵轴用 P99 定位而不是最大值：ETW 偶尔的重复上报会冒出一个几千 FPS 的尖峰，
  // 若按最大值定刻度，真实数据就被压成贴着底部的一条线了。
  const yMax = niceCeil(percentile(fps, 0.99), 60);

  const dpr = window.devicePixelRatio || 1;
  const cssW = canvas.clientWidth || 900;
  const cssH = canvas.clientHeight || 200;
  if (canvas.width !== Math.round(cssW * dpr) || canvas.height !== Math.round(cssH * dpr)) {
    canvas.width = Math.round(cssW * dpr);
    canvas.height = Math.round(cssH * dpr);
  }

  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, cssW, cssH);

  const padL = 42, padR = 14, padT = 12, padB = 20;
  const w = Math.max(10, cssW - padL - padR);
  const h = Math.max(10, cssH - padT - padB);

  ctx.strokeStyle = chartPalette.grid;
  ctx.lineWidth = 1;
  ctx.font = '11px Consolas, monospace';
  ctx.fillStyle = chartPalette.tick;
  const steps = 4;
  for (let i = 0; i <= steps; i++) {
    const y = padT + (h * i) / steps;
    ctx.beginPath();
    ctx.moveTo(padL, y);
    ctx.lineTo(padL + w, y);
    ctx.stroke();
    ctx.fillText(String(Math.round((yMax * (steps - i)) / steps)), 6, y + 4);
  }

  const n = fps.length;
  const xAt = (i) => padL + (w * i) / (n - 1);
  const yAt = (v) => padT + h - (h * Math.min(Math.max(v, 0), yMax)) / yMax;

  const grad = ctx.createLinearGradient(0, padT, 0, padT + h);
  grad.addColorStop(0, 'rgba(110,243,192,.30)');
  grad.addColorStop(1, 'rgba(110,243,192,0)');

  ctx.beginPath();
  ctx.moveTo(xAt(0), padT + h);
  for (let i = 0; i < n; i++) ctx.lineTo(xAt(i), yAt(fps[i]));
  ctx.lineTo(xAt(n - 1), padT + h);
  ctx.closePath();
  ctx.fillStyle = grad;
  ctx.fill();

  ctx.beginPath();
  for (let i = 0; i < n; i++) {
    const x = xAt(i), y = yAt(fps[i]);
    if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
  }
  ctx.strokeStyle = '#6ef3c0';
  ctx.lineWidth = 1.4;
  ctx.lineJoin = 'round';
  ctx.stroke();

  const avg = fps.reduce((a, b) => a + b, 0) / n;
  const above = fps.filter((v) => v > yMax).length;
  if (note) {
    let txt = `${n} 帧 · 均值 ${avg.toFixed(0)} · 最低 ${low.toFixed(0)} FPS`;
    if (above > 0) txt += ` · ${above} 个尖峰超出纵轴（已裁剪）`;
    note.textContent = txt;
  }
}

/* ===================== 帧率曲线 ===================== */

function drawChart(frameTimes, low1History, low01History) {
  const canvas = $('chart');
  if (!canvas) return;

  const dpr = window.devicePixelRatio || 1;
  const cssW = canvas.clientWidth || 800;
  const cssH = canvas.clientHeight || 200;
  if (canvas.width !== Math.round(cssW * dpr) || canvas.height !== Math.round(cssH * dpr)) {
    canvas.width = Math.round(cssW * dpr);
    canvas.height = Math.round(cssH * dpr);
  }

  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, cssW, cssH);

  const info = $('chart-info');
  if (!frameTimes || frameTimes.length < 2) {
    info.textContent = '等待数据…';
    return;
  }

  const fps = frameTimes.map((ft) => (ft > 0 ? Math.min(1000 / ft, 2000) : 0));

  // 纵轴要同时容纳逐帧曲线和两条 Low 曲线，取三者 P99 的最大值
  const lows = [...(low1History || []), ...(low01History || [])].filter((v) => v > 0);
  const yMax = niceCeil(Math.max(percentile(fps, 0.99), percentile(lows, 0.99)), 60);

  const padL = 36, padR = 10, padT = 10, padB = 14;
  const w = Math.max(10, cssW - padL - padR);
  const h = Math.max(10, cssH - padT - padB);

  // 网格与刻度
  ctx.strokeStyle = chartPalette.grid;
  ctx.lineWidth = 1;
  ctx.font = '11px Consolas, monospace';
  ctx.fillStyle = chartPalette.tick;
  const steps = 4;
  for (let i = 0; i <= steps; i++) {
    const y = padT + (h * i) / steps;
    ctx.beginPath();
    ctx.moveTo(padL, y);
    ctx.lineTo(padL + w, y);
    ctx.stroke();
    ctx.fillText(String(Math.round((yMax * (steps - i)) / steps)), 4, y + 4);
  }

  const n = fps.length;
  const xAt = (i) => padL + (w * i) / (n - 1);
  const yAt = (v) => padT + h - (h * Math.min(Math.max(v, 0), yMax)) / yMax;

  // 面积
  const grad = ctx.createLinearGradient(0, padT, 0, padT + h);
  grad.addColorStop(0, 'rgba(110,243,192,.32)');
  grad.addColorStop(1, 'rgba(110,243,192,0)');
  ctx.beginPath();
  ctx.moveTo(xAt(0), padT + h);
  for (let i = 0; i < n; i++) ctx.lineTo(xAt(i), yAt(fps[i]));
  ctx.lineTo(xAt(n - 1), padT + h);
  ctx.closePath();
  ctx.fillStyle = grad;
  ctx.fill();

  // 折线
  ctx.beginPath();
  for (let i = 0; i < n; i++) {
    const x = xAt(i), y = yAt(fps[i]);
    if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
  }
  ctx.strokeStyle = '#6ef3c0';
  ctx.lineWidth = 1.6;
  ctx.lineJoin = 'round';
  ctx.stroke();

  // ---- 1% / 0.1% Low 曲线（每秒一个点，铺满整个宽度）----
  const drawLow = (series, color) => {
    if (!series || series.length < 2) return;
    const m = series.length;
    ctx.beginPath();
    for (let i = 0; i < m; i++) {
      const x = padL + (w * i) / (m - 1);
      const y = yAt(series[i]);
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.strokeStyle = color;
    ctx.lineWidth = 1.8;
    ctx.lineJoin = 'round';
    ctx.stroke();
  };
  drawLow(low1History, '#ffb454');
  drawLow(low01History, '#b48cff');

  const avg = fps.reduce((a, b) => a + b, 0) / n;
  let txt = `最近 ${n} 帧 · 均值 ${avg.toFixed(0)} · 峰值 ${Math.max(...fps).toFixed(0)} · 最低 ${Math.min(...fps).toFixed(0)} FPS`;
  if (low1History && low1History.length > 1) txt += ` · Low 已采样 ${low1History.length}s`;
  info.textContent = txt;
}

/* ===================== 帧时间曲线 ===================== */

function drawFrameTimeChart(frameTimes) {
  const canvas = $('ft-chart');
  if (!canvas) return;

  const dpr = window.devicePixelRatio || 1;
  const cssW = canvas.clientWidth || 800;
  const cssH = canvas.clientHeight || 150;
  if (canvas.width !== Math.round(cssW * dpr) || canvas.height !== Math.round(cssH * dpr)) {
    canvas.width = Math.round(cssW * dpr);
    canvas.height = Math.round(cssH * dpr);
  }

  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, cssW, cssH);

  const info = $('ft-chart-info');
  if (!frameTimes || frameTimes.length < 2) {
    if (info) info.textContent = '等待数据…';
    return;
  }

  // 帧时间单位是 ms，越小越好；同样用 P99 定上限，避免异常尖峰把曲线压扁
  const ft = frameTimes.map((v) => (v > 0 ? Math.min(v, 2000) : 0));
  const yMax = niceCeil(percentile(ft, 0.99), 8, NICE_MS);

  const padL = 44, padR = 10, padT = 10, padB = 14;
  const w = Math.max(10, cssW - padL - padR);
  const h = Math.max(10, cssH - padT - padB);

  ctx.strokeStyle = chartPalette.grid;
  ctx.lineWidth = 1;
  ctx.font = '11px Consolas, monospace';
  ctx.fillStyle = chartPalette.tick;
  const steps = 4;
  for (let i = 0; i <= steps; i++) {
    const y = padT + (h * i) / steps;
    ctx.beginPath();
    ctx.moveTo(padL, y);
    ctx.lineTo(padL + w, y);
    ctx.stroke();
    const label = (yMax * (steps - i)) / steps;
    ctx.fillText(label >= 10 ? label.toFixed(0) : label.toFixed(1), 4, y + 4);
  }

  const n = ft.length;
  const xAt = (i) => padL + (w * i) / (n - 1);
  const yAt = (v) => padT + h - (h * Math.min(Math.max(v, 0), yMax)) / yMax;

  const grad = ctx.createLinearGradient(0, padT, 0, padT + h);
  grad.addColorStop(0, 'rgba(100,210,255,.28)');
  grad.addColorStop(1, 'rgba(100,210,255,0)');
  ctx.beginPath();
  ctx.moveTo(xAt(0), padT + h);
  for (let i = 0; i < n; i++) ctx.lineTo(xAt(i), yAt(ft[i]));
  ctx.lineTo(xAt(n - 1), padT + h);
  ctx.closePath();
  ctx.fillStyle = grad;
  ctx.fill();

  ctx.beginPath();
  for (let i = 0; i < n; i++) {
    const x = xAt(i), y = yAt(ft[i]);
    if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
  }
  ctx.strokeStyle = '#64d2ff';
  ctx.lineWidth = 1.4;
  ctx.lineJoin = 'round';
  ctx.stroke();

  // 平均线（虚线），方便一眼看出哪些帧高过平均
  const ftAvg = ft.reduce((a, b) => a + b, 0) / n;
  if (ftAvg > 0 && ftAvg <= yMax) {
    const ya = yAt(ftAvg);
    ctx.save();
    ctx.setLineDash([5, 4]);
    ctx.strokeStyle = chartPalette.avgLine;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(padL, ya);
    ctx.lineTo(padL + w, ya);
    ctx.stroke();
    ctx.restore();
  }

  if (info) {
    info.textContent =
      `平均 ${ftAvg.toFixed(2)} ms · 最慢 ${Math.max(...ft).toFixed(1)} ms · 最快 ${Math.min(...ft).toFixed(2)} ms`;
  }
}

/* ===================== 设置页 ===================== */

const FIELDS = [
  ['overlayEnabled', 'checkbox'],
  ['overlayStyle', 'number'],
  ['autoShowOnGame', 'checkbox'],
  ['showGameName', 'checkbox'],
  ['showFps', 'checkbox'],
  ['showFrameTime', 'checkbox'],
  ['showLow1', 'checkbox'],
  ['showLow01', 'checkbox'],
  ['showCpu', 'checkbox'],
  ['showGpu', 'checkbox'],
  ['showGpuMemory', 'checkbox'],
  ['showMemory', 'checkbox'],
  ['showCpuFrequency', 'checkbox'],
  ['showGpuFrequency', 'checkbox'],
  ['showCpuPower', 'checkbox'],
  ['showGpuPower', 'checkbox'],
  ['showCpuTemperature', 'checkbox'],
  ['showGpuTemperature', 'checkbox'],
  ['showVramTemperature', 'checkbox'],
  ['showPCore', 'checkbox'],
  ['showECore', 'checkbox'],
  ['showOverlayChart', 'checkbox'],
  ['showOverlayLow1', 'checkbox'],
  ['showOverlayLow01', 'checkbox'],
  ['showOverlayFrameTime', 'checkbox'],
  ['overlayChartHeight', 'range'],
  ['x', 'number'],
  ['y', 'number'],
  ['scale', 'range'],
  ['backOpacity', 'range'],
  ['cornerRadius', 'range'],
  ['fontFamily', 'text'],
  ['textColor', 'color'],
  ['labelColor', 'color'],
  ['backColor', 'color'],
  ['hotkeyToggleOverlay', 'text'],
  ['hotkeyOpenMain', 'text'],
  ['recordSessions', 'checkbox'],
  ['writeFrameCsv', 'checkbox'],
  ['sessionIdleSeconds', 'number'],
  ['maxSessionsInHistory', 'number'],
  ['priorityClass', 'text'],
  ['uiTheme', 'text'],
];

/* ===================== 悬浮窗配色主题 ===================== */

const THEMES = {
  dark:  { backColor: '#0B0F14', textColor: '#6EF3C0', labelColor: '#D8E6F2' },
  light: { backColor: '#FFFFFF', textColor: '#0A6B4A', labelColor: '#3A4A5A' },
};

/** 根据当前颜色反推是深色、浅色还是自定义。 */
function detectTheme(s) {
  const back = String((s && s.backColor) || '').toUpperCase();
  if (back === '#FFFFFF' || back === '#FFF') return 'light';
  if (back === '#0B0F14') return 'dark';
  return 'custom';
}

function applyTheme(theme, pushSettings) {
  const preset = THEMES[theme];
  if (!preset) return;              // custom 不改任何颜色
  const t = $('s-textColor'), l = $('s-labelColor'), b = $('s-backColor');
  if (t) t.value = preset.textColor;
  if (l) l.value = preset.labelColor;
  if (b) b.value = preset.backColor;

  // 白底若太透明，会透出桌面颜色发灰、深色文字看不清，顺手把不透明度提到够用的水平
  if (theme === 'light') {
    const op = $('s-backOpacity');
    if (op && Number(op.value) < 215) {
      op.value = 225;
      syncRangeLabels();
    }
  }
  pushSettings(true);
}

function fillSettings(s) {
  for (const [key, kind] of FIELDS) {
    const el = $('s-' + key);
    if (!el) continue;
    const v = s[key];
    if (kind === 'checkbox') el.checked = !!v;
    else el.value = v === undefined || v === null ? '' : v;
  }

  // 恢复自定义显示顺序（后端缺失或为空时用默认顺序）
  const saved = Array.isArray(s.overlayOrder) ? s.overlayOrder.filter((k) => ORDER_ITEMS.some((i) => i.key === k)) : [];
  const merged = saved.concat(ORDER_ITEMS.map((i) => i.key).filter((k) => !saved.includes(k)));
  orderList = merged;

  // 恢复网格布局
  const blocks = Array.isArray(s.overlayBlocks) ? s.overlayBlocks.filter((b) => b && BLOCK_TYPES.some((t) => t.key === b.type)) : [];
  gridState = {
    blocks: blocks.map((b) => ({ type: b.type, row: Number(b.row) || 0, col: Number(b.col) || 0 })),
    rows: Math.max(1, Number(s.gridRows) || 3),
    cols: Math.max(1, Number(s.gridCols) || 1),
  };

  // 主题选择器反映当前配色
  const themeSel = $('s-colorTheme');
  if (themeSel) themeSel.value = detectTheme(s);

  // 主界面主题
  applyUiTheme(s.uiTheme);
  const uiThemeSel = $('s-uiTheme');
  if (uiThemeSel) uiThemeSel.value = s.uiTheme === 'light' ? 'light' : 'dark';

  // 手动目标的进程名不在 FIELDS 里，由监控页顶部下拉框承载
  syncRangeLabels();
}

function collectSettings() {
  // 以「后端当前的设置」为基底，只覆盖界面上真实存在的控件。
  // 否则 UI 里没有对应控件的字段（行间距、启动行为、自定义顺序等）
  // 会被反序列化成 C# 默认值，表现出来就是“重启后设置被重置”。
  const out = Object.assign({}, settings || {});

  for (const [key, kind] of FIELDS) {
    const el = $('s-' + key);
    if (!el) continue;
    if (kind === 'checkbox') out[key] = el.checked;
    else if (kind === 'number' || kind === 'range') out[key] = Number(el.value) || 0;
    else out[key] = el.value;
  }

  // 手动目标不归这个表单管，但要原样带回去
  if (settings) {
    out.manualTargetEnabled = settings.manualTargetEnabled;
    out.manualTargetName = settings.manualTargetName || '';
  }

  // 自定义显示顺序
  out.overlayOrder = orderList.slice();

  // 自由网格布局
  out.overlayBlocks = gridState.blocks.map((b) => ({ type: b.type, row: b.row, col: b.col }));
  out.gridRows = gridState.rows;
  out.gridCols = gridState.cols;

  return out;
}

/**
 * 把 e.code 转成后端能解析的键名。
 * 用 code 而不是 key：key 会受键盘布局和输入法影响（比如中文输入法下 key 可能是 "Process"）。
 */
function normalizeHotkeyCode(code) {
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);          // KeyA -> A
  if (/^Digit[0-9]$/.test(code)) return code.slice(5);        // Digit1 -> 1
  if (/^Numpad[0-9]$/.test(code)) return 'Num' + code.slice(6);
  if (/^F([1-9]|1[0-9]|2[0-4])$/.test(code)) return code;     // F1..F24
  const map = {
    Space: 'Space', Tab: 'Tab', Enter: 'Enter', Backspace: 'Backspace',
    ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
    Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown',
    Insert: 'Insert', Delete: 'Delete',
    Minus: '-', Equal: '=', BracketLeft: '[', BracketRight: ']',
    Semicolon: ';', Quote: "'", Comma: ',', Period: '.', Slash: '/',
    Backslash: '\\', Backquote: '`',
  };
  return map[code] || null;
}

const MODIFIER_CODES = new Set([
  'ControlLeft', 'ControlRight', 'ShiftLeft', 'ShiftRight',
  'AltLeft', 'AltRight', 'MetaLeft', 'MetaRight',
]);

/**
 * 让一个只读输入框变成"按键录制器"：
 * 聚焦后按下任意组合键，自动填成 "Ctrl+Alt+F9" 这种格式并即时保存。
 */
function setupHotkeyRecorder(input, pushSettings) {
  if (!input) return;

  let original = '';

  input.addEventListener('focus', () => {
    original = input.value;
    input.classList.add('recording');
    input.value = '按下组合键…';
  });

  input.addEventListener('blur', () => {
    input.classList.remove('recording');
    // 没录到有效按键就还原（比如只是点了下又点走）
    if (input.value === '按下组合键…') input.value = original;
  });

  input.addEventListener('keydown', (e) => {
    // 全部拦下，避免 F5 刷新、Ctrl+S 保存、Tab 跳焦点之类干扰录制
    e.preventDefault();
    e.stopPropagation();

    if (e.key === 'Escape') {
      input.value = original;
      input.blur();
      return;
    }

    // 只按修饰键时先不提交，等主键
    if (MODIFIER_CODES.has(e.code)) return;

    const main = normalizeHotkeyCode(e.code);
    if (!main) return;                       // 不支持的键，忽略

    const parts = [];
    if (e.ctrlKey) parts.push('Ctrl');
    if (e.altKey) parts.push('Alt');
    if (e.shiftKey) parts.push('Shift');
    if (e.metaKey) parts.push('Win');
    parts.push(main);

    input.value = parts.join('+');
    input.blur();                            // 收起录制态（会触发 blur 里的还原判断）
    pushSettings(true);                      // 立即生效，热更新
  });
}

/* ===================== 显示项自定义顺序 ===================== */

/** 悬浮窗竖排样式能显示的项；key 必须与后端 BuildRowsVertical 里的 Key 一致。 */
const ORDER_ITEMS = [
  { key: 'gamename',  field: 'showGameName',        label: '游戏名' },
  { key: 'fps',       field: 'showFps',             label: 'FPS' },
  { key: 'frametime', field: 'showFrameTime',       label: '帧时间' },
  { key: 'low1',      field: 'showLow1',            label: '1% Low' },
  { key: 'low01',     field: 'showLow01',           label: '0.1% Low' },
  { key: 'cpu',       field: 'showCpu',             label: 'CPU' },
  { key: 'pcore',     field: 'showPCore',           label: '大核' },
  { key: 'ecore',     field: 'showECore',           label: '小核' },
  { key: 'gpu',       field: 'showGpu',             label: 'GPU' },
  { key: 'vram',      field: 'showGpuMemory',       label: '显存' },
  { key: 'memory',    field: 'showMemory',          label: '内存' },
];

let orderList = ORDER_ITEMS.map((i) => i.key);

/** 按 orderList 重排显示项 DOM，并给每项挂上上下移动按钮。 */
function renderOrderList(onChange) {
  const wrap = $('order-list');
  if (!wrap) return;

  // 记住每个 checkbox 的勾选状态
  const checked = {};
  for (const it of ORDER_ITEMS) {
    const el = $('s-' + it.field);
    if (el) checked[it.field] = el.checked;
  }

  wrap.innerHTML = '';
  orderList.forEach((key, idx) => {
    const item = ORDER_ITEMS.find((i) => i.key === key);
    if (!item) return;

    const row = document.createElement('div');
    row.className = 'order-item';
    row.draggable = true;
    row.dataset.key = key;
    row.innerHTML = `
      <span class="ord-handle" title="拖动排序">⠿</span>
      <label><input type="checkbox" id="s-${item.field}"${checked[item.field] ? ' checked' : ''}><span>${item.label}</span></label>
      <span class="ord-btns">
        <button type="button" class="ord-up" title="上移"${idx === 0 ? ' disabled' : ''}>▲</button>
        <button type="button" class="ord-down" title="下移"${idx === orderList.length - 1 ? ' disabled' : ''}>▼</button>
      </span>`;
    wrap.appendChild(row);
  });

  // 事件绑定
  wrap.querySelectorAll('.order-item').forEach((row) => {
    const key = row.dataset.key;
    const idx = orderList.indexOf(key);
    const item = ORDER_ITEMS.find((i) => i.key === key);

    const cb = row.querySelector('input[type=checkbox]');
    if (cb) cb.addEventListener('change', () => onChange(item.field, cb.checked));

    const up = row.querySelector('.ord-up');
    if (up) up.addEventListener('click', () => {
      if (idx <= 0) return;
      [orderList[idx - 1], orderList[idx]] = [orderList[idx], orderList[idx - 1]];
      renderOrderList(onChange);
      onChange(null, null);
    });

    const down = row.querySelector('.ord-down');
    if (down) down.addEventListener('click', () => {
      if (idx >= orderList.length - 1) return;
      [orderList[idx + 1], orderList[idx]] = [orderList[idx], orderList[idx + 1]];
      renderOrderList(onChange);
      onChange(null, null);
    });

    // 拖动排序
    row.addEventListener('dragstart', (e) => {
      e.dataTransfer.setData('text/plain', key);
      e.dataTransfer.effectAllowed = 'move';
      row.classList.add('dragging-row');
    });
    row.addEventListener('dragend', () => row.classList.remove('dragging-row'));
    row.addEventListener('dragover', (e) => { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; });
    row.addEventListener('drop', (e) => {
      e.preventDefault();
      const from = e.dataTransfer.getData('text/plain');
      if (!from || from === key) return;
      const fromIdx = orderList.indexOf(from);
      const toIdx = orderList.indexOf(key);
      if (fromIdx < 0 || toIdx < 0) return;
      orderList.splice(fromIdx, 1);
      orderList.splice(toIdx, 0, from);
      renderOrderList(onChange);
      onChange(null, null);
    });
  });
}

/* ===================== 网格布局编辑器 ===================== */

/** 可拖进网格的数据块类型；key 必须与后端 BlockContent 的 case 一致。 */
const BLOCK_TYPES = [
  { key: 'gamename',  label: '游戏名' },
  { key: 'fps',       label: 'FPS' },
  { key: 'frametime', label: '帧时间' },
  { key: 'low1',      label: '1% Low' },
  { key: 'low01',     label: '0.1% Low' },
  { key: 'cpu',       label: 'CPU' },
  { key: 'gpu',       label: 'GPU' },
  { key: 'pcore',     label: '大核' },
  { key: 'ecore',     label: '小核' },
  { key: 'vram',      label: '显存' },
  { key: 'memory',    label: '内存' },
  { key: 'curve',     label: '帧率曲线' },
  { key: 'ftcurve',   label: '帧时间曲线' },
];

const DEFAULT_GRID_BLOCKS = [
  { type: 'gamename',  row: 0, col: 0 },
  { type: 'fps',       row: 1, col: 0 },
  { type: 'frametime', row: 2, col: 0 },
  { type: 'cpu',       row: 3, col: 0 },
  { type: 'gpu',       row: 4, col: 0 },
  { type: 'curve',     row: 5, col: 0 },
];

let gridState = { blocks: [], rows: 3, cols: 1 };

/** 找某个格子上已放置的块。 */
function blockAt(row, col) {
  return gridState.blocks.find((b) => b.row === row && b.col === col) || null;
}

/**
 * 放置一个块。放好后保证它的右侧和下方各有一个空槽（没有就扩展网格）。
 * 同一类型只能占一个格子——拖到新位置等于从旧位置挪过来，
 * 这样池子里的「已使用」标记始终与网格一致。
 */
function placeBlock(type, row, col) {
  gridState.blocks = gridState.blocks.filter((b) => b.type !== type && !(b.row === row && b.col === col));
  gridState.blocks.push({ type, row, col });
  gridState.rows = Math.max(gridState.rows, row + 2);   // 下方留一格
  gridState.cols = Math.max(gridState.cols, col + 2);   // 右侧留一格
}

/**
 * 移除块后把网格收回去：只留到"最右下那个块"再各多一格，
 * 最少回到初始的 3 行 1 列。否则块越删越少、虚线框却一直留着。
 */
function shrinkGrid() {
  let maxRow = -1;
  let maxCol = -1;
  for (const b of gridState.blocks) {
    if (b.row > maxRow) maxRow = b.row;
    if (b.col > maxCol) maxCol = b.col;
  }
  gridState.rows = Math.max(3, maxRow + 2);
  gridState.cols = Math.max(1, maxCol + 2);
}

function removeBlock(row, col) {
  const before = gridState.blocks.length;
  gridState.blocks = gridState.blocks.filter((b) => !(b.row === row && b.col === col));
  if (gridState.blocks.length === before) return false;
  shrinkGrid();
  return true;
}

/** 渲染下方“数据块池”。 */
function renderBlockPool(onChange) {
  const pool = $('block-pool');
  if (!pool) return;

  const used = new Set(gridState.blocks.map((b) => b.type));
  pool.innerHTML = '';
  for (const t of BLOCK_TYPES) {
    const chip = document.createElement('div');
    chip.className = 'block-chip' + (used.has(t.key) ? ' used' : '');
    chip.textContent = t.label;
    chip.draggable = true;
    chip.dataset.type = t.key;
    chip.addEventListener('dragstart', (e) => {
      e.dataTransfer.setData('text/plain', 'block:' + t.key);
      e.dataTransfer.effectAllowed = 'copy';
    });
    pool.appendChild(chip);
  }
}

/** 渲染网格（虚线空槽 + 已放置的块）。 */
function renderGridEditor(onChange) {
  const wrap = $('grid-editor');
  if (!wrap) return;

  wrap.style.gridTemplateColumns = `repeat(${gridState.cols}, minmax(110px, 1fr))`;
  wrap.innerHTML = '';

  for (let r = 0; r < gridState.rows; r++) {
    for (let c = 0; c < gridState.cols; c++) {
      const placed = blockAt(r, c);
      const slot = document.createElement('div');
      slot.className = 'grid-slot' + (placed ? ' filled' : '');
      slot.dataset.row = r;
      slot.dataset.col = c;

      if (placed) {
        const def = BLOCK_TYPES.find((t) => t.key === placed.type);
        slot.innerHTML = `<span class="slot-label">${def ? def.label : placed.type}</span>`
                       + `<span class="slot-kind">R${r + 1}C${c + 1}</span>`;
        slot.title = '点击移除';
        slot.addEventListener('click', () => {
          if (removeBlock(r, c)) {
            // 池子里的 used 状态依赖 blocks，移除后必须一起重绘，否则那块一直是灰的
            renderGridEditor(onChange);
            renderBlockPool(onChange);
            onChange();
          }
        });
      } else {
        slot.textContent = '拖到这里';
      }

      slot.addEventListener('dragover', (e) => {
        e.preventDefault();
        e.dataTransfer.dropEffect = 'copy';
        slot.classList.add('drag-over');
      });
      slot.addEventListener('dragleave', () => slot.classList.remove('drag-over'));
      slot.addEventListener('drop', (e) => {
        e.preventDefault();
        slot.classList.remove('drag-over');
        const data = e.dataTransfer.getData('text/plain') || '';
        const type = data.startsWith('block:') ? data.slice(6) : data;
        if (!BLOCK_TYPES.some((t) => t.key === type)) return;
        placeBlock(type, r, c);
        renderGridEditor(onChange);
        renderBlockPool(onChange);
        onChange();
      });

      wrap.appendChild(slot);
    }
  }

  const hint = $('grid-hint');
  if (hint) {
    hint.textContent = gridState.blocks.length
      ? `已放置 ${gridState.blocks.length} 个块 · 网格 ${gridState.rows}×${gridState.cols}`
      : '网格为空时悬浮窗会使用「样式」下拉里的固定布局';
  }
}

/* ===================== 主界面主题 ===================== */

/** 把主题写到 <html data-theme>，CSS 变量会跟着切换。 */
function applyUiTheme(theme) {
  const t = theme === 'light' ? 'light' : 'dark';
  document.documentElement.dataset.theme = t;

  // 图表是用 canvas 画的，读不到 CSS 变量，这里给一份随主题变化的配色
  chartPalette = t === 'light'
    ? { grid: 'rgba(180,195,210,.75)', tick: '#64798c', avgLine: '#8fa4b6' }
    : { grid: 'rgba(34,49,64,.9)', tick: '#7d93a8', avgLine: '#7d93a8' };
}

let chartPalette = { grid: 'rgba(34,49,64,.9)', tick: '#7d93a8', avgLine: '#7d93a8' };

function syncRangeLabels() {  const pairs = [['scale', 2], ['backOpacity', 0], ['cornerRadius', 0], ['overlayChartHeight', 0]];
  for (const [key, digits] of pairs) {
    const el = $('s-' + key);
    const lab = $('s-' + key + '-val');
    if (el && lab) lab.textContent = Number(el.value).toFixed(digits);
  }
}

/* ===================== 事件绑定 ===================== */

function init() {
  // 标签页
  document.querySelectorAll('.tab').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.tab').forEach((b) => b.classList.remove('active'));
      document.querySelectorAll('.page').forEach((p) => p.classList.remove('active'));
      btn.classList.add('active');
      $('page-' + btn.dataset.page).classList.add('active');
      if (btn.dataset.page === 'monitor') drawChart((latest && latest.recentFrameTimes) || []);
      if (btn.dataset.page === 'history') send({ type: 'getSessions' });
      if (btn.dataset.page === 'settings') send({ type: 'getCandidates' });
      if (btn.dataset.page === 'monitor') send({ type: 'getCandidates' });
    });
  });

  // 历史记录相关
  $('btn-refresh-history').addEventListener('click', () => send({ type: 'getSessions' }));
  $('btn-open-records').addEventListener('click', () => send({ type: 'openRecordsFolder' }));

  $('btn-clear-history').addEventListener('click', () => {
    const n = Array.isArray(history) ? history.length : 0;
    if (n === 0) { alert('当前没有历史记录。'); return; }
    if (!confirm(`确定清空全部 ${n} 条历史记录吗？\n\n所有逐帧 CSV 也会一起删除，此操作不可撤销。`)) return;
    send({ type: 'clearSessions' });
  });
  $('btn-close-detail').addEventListener('click', () => $('history-detail').classList.add('hidden'));

  // 监测目标（监控页顶部下拉框）
  const targetSel = $('monitor-target-select');
  if (targetSel) {
    targetSel.addEventListener('change', () => {
      const pid = Number(targetSel.value) || 0;
      if (pid > 0) {
        const c = candidates.find((x) => x.pid === pid);
        send({ type: 'setManualTarget', name: c ? c.name : '', pid });
      } else {
        send({ type: 'clearManualTarget' });
      }
    });
  }
  const targetRefresh = $('btn-refresh-targets');
  if (targetRefresh) {
    targetRefresh.addEventListener('click', () => send({ type: 'getCandidates' }));
  }

  // 表单变更 → 防抖后立即下发（热更新，无需点保存）
  let pushTimer = null;
  const pushSettings = (immediate = false) => {
    if (pushTimer) clearTimeout(pushTimer);
    const fire = () => {
      pushTimer = null;
      send({ type: 'setSettings', data: collectSettings() });
    };
    if (immediate) fire();
    else pushTimer = setTimeout(fire, 250);   // 拖动滑块时避免每像素发一条
  };

  for (const [key, kind] of FIELDS) {
    const el = $('s-' + key);
    if (!el) continue;
    el.addEventListener('input', () => {
      if (kind === 'range') syncRangeLabels();
      // 滑块/颜色拖动用防抖，勾选框和数字框立即生效
      pushSettings(kind === 'checkbox');
    });
    if (kind === 'checkbox' || kind === 'color' || kind === 'range') {
      el.addEventListener('change', () => {
        syncRangeLabels();
        pushSettings(true);
      });
    }
  }

  // 位置预设
  document.querySelectorAll('.mini[data-pos]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const margin = 24;
      const vw = window.screen.width, vh = window.screen.height;
      const map = {
        lt: [margin, margin],
        rt: [vw - 320, margin],
        lb: [margin, vh - 220],
        rb: [vw - 320, vh - 220],
      };
      const [x, y] = map[btn.dataset.pos];
      $('s-x').value = Math.max(0, x);
      $('s-y').value = Math.max(0, y);
      pushSettings(true);
    });
  });

  $('btn-reset').addEventListener('click', () => {
    if (!confirm('把所有设置恢复为默认值？')) return;
    send({ type: 'resetSettings' });
    const hint = $('save-hint');
    hint.textContent = '已恢复默认';
    setTimeout(() => { hint.textContent = ''; }, 1800);
  });

  // 热键录制：点输入框后直接按组合键
  setupHotkeyRecorder($('s-hotkeyToggleOverlay'), pushSettings);
  setupHotkeyRecorder($('s-hotkeyOpenMain'), pushSettings);

  // 显示项：可勾选 + 拖动/箭头排序，改动即时下发
  renderOrderList((field, checked) => {
    if (field) {
      const el = $('s-' + field);
      if (el) el.checked = checked;
    }
    pushSettings(true);
  });

  // 网格布局编辑器
  const onGridChange = () => pushSettings(true);
  renderBlockPool(onGridChange);
  renderGridEditor(onGridChange);

  const btnGridClear = $('btn-grid-clear');
  if (btnGridClear) {
    btnGridClear.addEventListener('click', () => {
      gridState = { blocks: [], rows: 3, cols: 1 };
      renderGridEditor(onGridChange);
      renderBlockPool(onGridChange);
      onGridChange();
    });
  }

  const btnGridAuto = $('btn-grid-auto');
  if (btnGridAuto) {
    btnGridAuto.addEventListener('click', () => {
      gridState = {
        blocks: DEFAULT_GRID_BLOCKS.map((b) => ({ ...b })),
        rows: 6, cols: 2,
      };
      renderGridEditor(onGridChange);
      renderBlockPool(onGridChange);
      onGridChange();
    });
  }

  // 界面主题：切换后立即生效并重绘图表
  const uiThemeSel = $('s-uiTheme');
  if (uiThemeSel) {
    uiThemeSel.addEventListener('change', () => {
      applyUiTheme(uiThemeSel.value);
      const s = latest || {};
      drawChart(s.recentFrameTimes || [], s.low1PercentHistory || [], s.low01PercentHistory || []);
      drawFrameTimeChart(s.recentFrameTimes || []);
      pushSettings(true);
    });
  }

  // 配色主题：选深色/浅色时一次性覆盖三个颜色；选自定义则保留原样
  const themeSel = $('s-colorTheme');
  if (themeSel) {
    themeSel.addEventListener('change', () => {
      if (themeSel.value === 'custom') return;
      applyTheme(themeSel.value, pushSettings);
    });
  }

  // 调整位置：切换悬浮窗的拖动模式
  let dragMode = false;
  const dragBtn = $('btn-drag-overlay');
  const dragHint = $('drag-hint');
  if (dragBtn) {
    dragBtn.addEventListener('click', () => {
      dragMode = !dragMode;
      send({ type: 'setOverlayDragMode', enabled: dragMode });
      dragBtn.classList.toggle('dragging', dragMode);
      dragBtn.textContent = dragMode ? '完成调整' : '调整位置';
      if (dragHint) {
        dragHint.textContent = dragMode
          ? '拖动模式已开启：直接拖动屏幕上的悬浮窗，拖完点「完成调整」。'
          : '点「调整位置」后，直接拖动屏幕上的悬浮窗到想要的地方，拖完再点一次结束。';
      }
    });
  }

  window.addEventListener('resize', () => {
    const s = latest || {};
    drawChart(s.recentFrameTimes || [], s.low1PercentHistory || [], s.low01PercentHistory || []);
    drawFrameTimeChart(s.recentFrameTimes || []);
  });

  // 支持用 #monitor / #history / #settings 直接打开对应页签
  const hash = (location.hash || '').replace('#', '');
  if (hash) {
    const target = document.querySelector(`.tab[data-page="${hash}"]`);
    if (target) target.click();
  }

  connect();
}

document.addEventListener('DOMContentLoaded', init);
