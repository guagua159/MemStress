using System.Text;
using System.Windows;
using MemStress.Core;

namespace MemStress;

/// <summary>
/// 报告页：测试结束后在这里直接显示完整结论，不依赖浏览器。
///
/// 为什么单独一页而不是塞进「运行日志」：报告是结构化内容（结论、参数表、错误明细表、WHEA 段），
/// 而日志框是滚动追加的——后续任何一条日志都会把报告冲走，用户想回看就得翻。
/// 这里的内容只在「导出报告」或测试收尾时整体替换，不被日志干扰。
/// </summary>
public partial class MainWindow
{
    /// <summary>缓存最近一次生成的报告正文，供复制按钮使用。</summary>
    private string _lastReportText = "";

    /// <summary>
    /// 生成报告并显示在程序内。返回是否成功（失败时已提示用户）。
    /// </summary>
    private bool ShowReportInApp()
    {
        var data = BuildReportData();
        if (data == null) return false;

        string md = ReportWriter.BuildMarkdown(data);
        _lastReportText = md;
        ReportBox.Text = md;

        // 报告正文是 Markdown 源文：程序内按纯文本显示，可读、可滚动、可全选复制。
        // 不做富文本渲染——那需要自己写一个 Markdown 渲染器，而这里的目的是「看结论」，
        // 表格用等宽字体对齐已经够用，且纯文本能直接粘到论坛/工单里。
        ReportBox.FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei");
        ReportTab.Visibility = Visibility.Visible;
        return true;
    }

    private void CopyReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReportText.Length == 0) return;
        try
        {
            Clipboard.SetText(_lastReportText);
            StatusText.Text = "报告正文已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void SaveReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ShowReportInApp()) return;
        WriteReportFiles();
    }
}
