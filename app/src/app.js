'use strict';

/* ===================== 连接层 ===================== */

const PORT = (window.gamefps && window.gamefps.wsPort) || 8731;

let ws = null;
let reconnectTimer = null;
let latest = null;
let settings = null;
/** 上一次发给主进程的关闭策略，避免每条设置广播都重复发一次 IPC。 */
let lastClosePolicy = null;

/** 用户是否正在设置页的某个输入控件上操作（热更新时用来抑制回填）。 */
/** 同步由后端拍板的布尔开关（开机自启 / 关闭行为）。 */
function syncAuthoritativeSwitches(s) {
  if (!s) return;
  const auto = $('s-autoStart');
  if (auto && auto.checked !== !!s.autoStart) auto.checked = !!s.autoStart;

  const close = $('s-minimizeOnClose');
  if (close) {
    const want = s.minimizeOnClose !== false;
    if (close.checked !== want) close.checked = want;
  }

  // 顺带给主进程也同步一次：托盘菜单改过关闭行为时，这里能兜住。
  // 只在真的变了时才发 —— 后端每次回推设置都会走到这里，
  // 无条件发等于让每一条设置广播都多一次 IPC。
  const policy = s.minimizeOnClose === false ? 'quit' : 'minimize';
  if (policy !== lastClosePolicy) {
    lastClosePolicy = policy;
    if (window.gamefps && window.gamefps.setClosePolicy) {
      window.gamefps.setClosePolicy(policy);
    }
  }
}

