using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MemStress.Core;
using Microsoft.Win32;

namespace MemStress;

public partial class MainWindow
{
    /// <summary>
    /// 提权状态检查 + 「以管理员重启」。
    ///
    /// 为什么放在 GUI 而不是 manifest：大页需要提权令牌，但 CLI 自检必须永远能被
    /// 脚本以普通令牌启动并接住 stdout（manifest 写 highestAvailable 时
    /// CreateProcess 直接报 740 拒绝，见 app.manifest 里的选型记录）。
    /// 所以启动令牌 = asInvoker，提权变成 GUI 里一个可见、可选的动作。
    /// </summary>
    private void InitElevationUi()
    {
        bool elevated = SystemInfo.IsElevated();
        ElevationText.Text = elevated
            ? "管理员（大页可用）"
            : "非管理员（大页不可用）";
        ElevationText.Foreground = elevated
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x00))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E));

        RestartElevatedButton.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RestartElevatedButton_Click(object sender, RoutedEventArgs e)
    {
        // 压测运行中重启 = 旧实例退出时才释放内存，新实例又立刻申请同样多的内存，
        // 在大占用下几乎必然分配失败。置灰是运行时状态，这里再拦一道兜底。
        if (_running) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer");
            // ShellExecute 的 runas 动词会弹 UAC；用户取消时抛 OperationCanceledException
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "MemStress.exe",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            Process.Start(psi);
            Application.Current.Shutdown(); // 旧实例退出，避免两份同时占内存
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"重启失败：{ex.Message}\n\n也可以手动：右键 MemStress.exe → 以管理员身份运行。",
                "MemStress", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
