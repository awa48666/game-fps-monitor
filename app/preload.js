// 渲染进程与主进程之间的安全桥。
// 注意：preload 运行在渲染进程里，process.argv 是渲染进程的参数，
// 拿不到主进程的 --ws-port，所以端口必须通过同步 IPC 向主进程索取。
const { contextBridge, ipcRenderer } = require('electron');
const fs = require('fs');
const path = require('path');

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

  /**
   * 把 app/src 下的资源读成 dataURL。
   * 页面是 file:// 加载的，渲染进程里 fetch('file://...') 会被 Chromium 直接拒掉，
   * 而图片准星又必须拿到真实字节交给后端，所以这里用 preload 的 Node 能力读盘。
   * 只接受文件名（basename），拼不出目录穿越的路径。
   */
  readAssetDataUrl: (relative) => {
    try {
      const rel = String(relative || '').replace(/\\/g, '/').replace(/^\/+/, '');
      if (!rel || rel.includes('..')) return null;

      const root = path.join(__dirname, 'src');
      const full = path.resolve(root, rel);
      // 解析后必须仍在 src 里，挡住 ../ 之类的越界读
      if (full !== root && !full.startsWith(root + path.sep)) return null;

      const buf = fs.readFileSync(full);
      const ext = path.extname(full).toLowerCase();
      const mime = ext === '.png' ? 'image/png'
        : (ext === '.jpg' || ext === '.jpeg') ? 'image/jpeg'
          : ext === '.gif' ? 'image/gif'
            : ext === '.bmp' ? 'image/bmp'
              : 'application/octet-stream';
      return `data:${mime};base64,${buf.toString('base64')}`;
    } catch {
      return null;
    }
  },
});