function isEditingForm() {
  const el = document.activeElement;
  // cx- 是准星页的控件；同样不能在拖动时被打断
  if (!el || !el.id || !(el.id.startsWith('s-') || el.id.startsWith('cx-'))) return false;
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
      // 这两个是"后端说了算"的开关：开机自启可能因注册表写入失败被后端弹回，
      // 关闭行为也可能被托盘改过。它们都不是输入框，回填不会打断打字，
      // 所以即便正在编辑其它字段也要同步，否则界面会显示成用户以为的状态。
      syncAuthoritativeSwitches(settings);
      break;
    case 'displays':
      renderDisplays(msg.data);
      break;
    case 'crosshairImages':
      renderCxUserImages(Array.isArray(msg.data) ? msg.data : []);
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

  // 详情卡可能被上次点击挪进了列表；先送回页面末尾，否则会被下面的 innerHTML 重建吞掉
  const pageEl = $('page-history');
  const detailEl = $('history-detail');
  if (detailEl && pageEl && detailEl.parentElement !== pageEl) pageEl.appendChild(detailEl);

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
      // 详情卡直接插到选中的记录卡后面，而不是固定在页面底部
      const detail = $('history-detail');
      if (detail) el.insertAdjacentElement('afterend', detail);
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

/* ===================== 屏幕准星 ===================== */

/** 与 C# CrosshairForm 的绘制分支一一对应。 */
const CX_STYLES = [
  { id: 'Cross',       icon: '+',  name: '十字' },
  { id: 'Dot',         icon: '●',  name: '圆点' },
  { id: 'Circle',      icon: '○',  name: '圆环' },
  { id: 'CrossDot',    icon: '✚',  name: '十字+点' },
  { id: 'CircleCross', icon: '⊕',  name: '圆环+十字' },
  { id: 'DotBox',      icon: '▣',  name: '方框+点' },
];

const CX_COLOR_PRESETS = [
  '#ff0000', '#00ff00', '#0000ff', '#00ffff', '#ff00ff',
  '#ffff00', '#ffffff', '#ff8800', '#ff0088',
];

/** 内置的图片准星（随前端打包，选中后由后端复制到本地磁盘）。 */
const CX_PRESET_IMAGES = [
  { file: 'cat.png',    name: 'Cat' },
  { file: 'donk.png',   name: 'donk' },
  { file: 's1mple.png', name: 's1mple' },
  { file: 'ropz.png',   name: 'ropz' },
  { file: 'MMW2.0.png', name: 'MMW 2.0' },
  { file: 'SSCJ.png',   name: 'SSCJ' },
  { file: 'TShape.png', name: 'T 字准星' },
];

/** 图片准星在 Style 字段里用的前缀（后端只在程序绘制时读 Style）。 */
const CX_PRESET_PREFIX = 'Preset_';

/** 用户预设的本地存储键；一个预设 = 一份完整的准星设置快照。 */
const CX_PRESETS_KEY = 'gfps.cxPresets';

/** 预览图片的本地缓存键前缀（按图片文件名存 dataURL，重启后预览还在）。 */
const CX_IMG_CACHE_PREFIX = 'gfps.cxImg:';

/** 当前选中的样式（不在 settings.crosshair 里存 UI 态以外的东西） */
let cxStyle = 'Cross';

/** 渲染方式：'procedural' 程序绘制 / 'image' 图片绘制（内置预设或自选图片）。 */
let cxMode = 'procedural';

/** 当前图片准星的本地路径（后端最后落盘的那个）。 */
let cxImagePath = null;

/** 当前预设图文件名（cxMode === 'preset' 时有效）。 */
let cxPresetFile = null;

/** 预览用的已解码图片。 */
let cxPreviewImage = null;
let cxPreviewImageKey = null;

/**
 * 后端已落盘的自选图片（和内置预设一起排在网格里）。
 * NexBox 的做法：选完自己的图，它就变成预设网格里的一格，下次直接点。
 */
let cxUserImages = [];

/** 后端回推的显示器列表（index/primary/width/height），预览按目标屏分辨率等比例缩放用。 */
let cxDisplays = [];

/** 由 init() 注入：把准星改动防抖后下发给后端。 */
let cxPush = null;
/** 由 init() 注入：立即下发（点选类操作，不等防抖）。 */
let cxPushNow = null;

function pushCrosshair() {
  if (cxPush) cxPush();
}

function pushCrosshairNow() {
  if (cxPushNow) cxPushNow();
}

/** 等待显示器列表就绪后再落位的显示器序号（见 fillCrosshair）。 */
let cxMonitorPending = null;

function cxColor(hex, opacity) {
  const h = String(hex || '#ff0000').replace('#', '');
  const r = parseInt(h.slice(0, 2), 16) || 0;
  const g = parseInt(h.slice(2, 4), 16) || 0;
  const b = parseInt(h.slice(4, 6), 16) || 0;
  const a = Math.max(0, Math.min(255, Number(opacity) || 0)) / 255;
  return `rgba(${r},${g},${b},${a})`;
}

/** 从界面读出完整的准星设置；缺的字段沿用后端当前值。 */
function readCrosshair() {
  const base = Object.assign({}, (settings && settings.crosshair) || {});
  const val = (id, fallback) => {
    const el = $(id);
    return el ? el.value : fallback;
  };
  const num = (id, fallback) => {
    const raw = val(id, '');
    // 空字符串不能当 0 —— Number('') 是 0，会把没设置过的控件读成 0
    if (raw === '' || raw === null || raw === undefined) return fallback;
    const n = Number(raw);
    return isFinite(n) ? n : fallback;
  };

  base.enabled = $('cx-enabled') ? $('cx-enabled').checked : !!base.enabled;
  base.style = cxStyle;
  base.size = num('cx-size', base.size ?? 20);
  base.thickness = num('cx-thickness', base.thickness ?? 2);
  base.gap = num('cx-gap', base.gap ?? 0);
  base.dotSize = num('cx-dot', base.dotSize ?? 2);
  base.opacity = num('cx-opacity', base.opacity ?? 255);
  base.color = val('cx-color', base.color || '#ff0000');
  base.outlineEnabled = $('cx-outline-enabled') ? $('cx-outline-enabled').checked : !!base.outlineEnabled;
  base.outlineColor = val('cx-outline-color', base.outlineColor || '#000000');
  base.outlineThickness = num('cx-outline-thickness', base.outlineThickness ?? 1);
  base.monitorIndex = num('cx-monitor', base.monitorIndex ?? -1);
  base.offsetX = num('cx-offset-x', base.offsetX ?? 0);
  base.offsetY = num('cx-offset-y', base.offsetY ?? 0);
  base.hotkeyToggle = val('cx-hotkey', base.hotkeyToggle || 'F8');

  // ---- 渲染方式与图片 ----
  base.useCustomImage = cxMode !== 'procedural';
  base.style = cxMode === 'procedural'
    ? cxStyle
    : (cxPresetFile ? CX_PRESET_PREFIX + cxPresetFile : 'Custom');
  base.imageSize = num('cx-image-size', base.imageSize ?? 64);
  base.customImagePath = base.customImagePath ?? null;

  // ---- 按住显示 ----
  base.holdEnabled = $('cx-hold-enabled') ? $('cx-hold-enabled').checked : !!base.holdEnabled;
  base.holdKey = val('cx-hold-key', base.holdKey || 'Mouse2');
  base.holdDelayMs = num('cx-hold-delay', base.holdDelayMs ?? 0);

  return base;
}

/** 后端设置 → 界面控件（不含画布重绘，调用方决定何时画）。 */
function fillCrosshair(c) {
  if (!c) return;

  // 图片模式时 c.style 是 "Preset_xxx"，不能拿它覆盖程序绘制的样式选择
  const isPresetStyle = typeof c.style === 'string' && c.style.startsWith(CX_PRESET_PREFIX);
  if (!isPresetStyle && CX_STYLES.some((s) => s.id === c.style)) cxStyle = c.style;

  cxImagePath = c.customImagePath || null;
  cxPresetFile = isPresetStyle ? c.style.slice(CX_PRESET_PREFIX.length) : null;
  cxMode = c.useCustomImage ? 'image' : 'procedural';

  const set = (id, v) => { const el = $(id); if (el) el.value = v; };
  const check = (id, v) => { const el = $(id); if (el) el.checked = !!v; };

  check('cx-enabled', c.enabled);
  set('cx-size', c.size ?? 20);
  set('cx-thickness', c.thickness ?? 2);
  set('cx-gap', c.gap ?? 0);
  set('cx-dot', c.dotSize ?? 2);
  set('cx-opacity', c.opacity ?? 255);
  set('cx-color', c.color || '#ff0000');
  check('cx-outline-enabled', c.outlineEnabled);
  set('cx-outline-color', c.outlineColor || '#000000');
  set('cx-outline-thickness', c.outlineThickness ?? 1);
  // 显示器下拉的选项由 displays 消息单独填充，可能与 settings 一先一后到达。
  // 选项还没建好时给 select 设一个不存在的值会被浏览器置成空串，
  // 结果就是"选的 2 号屏"被悄悄改回主屏 —— 这里先把它存起来，等列表到了再落位。
  const monEl = $('cx-monitor');
  const wantMon = String(c.monitorIndex ?? -1);
  if (monEl && [...monEl.options].some((o) => o.value === wantMon)) {
    monEl.value = wantMon;
    cxMonitorPending = null;
  } else {
    cxMonitorPending = wantMon;
  }
  set('cx-offset-x', c.offsetX ?? 0);
  set('cx-offset-y', c.offsetY ?? 0);
  set('cx-hotkey', c.hotkeyToggle || 'F8');
  set('cx-image-size', c.imageSize ?? 64);
  check('cx-hold-enabled', c.holdEnabled);
  set('cx-hold-key', c.holdKey || 'Mouse2');
  set('cx-hold-delay', c.holdDelayMs ?? 0);

  const nameEl = $('cx-image-name');
  if (nameEl) nameEl.textContent = cxImagePath ? basenameOf(cxImagePath) : '未选择图片';

  renderCxStyleButtons();
  renderCxColors();
  renderCxRenderMode();
  renderCxPresetImages();
  renderCxPresets();
  refreshCxPreviewImage();
  syncCxLabels();
}

function syncCxLabels() {
  const pairs = [
    ['cx-size', ''], ['cx-thickness', ''], ['cx-gap', ''],
    ['cx-dot', ''], ['cx-opacity', ''], ['cx-outline-thickness', ''],
    ['cx-image-size', ''],
  ];
  for (const [id] of pairs) {
    const el = $(id);
    const out = $(id + '-val');
    if (el && out) out.textContent = el.value;
  }
}

function renderCxStyleButtons() {
  const box = $('cx-styles');
  if (!box) return;
  box.innerHTML = '';
  for (const s of CX_STYLES) {
    const btn = document.createElement('button');
    btn.className = 'cx-style' + (s.id === cxStyle ? ' active' : '');
    btn.type = 'button';
    btn.dataset.style = s.id;
    btn.innerHTML = `<i>${s.icon}</i><span>${s.name}</span>`;
    btn.addEventListener('click', () => {
      cxStyle = s.id;
      renderCxStyleButtons();
      drawCrosshairPreview();
      pushCrosshair();
    });
    box.appendChild(btn);
  }
}

function renderCxColors() {
  const box = $('cx-colors');
  if (!box) return;
  const current = ($('cx-color') && $('cx-color').value || '').toLowerCase();
  box.innerHTML = '';
  for (const c of CX_COLOR_PRESETS) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'cx-swatch' + (c === current ? ' active' : '');
    b.style.background = c;
    b.title = c;
    b.addEventListener('click', () => {
      if ($('cx-color')) $('cx-color').value = c;
      renderCxColors();
      drawCrosshairPreview();
      pushCrosshair();
    });
    box.appendChild(b);
  }
}

