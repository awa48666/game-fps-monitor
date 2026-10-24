// GameFPS 前端主进程
// 由 C# 后端以 `electron "<app目录>" --ws-port=<端口>` 启动。
//
// 注意：electron.exe 是 GUI 子系统程序，stdout/stderr 不会附着到控制台，
// 因此这里把早期诊断信息落到 electron.log，方便排查启动失败。

const path = require('path');
const fs = require('fs');

const LOG = path.join(__dirname, 'electron.log');

function log(msg) {
  try {
    // 超过 256KB 就重开，避免日志无限增长
    if (fs.existsSync(LOG) && fs.statSync(LOG).size > 262144) fs.writeFileSync(LOG, '');
    fs.appendFileSync(LOG, `[${new Date().toISOString()}] ${msg}\n`);
  } catch { /* 日志写不了就算了 */ }
}

log('=== 启动 ===');
log('execPath=' + process.execPath);
log('versions.electron=' + (process.versions.electron || '(缺失! 说明不是以 Electron 方式运行)'));
log('ELECTRON_RUN_AS_NODE=' + (process.env.ELECTRON_RUN_AS_NODE || '(未设置)'));
log('argv=' + JSON.stringify(process.argv));

let electron;
try {
  electron = require('electron');
} catch (err) {
  log('require("electron") 抛异常: ' + (err && err.stack));
  process.exit(1);
}

log('require("electron") 类型=' + typeof electron
    + ' keys=' + (electron && typeof electron === 'object' ? Object.keys(electron).join(',') : electron));

const { app, BrowserWindow, ipcMain, shell } = electron || {};

process.on('uncaughtException', (err) => log('未捕获异常: ' + (err && err.stack)));

if (!app || !ipcMain || !BrowserWindow) {
  log('致命: electron 模块不完整，app/ipcMain/BrowserWindow 有缺失');
  process.exit(1);
}

const portArg = process.argv.find((a) => a.startsWith('--ws-port='));
const wsPort = portArg ? Number(portArg.split('=')[1]) : 8731;

// 可选：--page=history 让界面直接打开某个页签
const pageArg = process.argv.find((a) => a.startsWith('--page='));
const initialPage = pageArg ? pageArg.split('=')[1] : '';

let mainWindow = null;

// 点关闭按钮时的行为：'minimize' = 收进托盘继续后台跑，'quit' = 退出整个程序。
// 默认最小化 —— 这是个帧率监测工具，用户关界面通常只是想去玩游戏了，
// 直接退出会让采集和悬浮窗一起消失，那才是意外。
let closePolicy = 'minimize';

// 是否允许窗口真正关闭。退出流程里先把它置 true，再调 win.close()，
// 否则 close 事件里的 preventDefault 会把退出也拦下来。
let allowClose = false;

function createWindow() {
  log('createWindow: 创建窗口');
  mainWindow = new BrowserWindow({
    width: 1200,
    height: 820,
    minWidth: 940,
    minHeight: 620,
    backgroundColor: '#0b0f14',
    autoHideMenuBar: true,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });

  mainWindow.once('ready-to-show', () => {
    log('ready-to-show: 显示窗口');
    mainWindow.show();
  });

  // 关闭按钮：按策略决定是收进托盘还是真退出
  mainWindow.on('close', (e) => {
    if (allowClose) return;          // 退出流程已放行
    if (closePolicy === 'minimize') {
      e.preventDefault();
      log('close: 按策略最小化到托盘');
      mainWindow.hide();
      return;
    }
    // 'quit'：交给渲染进程经 WebSocket 通知 C# 后端一起退出
    e.preventDefault();
    log('close: 按策略退出整个程序');
    mainWindow.webContents.send('request-quit');
    // 兜底：WS 断开时渲染进程可能收不到，2 秒后强制退出
    setTimeout(() => {
      if (!allowClose) {
        log('close: 2 秒内未收到退出确认，强制退出');
        allowClose = true;
        app.quit();
      }
    }, 2000);
  });

  mainWindow.webContents.on('did-fail-load', (_e, code, desc, url) => {
    log(`did-fail-load code=${code} desc=${desc} url=${url}`);
  });

  mainWindow.webContents.on('render-process-gone', (_e, details) => {
    log('render-process-gone: ' + JSON.stringify(details));
  });

  const indexFile = path.join(__dirname, 'src', 'index.html');
  mainWindow.loadFile(indexFile, initialPage ? { hash: initialPage } : undefined)
    .then(() => log('loadFile 完成'))
    .catch((err) => log('loadFile 失败: ' + (err && err.stack)));

  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    shell.openExternal(url);
    return { action: 'deny' };
  });
}

ipcMain.on('focus-window', () => {
  log('收到 focus-window');
  if (!mainWindow) return;
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
  mainWindow.webContents.send('window-shown');
});

// 渲染进程收到设置后调这里，把「关闭行为」同步给主进程。
// 这样改完设置立刻生效，不需要重启界面。
ipcMain.on('set-close-policy', (_e, mode) => {
  closePolicy = mode === 'quit' ? 'quit' : 'minimize';
  log('closePolicy -> ' + closePolicy);
});

// 渲染进程确认可以退出了（它已经通知过后端，或 WS 本来就断着）
ipcMain.on('force-quit', () => {
  log('收到 force-quit');
  allowClose = true;
  app.quit();
});

// preload 在渲染进程里拿不到主进程的 --ws-port，用同步 IPC 提供
ipcMain.on('get-ws-port', (event) => {
  event.returnValue = wsPort;
});

app.whenReady().then(() => {
  log('app ready');
  createWindow();
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow();
});

app.on('window-all-closed', () => {
  log('window-all-closed');
  // 只有退出流程里才真的退（allowClose），否则窗口关了但后端还在跑，
  // 直接 app.quit() 会让托盘图标和采集一起消失，与"最小化"策略相悖。
  if (allowClose && process.platform !== 'darwin') app.quit();
});
