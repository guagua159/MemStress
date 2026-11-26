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
    private void AppendLog(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss} {line}\n");
        LogBox.ScrollToEnd();
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        string dir = _log?.SessionDir ?? AppPaths.LogsRoot;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败"); }
    }

    private void UpdateLogDir() => LogDirText.Text = $"日志根目录：{AppPaths.LogsRoot}";

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        // 先在程序内显示：不依赖用户机器上有浏览器，也不需要跳出窗口。
        if (!ShowReportInApp()) return;
        WriteReportFiles();
        ReportTab.Focus();
    }

    /// <summary>
    /// 把报告写到会话目录。只写 report.md——下游按它解析，必须保留。
    /// 不再生成 report.html、也不再自动打开浏览器：报告已经在程序内可见，
    /// 而 html 的存在理由本来就是「给没有渲染器的环境一个可双击的版本」，
    /// 现在这个理由消失了（CHANGELOG 1.6）。
    /// </summary>
    private void WriteReportFiles()
    {
        var data = BuildReportData();
        if (data == null) return;
        string dir = _log?.SessionDir ?? AppPaths.LogsRoot;
        string mdPath = Path.Combine(dir, "report.md");
        File.WriteAllText(mdPath, ReportWriter.BuildMarkdown(data), new UTF8Encoding(false));
        AppendLog($"[报告] {mdPath}");
        StatusText.Text = $"报告已生成：{mdPath}";
    }

    /// <summary>取已固化的值，空则用兜底文案。避免把界面提示语当成数据写进报告。</summary>
    private static string Fallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private ReportWriter.ReportData? BuildReportData()    {
        if (_systemInfo == null)
        {
            MessageBox.Show(this, "机器信息尚未采集完成，请稍候再导出。", "MemStress");
            return null;
        }
        var snap = _engine?.Snapshot() ?? _lastSnapshot;
        return new ReportWriter.ReportData
        {
            SystemInfo = _systemInfo,
            Options = BuildOptions().Describe(),
            SessionDir = _log?.SessionDir ?? AppPaths.LogsRoot,
            Elapsed = snap?.Elapsed ?? _lastElapsed,
            TotalBytes = snap?.BytesTotal ?? 0,
            AllocationBytes = _engine?.TotalBytes ?? _lastAllocatedBytes,
            BytesDone = snap?.BytesDone ?? 0,
            ErrorCount = _errors.Count,
            // 引擎停止后 Arena 会被释放并置空，这里退回停止时固化的值。
            // 早先回退到 AllocText.Text，而那段文字本身已含「已释放（测试结束）」，
            // 报告里于是出现「已释放（已释放（测试结束））」这种嵌套废话。
            AllocationKind = _engine?.Arena?.Kind.ToString() ?? Fallback(_lastAllocationKind, "已释放"),
            AllocationNote = _engine?.Arena?.Note ?? Fallback(_lastAllocationNote, ""),
            Passes = snap?.Pass ?? 0,
            Errors = _errors.Select(r => r.Error).ToList(),
            Whea = _wheaEvents.ToList(),
            WheaQueryable = _whea?.Queryable ?? false,
            // 只有「跑满预定轮次/时长」才算完成。早先这里写成 !_running，于是手动点停止
            // 也会被算成已完成，报告顶部直接给出「通过」——把一次 5% 就中止的测试说成通过。
            Completed = _completedNaturally,
            CrossCheck = _crossCheck,
            StopReason = _stopReason
        };
    }
}