/** 显示器下拉框。value 用 -1 表示主显示器。 */
function renderDisplays(list) {
  const sel = $('cx-monitor');
  cxDisplays = Array.isArray(list) ? list : [];
  if (!sel) return;

  // 重建选项会把选择清掉，先把界面当前真正选中的那个记下来
  const before = sel.value;
  sel.innerHTML = '';

  const auto = document.createElement('option');
  auto.value = '-1';
  auto.textContent = '主显示器（跟随系统）';
  sel.appendChild(auto);

  (Array.isArray(list) ? list : []).forEach((d) => {
    const opt = document.createElement('option');
    opt.value = String(d.index);
    opt.textContent = `${d.name} · ${d.width}×${d.height}${d.primary ? ' · 主' : ''}`;
    sel.appendChild(opt);
  });

  // 优先级：等列表落位的待定值 → 界面原本选中的 → 设置里的值 → 主屏。
  // 待定值排在前面，因为它来自后端设置；此时下拉里往往只有默认的 "-1"，
  // 那个 -1 只是 HTML 的初始项，不是用户的选择。
  const has = (v) => [...sel.options].some((o) => o.value === v);
  let wanted = null;

  if (cxMonitorPending !== null && has(cxMonitorPending)) {
    wanted = cxMonitorPending;
  }
  cxMonitorPending = null;

  if (wanted === null && before && has(before)) wanted = before;

  if (wanted === null) {
    const fromSettings = String((settings && settings.crosshair && settings.crosshair.monitorIndex) ?? -1);
    wanted = has(fromSettings) ? fromSettings : '-1';
  }

  sel.value = wanted;
  if (sel.value !== wanted) sel.value = '-1';   // 该序号已不存在（拔了屏）→ 回主屏
}

/** 画布预览：按与 C# 相同的几何实时还原准星外观。 */
/** 取文件名部分（兼容 Windows 反斜杠路径）。 */
function basenameOf(p) {
  return String(p || '').split(/[\\/]/).pop();
}

/**
 * 收到后端的自选图列表：更新网格。
 * 自选图和内置预设并排 —— 这就是 NexBox 的行为，选过的图下次直接点。
 */
function renderCxUserImages(list) {
  cxUserImages = list
    .map((it) => ({ name: it.name || basenameOf(it.path), path: it.path }))
    .filter((it) => it.name);
  renderCxPresetImages();
}

/**
 * 把 app/src 下的资源读成 dataURL。
 * 页面以 file:// 加载，渲染进程里 fetch 本地文件会被 Chromium 拒绝，
 * 所以优先走 preload 的 Node 桥；浏览器里直接打开时回落到相对 URL。
 */
function readAssetDataUrl(rel) {
  try {
    if (window.gamefps && typeof window.gamefps.readAssetDataUrl === 'function') {
      return window.gamefps.readAssetDataUrl(rel);
    }
  } catch { /* 桥不可用就走回落分支 */ }
  return null;
}

/** 渲染方式分段按钮：切换时同步面板可见性。 */
function renderCxRenderMode() {
  const seg = $('cx-render-mode');
  if (seg) {
    for (const btn of seg.querySelectorAll('.cx-seg')) {
      btn.classList.toggle('active', btn.dataset.mode === cxMode);
    }
  }

  updateCxDims();

  const panel = $('cx-image-panel');
  const note = $('cx-procedural-note');
  if (panel) panel.hidden = cxMode === 'procedural';
  if (note) note.hidden = cxMode !== 'procedural';

  // 程序绘制的那几个参数在图片模式下不起作用，直接藏掉，免得误导
  const styleBox = $('cx-styles');
  if (styleBox) styleBox.hidden = cxMode !== 'procedural';
}

/** 内置预设 + 已导入的自选图，一起排成缩略图网格。 */
function renderCxPresetImages() {
  const box = $('cx-preset-images');
  if (!box) return;
  box.innerHTML = '';

  const activeName = cxMode === 'image'
    ? (cxPresetFile || (cxImagePath ? basenameOf(cxImagePath) : null))
    : null;

  // 内置预设
  for (const p of CX_PRESET_IMAGES) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'cx-preset-img' + (activeName === p.file ? ' active' : '');
    btn.title = p.name;
    const img = document.createElement('img');
    const thumbRel = `assets/crosshair-presets/${p.file}`;
    img.src = readAssetDataUrl(thumbRel) || `./${thumbRel}`;
    img.alt = p.name;
    btn.appendChild(img);
    const label = document.createElement('span');
    label.textContent = p.name;
    btn.appendChild(label);
    btn.addEventListener('click', () => applyCxPresetImage(p.file));
    box.appendChild(btn);
  }

  // 用户自己导入的图：和预设并排，一格一张
  for (const u of cxUserImages) {
    const name = u.name || basenameOf(u.path || '');
    if (!name) continue;

    // 点内置预设时,后端也会把图落盘一份到用户图目录,回推列表里就多了
    // 与内置同名的副本 —— 跳过它,同一张图只留内置那一格
    if (CX_PRESET_IMAGES.some((p) => p.file === name)) continue;

    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'cx-preset-img own' + (activeName === name ? ' active' : '');
    btn.title = name;

    // 删除这一格：删盘上的文件；删的是当前在用图时后端自动回退程序绘制
    const del = document.createElement('span');
    del.className = 'cx-img-del';
    del.title = '删除这张图片';
    del.textContent = '✕';
    del.addEventListener('click', (e) => {
      e.stopPropagation();
      if (!confirm(`确定删除图片「${name}」吗？\n屏幕准星若正在用它，会自动回到程序绘制。`)) return;
      try { localStorage.removeItem(CX_IMG_CACHE_PREFIX + name); } catch { /* 忽略 */ }
      cxUserImages = cxUserImages.filter((u) => (u.name || basenameOf(u.path || '')) !== name);
      renderCxPresetImages();
      send({ type: 'deleteCrosshairImage', data: { name } });
    });
    btn.appendChild(del);

    const img = document.createElement('img');
    img.alt = name;
    // 自选图的字节在 localStorage 里有缓存；没缓存就先留空，等图片列表回推后补
    const cached = localStorage.getItem(CX_IMG_CACHE_PREFIX + name);
    if (cached) img.src = cached;
    else {
      img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(
        '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64">' +
        '<rect width="64" height="64" fill="#2a2a2a"/></svg>');
    }
    btn.appendChild(img);

    const label = document.createElement('span');
    label.textContent = name;
    btn.appendChild(label);
    btn.addEventListener('click', () => applyCxUserImage(name));
    box.appendChild(btn);
  }
}

