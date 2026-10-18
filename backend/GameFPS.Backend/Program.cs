using System;
using System.Threading;
using System.Windows.Forms;

namespace GameFPS.Backend;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // 单实例：重复启动时提示并从托盘唤起
        using var singleInstance = new Mutex(true, @"Global\GameFPSMonitor.D0F2A9C1", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("游戏帧率监测已经在运行了，请查看系统托盘图标。",
                "GameFPS 游戏帧率监测", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var host = new AppHost();
        if (!host.Start())
        {
            MessageBox.Show(host.StartError ?? "启动失败。", "GameFPS 游戏帧率监测",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run();      // 泵消息：托盘、热键、悬浮窗渲染都依赖它
    }
}
