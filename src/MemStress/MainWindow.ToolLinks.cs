using System.Diagnostics;
using System.Windows;
using MemStress.Core;

namespace MemStress;

/// <summary>
/// y-cruncher 页上的三个辅助按钮：打开官网、打开工具目录、手动指定路径。
///
/// 为什么单独放：这些是「把用户引到正确位置」的导航动作，与压测/报告无关。
/// 混进 y-cruncher 主逻辑分片会让那份文件同时管「跑测试」和「开文件夹」两件事。
/// </summary>
public partial class MainWindow
{
    private void OpenToolSite_Click(object sender, RoutedEventArgs e)
    {
        string url = (sender as FrameworkElement)?.Tag as string ?? "";
        if (url.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败"); }
    }

    private void OpenToolDir_Click(object sender, RoutedEventArgs e)
    {
        string dir = (sender as FrameworkElement)?.Tag as string ?? "";
        try
        {
            string full = Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
            Directory.CreateDirectory(full);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败"); }
    }

    /// <summary>
    /// 让用户手动指定 exe。选过会记住（写进 tools\paths.json），
    /// 重启后不必再选一次——否则每次开机都要重来一遍。
    /// </summary>
    private void BrowseTool_Click(object sender, RoutedEventArgs e)
    {
        string toolId = (sender as FrameworkElement)?.Tag as string ?? "";
        if (toolId.Length == 0) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        ExternalTool.SaveManualPath(toolId, dlg.FileName);
        RefreshYcPresence();
        InitCrossCheckUi();
    }
}