/** 应用一张已落盘的自选图（字节从 localStorage 缓存取，没有就重新要一次）。 */
async function applyCxUserImage(name) {
  cxMode = 'image';
  cxPresetFile = name;
  renderCxRenderMode();
  renderCxPresetImages();

  const nameEl = $('cx-image-name');
  if (nameEl) nameEl.textContent = name;

  const cached = localStorage.getItem(CX_IMG_CACHE_PREFIX + name);
  if (cached) {
    send({ type: 'setCrosshairImage', data: { name, dataUrl: cached } });
    await refreshCxPreviewImage(name);
    drawCrosshairPreview();
  }
  // 没缓存说明是本机已落盘但界面还没见过这张图，后端会按现有路径直接应用
}

/** 选中内置预设图：把图片字节发给后端落盘，后端随即把路径写进设置。 */
async function applyCxPresetImage(file) {
  const rel = `assets/crosshair-presets/${file}`;
  let dataUrl = readAssetDataUrl(rel);

  if (!dataUrl) {
    try {
      const res = await fetch(`./${rel}`);
      const blob = await res.blob();
      dataUrl = await new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = reject;
        reader.readAsDataURL(blob);
      });
    } catch (err) {
      console.warn('预设图片加载失败', err);
      return;
    }
  }

  cxMode = 'image';
  cxPresetFile = file;
  renderCxRenderMode();
  renderCxPresetImages();
  const nameEl = $('cx-image-name');
  if (nameEl) nameEl.textContent = file;      // 后端回推路径之前先显示文件名
  await sendCxImage(file, dataUrl);
}

/** 用户自选图片。 */
function pickCxImage(file) {
  if (!file) return;
  const reader = new FileReader();
  reader.onload = async () => {
    cxMode = 'image';
    // 选自己的图 = 选中网格里「这一格」，文件名就是这一格的标识
    cxPresetFile = basenameOf(file.name);
    renderCxRenderMode();
    const nameEl = $('cx-image-name');
    if (nameEl) nameEl.textContent = file.name;

    // 原始大图不该一导入就被压小：图片原始短边比当前尺寸大时，
    // 自动把尺寸升到 1:1（≤512），换大图第一眼就是原大小
    const probe = new Image();
    probe.onload = () => {
      const native = Math.round(Math.min(probe.naturalWidth || 0, probe.naturalHeight || 0));
      const slider = $('cx-image-size');
      if (native > 0 && slider && native > Number(slider.value)) {
        slider.value = String(Math.min(1024, native));
        syncCxLabels();
        drawCrosshairPreview();
      }
    };
    probe.src = String(reader.result);

    await sendCxImage(file.name, String(reader.result));
  };
  reader.readAsDataURL(file);
}

/**
 * 把图片交给后端：后端解码后写到 %LOCALAPPDATA%\GameFPS\crosshair\，
 * 再把 UseCustomImage / CustomImagePath 写进设置并回推 —— 前端不直接碰磁盘。
 */
async function sendCxImage(name, dataUrl) {
  rememberCxImage(name, dataUrl);
  send({ type: 'setCrosshairImage', data: { name, dataUrl } });
  await refreshCxPreviewImage(name);
  drawCrosshairPreview();
  // 后端落盘后会回推 crosshairImages；这里先乐观插进网格，
  // 免得用户选完图要等一个来回才在预设里看到它
  const bare = basenameOf(name);
  if (bare && !cxUserImages.some((u) => u.name === bare)) {
    cxUserImages.push({ name: bare, path: null });
    renderCxPresetImages();
  }
}

/** 按文件名缓存图片 dataURL，供预览与重启后复用。 */
function rememberCxImage(name, dataUrl) {
  try {
    localStorage.setItem(CX_IMG_CACHE_PREFIX + basenameOf(name), dataUrl);
  } catch { /* 配额满了就只丢了预览缓存 */ }
}

/** 载入预览图片：本地缓存优先，预设图回落到打包资源。 */
function refreshCxPreviewImage(explicitName) {
  let key = null;
  let src = null;

  if (cxMode === 'image') {
    if (cxPresetFile) {
      key = cxPresetFile;
      src = localStorage.getItem(CX_IMG_CACHE_PREFIX + key)
        || `./assets/crosshair-presets/${key}`;
    } else {
      key = explicitName ? basenameOf(explicitName) : (cxImagePath ? basenameOf(cxImagePath) : null);
      if (key) src = localStorage.getItem(CX_IMG_CACHE_PREFIX + key);
    }
  }

  if (!src) {
    cxPreviewImage = null;
    cxPreviewImageKey = null;
    updateCxDims();
    return;
  }

  if (cxPreviewImageKey === src) return;

  const img = new Image();
  img.onload = () => {
    cxPreviewImage = img;
    cxPreviewImageKey = src;
    updateCxDims();
    drawCrosshairPreview();
  };
  img.onerror = () => {
    cxPreviewImage = null;
    cxPreviewImageKey = null;
    updateCxDims();
    drawCrosshairPreview();
  };
  img.src = src;
}

/** 显示当前图片的原始像素，方便把尺寸滑块对准 1:1。 */
function updateCxDims() {
  const el = $('cx-image-dims');
  if (!el) return;
  if (cxMode === 'image' && cxPreviewImage && (cxPreviewImage.naturalWidth || cxPreviewImage.width)) {
    const w = cxPreviewImage.naturalWidth || cxPreviewImage.width;
    const h = cxPreviewImage.naturalHeight || cxPreviewImage.height;
    el.textContent = `原图 ${w}×${h}`;
  } else {
    el.textContent = '';
  }
}

// ---------- 我的预设 ----------

function readCxPresets() {
  try {
    const raw = localStorage.getItem(CX_PRESETS_KEY);
    const list = raw ? JSON.parse(raw) : [];
    return Array.isArray(list) ? list : [];
  } catch {
    return [];
  }
}

function writeCxPresets(list) {
  try {
    localStorage.setItem(CX_PRESETS_KEY, JSON.stringify(list));
  } catch { /* 存不下就静默失败，不影响当前准星 */ }
}

