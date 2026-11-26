using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MemStress.Core;

namespace MemStress;

public partial class MainWindow
{
    private void Shutdown()
    {
        _timer.Stop();
        _whea?.Stop();
        try { _engine?.Stop(); } catch { }
        try { _engine?.Dispose(); } catch { }
        // 外部工具是独立进程：主程序退出不会带走它，会变成孤儿继续吃内存，
        // 而且归档逻辑再也不会执行。逐个发取消信号，再统一兜底杀进程树。
        try { _ycCts?.Cancel(); } catch { }
        try { _crossCheckCts.Cancel(); } catch { }
        try { ExternalTool.KillRunning(); } catch { }
        try { _log?.Dispose(); } catch { }
        try { _whea?.Dispose(); } catch { }
    }
}
