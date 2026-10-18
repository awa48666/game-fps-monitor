// 渲染进程与主进程之间的安全桥。
// 注意：preload 运行在渲染进程里，process.argv 是渲染进程的参数，
// 拿不到主进程的 --ws-port，所以端口必须通过同步 IPC 向主进程索取。
const { contextBridge, ipcRenderer } = require('electron');

let wsPort = 8731;
try {
  const p = ipcRenderer.sendSync('get-ws-port');
  if (typeof p === 'number' && p > 0) wsPort = p;
} catch { /* 主进程未就绪时退回默认端口 */ }

contextBridge.exposeInMainWorld('gamefps', {
  /** 后端 WebSocket 端口，由 C# 宿主经主进程传入 */
  wsPort,
  /** 请求把主窗口带到前台 */
  focusWindow: () => ipcRenderer.send('focus-window'),
});