/** 准星的出厂参数（「恢复默认」用）。 */
function cxDefaults() {
  return {
    enabled: (settings && settings.crosshair && settings.crosshair.enabled) || false,
    useCustomImage: false,
    style: 'Cross',
    size: 20,
    thickness: 2,
    color: '#ff0000',
    gap: 0,
    dotSize: 2,
    opacity: 255,
    imageSize: 64,
    customImagePath: null,
    monitorIndex: -1,
    offsetX: 0,
    offsetY: 0,
    outlineEnabled: false,
    outlineColor: '#000000',
    outlineThickness: 1,
    hotkeyToggle: 'F8',
    holdEnabled: false,
    holdKey: 'Mouse2',
    holdDelayMs: 0,
  };
}

function renderCxPresets() {
  const box = $('cx-presets');
  if (!box) return;

  const list = readCxPresets();
  box.innerHTML = '';

  const empty = $('cx-presets-empty');
  if (empty) empty.hidden = list.length > 0;

  for (const preset of list) {
    const card = document.createElement('div');
    card.className = 'cx-preset-card';

    const name = document.createElement('b');
    name.textContent = preset.name;
    card.appendChild(name);

    const meta = document.createElement('span');
    meta.className = 'muted small';
    meta.textContent = describeCxPreset(preset.settings);
    card.appendChild(meta);

    const actions = document.createElement('div');
    actions.className = 'cx-preset-actions';

    const useBtn = document.createElement('button');
    useBtn.type = 'button';
    useBtn.className = 'mini';
    useBtn.textContent = '应用';
    useBtn.addEventListener('click', () => {
      fillCrosshair(preset.settings);
      drawCrosshairPreview();
      pushCrosshairNow();
    });

    const delBtn = document.createElement('button');
    delBtn.type = 'button';
    delBtn.className = 'mini danger';
    delBtn.textContent = '删除';
    delBtn.addEventListener('click', () => {
      writeCxPresets(readCxPresets().filter((p) => p.id !== preset.id));
      renderCxPresets();
    });

    actions.appendChild(useBtn);
    actions.appendChild(delBtn);
    card.appendChild(actions);
    box.appendChild(card);
  }
}

function describeCxPreset(c) {
  if (!c) return '';
  if (c.useCustomImage) {
    const file = String(c.style || '').startsWith(CX_PRESET_PREFIX)
      ? String(c.style).slice(CX_PRESET_PREFIX.length)
      : basenameOf(c.customImagePath || '自定义图片');
    return `图片 · ${file}`;
  }
  const style = CX_STYLES.find((s) => s.id === c.style);
  return `${style ? style.name : '十字'} · 大小 ${c.size} · ${c.color}`;
}

/**
 * 打开预设命名对话框。
 * 注意不能用 window.prompt：Electron 渲染进程里 prompt 是被禁用的，
 * 调用直接返回 null，表现就是"点了按钮没反应"。
 */
function openCxPresetDialog() {
  const dlg = $('cx-preset-dialog');
  const input = $('cx-preset-name');
  if (!dlg || !input) return;

  input.value = `准星 ${readCxPresets().length + 1}`;
  dlg.hidden = false;
  input.focus();
  input.select();
}

function closeCxPresetDialog() {
  const dlg = $('cx-preset-dialog');
  if (dlg) dlg.hidden = true;
}

function commitCxPreset() {
  const input = $('cx-preset-name');
  const name = (input ? input.value : '').trim();
  if (!name) { if (input) input.focus(); return; }

  const list = readCxPresets();
  list.push({
    id: `preset_${Date.now()}`,
    name,
    settings: readCrosshair(),
  });
  writeCxPresets(list);
  renderCxPresets();
  closeCxPresetDialog();
}

/**
 * 按住键录制：键盘键走 keydown，鼠标键走 mousedown（右键要拦掉 contextmenu，
 * 否则会同时弹出右键菜单）。输出与后端 CrosshairHold.ParseKey 对齐。
 */
function setupCxHoldRecorder() {
  const el = $('cx-hold-key');
  if (!el) return;

  const commit = (token) => {
    el.value = token;
    pushCrosshairNow();
  };

  const keyToken = (e) => {
    if (e.key === 'Escape' || e.key === 'Esc') { el.blur(); return null; }
    if (e.key === ' ') return 'Space';
    if (e.key.length === 1) return e.key.toUpperCase();
    if (/^F\d{1,2}$/.test(e.key)) return e.key.toUpperCase();
    const map = {
      ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
      Control: 'ControlKey', Shift: 'ShiftKey', Alt: 'Menu', Meta: 'LWin',
      Enter: 'Return', Tab: 'Tab', Backspace: 'Back', Delete: 'Delete',
      Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown',
      Insert: 'Insert', CapsLock: 'CapsLock',
    };
    return map[e.key] || e.key;
  };

  el.addEventListener('keydown', (e) => {
    e.preventDefault();
    const token = keyToken(e);
    if (token) commit(token);
  });

  el.addEventListener('mousedown', (e) => {
    e.preventDefault();
    const map = { 0: 'Mouse1', 1: 'Mouse3', 2: 'Mouse2', 3: 'Mouse4', 4: 'Mouse5' };
    const token = map[e.button];
    if (token) commit(token);
  });

  el.addEventListener('contextmenu', (e) => e.preventDefault());
}

/// 预览用的 alpha 能量补偿：与后端 PrepareSource + BoostAlpha 保持同一策略。
/// 细线图（donk 只有 99 个非透明像素）一次性缩到 64px，覆盖率被插值摊到更大
/// 面积上，单像素亮度掉光；按 alpha 总和补一个增益就恢复可见、且线条不会变粗。
function downscaleForPreview(img, target) {
  const w = img.naturalWidth || img.width;
  const h = img.naturalHeight || img.height;
  if (!w || !h || target >= w) return img;

  try {
    const c = document.createElement('canvas');
    c.width = target;
    c.height = target;
    const cg = c.getContext('2d');
    cg.imageSmoothingEnabled = true;
    cg.imageSmoothingQuality = 'high';
    cg.clearRect(0, 0, target, target);
    cg.drawImage(img, 0, 0, target, target);

    // 源图 alpha 总量（在后端是 ToBitmap 后逐像素累加，这里等价）
    const srcCv = document.createElement('canvas');
    srcCv.width = w;
    srcCv.height = h;
    const sg = srcCv.getContext('2d');
    sg.drawImage(img, 0, 0, w, h);
    const srcA = sg.getImageData(0, 0, w, h).data;

    let srcEnergy = 0;
    for (let i = 3; i < srcA.length; i += 4) srcEnergy += srcA[i];
    if (srcEnergy <= 0) return c;

    const dst = cg.getImageData(0, 0, target, target);
    let dstEnergy = 0;
    for (let i = 3; i < dst.data.length; i += 4) dstEnergy += dst.data[i];
    if (dstEnergy <= 0) return c;

    const gain = Math.min(16, srcEnergy / dstEnergy);
    if (gain <= 1.05) return c;

    for (let i = 3; i < dst.data.length; i += 4) {
      const a = dst.data[i];
      if (a === 0) continue;
      dst.data[i] = Math.min(255, Math.round(a * gain));
    }
    cg.putImageData(dst, 0, 0);
    return c;
  } catch (e) {
    return img;
  }
}

/** 准星目标屏的分辨率：优先用后端回推的显示器列表（含 primary 标记），
 *  列表还没到时退回主窗口所在屏。预览按它等比例缩放 = 所见即所得。 */
function cxTargetScreen() {
  const idx = Number(($('cx-monitor') || {}).value);
  const list = cxDisplays;
  if (Array.isArray(list) && list.length) {
    const pick = list.find((d) => Number(d.index) === idx)
      || list.find((d) => d.primary)
      || list[0];
    if (pick && pick.width > 0 && pick.height > 0) return { w: pick.width, h: pick.height };
  }
  return { w: window.screen.width || 1920, h: window.screen.height || 1080 };
}

function drawCrosshairPreview() {
  const cv = $('cx-preview');
  if (!cv) return;

  // 格子本身就是屏幕的缩影：宽固定，高按目标屏宽高比走
  // （16:9 屏 → 260×146，4:3 → 260×195），不再是正方形
  const scr = cxTargetScreen();
  const cssW = Math.round(cv.getBoundingClientRect().width) || 260;
  const cssH = Math.max(60, Math.round(cssW * scr.h / scr.w));
  if (cv.style.height !== cssH + 'px') cv.style.height = cssH + 'px';

  const rect = cv.getBoundingClientRect();
  const w = Math.max(1, Math.round(rect.width || 240));
  const h = Math.max(1, Math.round(rect.height || Math.round(w * scr.h / scr.w)));
  const dpr = window.devicePixelRatio || 1;

  if (cv.width !== Math.round(w * dpr) || cv.height !== Math.round(h * dpr)) {
    cv.width = Math.round(w * dpr);
    cv.height = Math.round(h * dpr);
  }

  const g = cv.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);

  const s = readCrosshair();

  // 预览格子 = 目标屏的等比例缩影：画布按屏幕宽高比缩放，准星按同一比例画，
  // 预览里的大小就是它在屏幕上的真实占比
  let fit = Math.min(w / scr.w, h / scr.h);

  // 图片准星：后端把图片拉伸成正方形画在 size×size 内，这里用同样的几何预览，
  // 透明度也照样作用在图上，做到"预览即所见"。
  if (cxMode !== 'procedural' && cxPreviewImage) {
    const target = Math.max(8, Number(s.imageSize) || 64);
    // 等比例下准星不会超过屏幕，但极端设置（尺寸比屏幕还大）时收一下防溢出
    fit = Math.min(fit, (Math.min(w, h) - 8) / target);
    const draw = target * fit;
    g.globalAlpha = Math.max(0, Math.min(255, Number(s.opacity) || 0)) / 255;
    // 后端 RenderImageBitmap 会先把大图预缩到目标尺寸的 3 倍再画，
    // 避免细线图（donk/s1mple/TShape 这类）被一次性双三次拉糊。
    // 这里同样走一遍中间尺寸，否则预览会比实际准星更暗、更看不清。
    const src = downscaleForPreview(cxPreviewImage, Math.round(draw));
    g.drawImage(src, (w - draw) / 2, (h - draw) / 2, draw, draw);
    g.globalAlpha = 1;
    updateCxPreviewCaption(scr);
    return;
  }

  const thickness = Math.max(1, s.thickness);
  const outline = s.outlineEnabled && s.outlineThickness > 0 ? s.outlineThickness : 0;
  const half = thickness / 2 + outline;
  const span = Math.max(
    s.gap + s.size + half,
    s.size + half,
    s.dotSize / 2 + outline,
    3,
  );

  // 同一个 fit：程序绘制的准星也按屏幕等比例，跟图片模式口径一致
  const scale = Math.min(fit, (Math.min(w, h) / 2 - 4) / span);

  g.save();
  g.translate(w / 2, h / 2);
  g.scale(scale, scale);
  g.lineCap = 'round';
  g.lineJoin = 'miter';

  const main = cxColor(s.color, s.opacity);
  const line = cxColor(s.outlineColor, s.opacity);
  const size = Math.max(1, s.size);
  const gap = Math.max(0, s.gap);
  const dot = Math.max(0, s.dotSize);

  const arms = (color, width) => {
    g.strokeStyle = color;
    g.lineWidth = width;
    g.beginPath();
    g.moveTo(0, -gap - size); g.lineTo(0, -gap);
    g.moveTo(0, gap);         g.lineTo(0, gap + size);
    g.moveTo(-gap - size, 0); g.lineTo(-gap, 0);
    g.moveTo(gap, 0);         g.lineTo(gap + size, 0);
    g.stroke();
  };
  const ring = (color, width, extra) => {
    g.strokeStyle = color;
    g.lineWidth = width;
    g.beginPath();
    g.arc(0, 0, size + extra, 0, Math.PI * 2);
    g.stroke();
  };
  const boxRect = (color, width, extra) => {
    g.strokeStyle = color;
    g.lineWidth = width;
    g.strokeRect(-(size + extra), -(size + extra), (size + extra) * 2, (size + extra) * 2);
  };
  const dotCircle = (color, d) => {
    if (d <= 0) return;
    g.fillStyle = color;
    g.beginPath();
    g.arc(0, 0, d / 2, 0, Math.PI * 2);
    g.fill();
  };

  const ow = thickness + outline * 2;

  switch (s.style) {
    case 'Dot':
      if (outline > 0) dotCircle(line, dot + outline * 2);
      dotCircle(main, dot);
      break;
    case 'Circle':
      if (outline > 0) ring(line, ow, outline);
      ring(main, thickness, 0);
      break;
    case 'CrossDot':
      if (outline > 0) { arms(line, ow); dotCircle(line, dot + outline * 2); }
      arms(main, thickness);
      dotCircle(main, dot);
      break;
    case 'CircleCross':
      if (outline > 0) { ring(line, ow, outline); arms(line, ow); }
      ring(main, thickness, 0);
      arms(main, thickness);
      break;
    case 'DotBox':
      if (outline > 0) { boxRect(line, ow, outline); dotCircle(line, dot + outline * 2); }
      boxRect(main, thickness, 0);
      dotCircle(main, dot);
      break;
    default:
      if (outline > 0) arms(line, ow);
      arms(main, thickness);
      break;
  }

  g.restore();
  updateCxPreviewCaption(scr);
}

/** 预览说明：标注当前按哪块屏的分辨率等比例，所见即所得。 */
function updateCxPreviewCaption(scr) {
  const el = document.querySelector('.cx-preview-caption');
  if (el) el.textContent = `实时预览 · 按 ${scr.w}×${scr.h} 等比例（棋盘格 = 透明区域）`;
}

/* ===================== 设置页 ===================== */

const FIELDS = [
  ['overlayEnabled', 'checkbox'],
  ['usePresetStyle', 'checkbox'],
  ['overlayStyle', 'number'],
  ['minimizeOnClose', 'checkbox'],
  ['autoStart', 'checkbox'],
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
  ['cpuUsageMetric', 'text'],
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
    // usePresetStyle 是可空字段：老配置里没有它，后端反序列化后为 null，
    // 语义是"没表过态，按老行为推导"。老行为是"有网格块就用网格"，
    // 所以这里要显示推导结果而不是 false（否则老用户一升级就看到开关是关的，
    // 悬浮窗却还是网格版式，两边对不上）。
    if (key === 'usePresetStyle') {
      const hasBlocks = Array.isArray(s.overlayBlocks) && s.overlayBlocks.length > 0;
      el.checked = v === null || v === undefined ? !hasBlocks : !!v;
      continue;
    }
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
  // 设置回推时不重绘网格（怕打断拖拽），但状态提示必须跟上
  refreshGridHint();

  // 主题选择器反映当前配色
  const themeSel = $('s-colorTheme');
  if (themeSel) themeSel.value = detectTheme(s);

  // 主界面主题
  applyUiTheme(s.uiTheme);
  const uiThemeSel = $('s-uiTheme');
  if (uiThemeSel) uiThemeSel.value = s.uiTheme === 'light' ? 'light' : 'dark';

  // 手动目标的进程名不在 FIELDS 里，由监控页顶部下拉框承载
  syncRangeLabels();

  // 准星页：控件回填 + 预览重绘
  fillCrosshair(s.crosshair);
  drawCrosshairPreview();
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

  // 屏幕准星（嵌套对象，随设置一起往返）
  out.crosshair = readCrosshair();

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

  refreshGridHint();
}

/**
 * 刷新网格编辑器下方那行状态提示。
 * 单独抽出来是因为它不只依赖网格内容，还依赖「使用预设样式」开关 ——
 * 开关一变就得刷新，而那时不需要重绘整个网格。
 */
function refreshGridHint() {
  const hint = $('grid-hint');
  if (!hint) return;

  const count = gridState.blocks.length;
  const base = count
    ? `已放置 ${count} 个块 · 网格 ${gridState.rows}×${gridState.cols}`
    : '网格为空时悬浮窗会使用「样式」下拉里的固定布局';

  // 开着预设样式时网格不生效，这点必须写在提示里，否则用户排了半天看不到变化
  const presetOn = $('s-usePresetStyle') ? $('s-usePresetStyle').checked : true;
  hint.textContent = presetOn && count
    ? `${base} · 当前未生效（已开启「使用预设样式」）`
    : base;
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
      if (btn.dataset.page === 'crosshair') {
        // 画布在隐藏页里量出来的尺寸是 0，显示后必须重画一次
        send({ type: 'getDisplays' });
        send({ type: 'getCrosshairImages' });
        drawCrosshairPreview();
      }
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
      // 开关一变，网格提示里的「未生效」字样要跟着变
      if (key === 'usePresetStyle') refreshGridHint();
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
  setupHotkeyRecorder($('cx-hotkey'), pushSettings);

  // ---------- 准星页 ----------
  // 准星改动同样走热更新：拖滑块防抖，点选类立即下发
  cxPush = () => pushSettings(false);
  const cxNow = () => pushSettings(true);
  cxPushNow = cxNow;

  for (const id of ['cx-size', 'cx-thickness', 'cx-gap', 'cx-dot', 'cx-opacity', 'cx-outline-thickness']) {
    const el = $(id);
    if (!el) continue;
    el.addEventListener('input', () => { syncCxLabels(); drawCrosshairPreview(); cxPush(); });
    el.addEventListener('change', () => { syncCxLabels(); drawCrosshairPreview(); cxNow(); });
  }

  for (const id of ['cx-offset-x', 'cx-offset-y']) {
    const el = $(id);
    if (!el) continue;
    el.addEventListener('input', () => drawCrosshairPreview());
    el.addEventListener('change', () => { drawCrosshairPreview(); cxNow(); });
  }

  const cxEnabledEl = $('cx-enabled');
  if (cxEnabledEl) {
    cxEnabledEl.addEventListener('change', () => { drawCrosshairPreview(); cxNow(); });
  }

  const cxColorEl = $('cx-color');
  if (cxColorEl) {
    cxColorEl.addEventListener('input', () => { renderCxColors(); drawCrosshairPreview(); cxPush(); });
    cxColorEl.addEventListener('change', () => { renderCxColors(); drawCrosshairPreview(); cxNow(); });
  }

  const cxOutlineEl = $('cx-outline-enabled');
  if (cxOutlineEl) {
    cxOutlineEl.addEventListener('change', () => { drawCrosshairPreview(); cxNow(); });
  }

  const cxOutlineColorEl = $('cx-outline-color');
  if (cxOutlineColorEl) {
    cxOutlineColorEl.addEventListener('input', () => { drawCrosshairPreview(); cxPush(); });
    cxOutlineColorEl.addEventListener('change', () => { drawCrosshairPreview(); cxNow(); });
  }

  const cxMonitorEl = $('cx-monitor');
  if (cxMonitorEl) cxMonitorEl.addEventListener('change', () => cxNow());

  const btnDisplays = $('btn-refresh-displays');
  if (btnDisplays) btnDisplays.addEventListener('click', () => send({ type: 'getDisplays' }));

  // 渲染方式：程序绘制 / 预设图片 / 自定义图片
  const cxModeBox = $('cx-render-mode');
  if (cxModeBox) {
    for (const btn of cxModeBox.querySelectorAll('.cx-seg')) {
      btn.addEventListener('click', async () => {
        const mode = btn.dataset.mode;
        if (mode === cxMode) return;

        if (mode === 'procedural') {
          cxMode = 'procedural';
          renderCxRenderMode();
          renderCxPresetImages();
          drawCrosshairPreview();
          cxNow();
          return;
        }

        // 图片绘制：手上已有图（预设或自选）就直接沿用，没有就默认第一张预设，
        // 免得"切过去了但画面上还是空参数"
        if (cxPresetFile) {
          await applyCxPresetImage(cxPresetFile);
        } else if (cxImagePath) {
          cxMode = 'image';
          renderCxRenderMode();
          renderCxPresetImages();
          refreshCxPreviewImage();
          drawCrosshairPreview();
          cxNow();
        } else {
          await applyCxPresetImage(CX_PRESET_IMAGES[0].file);
        }
      });
    }
  }

  const cxImageSize = $('cx-image-size');
  if (cxImageSize) {
    cxImageSize.addEventListener('input', () => { syncCxLabels(); drawCrosshairPreview(); cxPush(); });
    cxImageSize.addEventListener('change', () => { syncCxLabels(); drawCrosshairPreview(); cxNow(); });
  }

  // 1:1 原图：把尺寸滑块直接对准当前图片的原始像素
  const cxNativeBtn = $('btn-cx-image-native');
  if (cxNativeBtn) {
    cxNativeBtn.addEventListener('click', () => {
      if (!cxImageSize) return;
      if (cxMode !== 'image' || !cxPreviewImage) return;
      const w = cxPreviewImage.naturalWidth || cxPreviewImage.width;
      const h = cxPreviewImage.naturalHeight || cxPreviewImage.height;
      if (!w && !h) return;
      const native = Math.max(8, Math.min(1024, Math.round(Math.min(w, h))));
      cxImageSize.value = String(native);
      syncCxLabels();
      drawCrosshairPreview();
      cxNow();
    });
  }

  const cxImageFile = $('cx-image-file');
  if (cxImageFile) {
    cxImageFile.addEventListener('change', () => {
      const f = cxImageFile.files && cxImageFile.files[0];
      if (f) pickCxImage(f);
      cxImageFile.value = '';        // 允许连续选同一个文件
    });
  }

  const btnPickImage = $('btn-cx-pick-image');
  if (btnPickImage) {
    btnPickImage.addEventListener('click', () => {
      const el = $('cx-image-file');
      if (el) el.click();
    });
  }

  const cxHoldEnabledEl = $('cx-hold-enabled');
  if (cxHoldEnabledEl) cxHoldEnabledEl.addEventListener('change', () => cxNow());

  const cxHoldDelayEl = $('cx-hold-delay');
  if (cxHoldDelayEl) {
    cxHoldDelayEl.addEventListener('input', () => cxPush());
    cxHoldDelayEl.addEventListener('change', () => cxNow());
  }

  setupCxHoldRecorder();

  const btnSavePreset = $('btn-cx-save-preset');
  if (btnSavePreset) btnSavePreset.addEventListener('click', openCxPresetDialog);

  const btnPresetOk = $('btn-cx-preset-ok');
  if (btnPresetOk) btnPresetOk.addEventListener('click', commitCxPreset);

  const btnPresetCancel = $('btn-cx-preset-cancel');
  if (btnPresetCancel) btnPresetCancel.addEventListener('click', closeCxPresetDialog);

  const presetNameEl = $('cx-preset-name');
  if (presetNameEl) {
    presetNameEl.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); commitCxPreset(); }
      else if (e.key === 'Escape') { e.preventDefault(); closeCxPresetDialog(); }
    });
  }

  const presetDlg = $('cx-preset-dialog');
  if (presetDlg) {
    presetDlg.addEventListener('click', (e) => {
      if (e.target === presetDlg) closeCxPresetDialog();   // 点遮罩关闭
    });
  }

  const btnResetCx = $('btn-cx-reset-default');
  if (btnResetCx) {
    btnResetCx.addEventListener('click', () => {
      closeCxPresetDialog();
      fillCrosshair(cxDefaults());
      drawCrosshairPreview();
      cxNow();
    });
  }

  renderCxStyleButtons();
  renderCxColors();
  renderCxRenderMode();
  renderCxPresetImages();
  renderCxPresets();
  refreshCxPreviewImage();
  syncCxLabels();
  drawCrosshairPreview();

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
  if (dragBtn) {
    dragBtn.addEventListener('click', () => {
      dragMode = !dragMode;
      send({ type: 'setOverlayDragMode', enabled: dragMode });
      dragBtn.classList.toggle('dragging', dragMode);
      dragBtn.textContent = dragMode ? '完成调整' : '调整位置';
    });
  }

  window.addEventListener('resize', () => {
    const s = latest || {};
    drawChart(s.recentFrameTimes || [], s.low1PercentHistory || [], s.low01PercentHistory || []);
    drawFrameTimeChart(s.recentFrameTimes || []);
    drawCrosshairPreview();
  });

  // 支持用 #monitor / #history / #settings 直接打开对应页签
  const hash = (location.hash || '').replace('#', '');
  if (hash) {
    const target = document.querySelector(`.tab[data-page="${hash}"]`);
    if (target) target.click();
  }

  // 主进程在「关闭窗口 = 退出程序」策略下会发 request-quit，
  // 由前端经 WebSocket 通知 C# 后端一起退出 —— 后端才是常驻进程，
  // 它退了会把 Electron 整棵进程树收掉。WS 断着就直接让主进程退出。
  if (window.gamefps && window.gamefps.onQuitRequested) {
    window.gamefps.onQuitRequested(() => {
      const connected = ws && ws.readyState === WebSocket.OPEN;
      if (connected) {
        send({ type: 'quit' });
        // 后端收到 quit 会走正常退出流程并收掉 Electron；这里不再重复退出，
        // 万一它没响应，主进程自己的 2 秒兜底会接管。
      } else if (window.gamefps.forceQuit) {
        window.gamefps.forceQuit();
      }
    });
  }

  // 窗口被托盘重新唤起时补拉一次设置。
  // 界面是被 hide() 藏起来的而不是销毁，隐藏期间后端可能被托盘菜单改动过
  // （比如在托盘里取消开机自启），不重新拉一次就会看到过期状态。
  if (window.gamefps && window.gamefps.onShown) {
    window.gamefps.onShown(() => {
      if (ws && ws.readyState === WebSocket.OPEN) send({ type: 'hello' });
      else connect();
    });
  }

  connect();
}

document.addEventListener('DOMContentLoaded', init);
