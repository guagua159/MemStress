using System.Windows;
using System.Windows.Controls;
using MemStress.Core;

namespace MemStress;

/// <summary>
/// y-cruncher 前端页。它原生没有 GUI，这一页把「手打命令行 + 翻日志」换成
/// 档位选择 + 实时进度 + 明确的通过/失败判定。
///
/// 与「交叉验证」阶段的关系：两者调的是同一套 <see cref="ExternalTool.RunYcPresetAsync"/>，
/// 只是入口不同——这里用户主动跑，那里是压测收尾后自动接着跑。
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource? _ycCts;
    private DateTime _ycStartedAt;
    private YcPreset _ycPreset = YcPreset.Quick;

    /// <summary>初始化这一页：填充档位、检测工具是否在位。</summary>
    private void InitYcTab()
    {
        YcPresetBox.Items.Clear();
        foreach (var p in YcPreset.All) YcPresetBox.Items.Add(p.Label);
        YcPresetBox.SelectedIndex = 0;
        RefreshYcPresence();
    }

    private ExternalTool? YcTool => ExternalTool.Catalog.FirstOrDefault(t => t.Id == "ycruncher");

    /// <summary>刷新「找到没找到」的状态与路径显示。</summary>
    private void RefreshYcPresence()
    {
        var tool = YcTool;
        string? exe = tool?.ResolvePath();
        if (exe == null)
        {
            YcStatusText.Text = "未找到";
            YcStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            YcPathText.Text = "把 y-cruncher 解压到 tools\\ycruncher\\，或点「指定路径…」手动选择。";
            YcRunButton.IsEnabled = false;
        }
        else
        {
            YcStatusText.Text = "已找到";
            YcStatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            YcPathText.Text = exe;
            YcRunButton.IsEnabled = true;
        }
        UpdateYcPresetDetail();
    }

    private void YcPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        int i = YcPresetBox.SelectedIndex;
        if (i >= 0 && i < YcPreset.All.Count) _ycPreset = YcPreset.All[i];
        UpdateYcPresetDetail();
    }

    /// <summary>把「会跑多久、用什么算法、吃多少内存」在选之前就摆出来。</summary>
    private void UpdateYcPresetDetail()
    {
        var p = _ycPreset;
        YcPresetDetail.Text =
            $"算法：{string.Join(" + ", p.Algorithms)}　|　每项 {p.PerTestSeconds} 秒　|　" +
            $"总时限 {p.TotalSeconds / 60} 分钟";

        // 内存量按物理总量 × 档位百分比，与 YcPreset.BuildArgs 生成 -M 的口径一致
        double totalGiB = Native.TotalPhysicalBytes() / 1073741824.0;
        YcMemoryText.Text = totalGiB > 0
            ? $"{totalGiB * p.MemoryPercent:F1} GiB（{p.MemoryPercent * 100:F0}% × {totalGiB:F0} GiB）"
            : "未知";
    }

    private async void YcRun_Click(object sender, RoutedEventArgs e)
    {
        var tool = YcTool;
        string? exe = tool?.ResolvePath();
        if (exe == null)
        {
            MessageBox.Show(this, "未找到 y-cruncher.exe。", "y-cruncher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        YcRunButton.IsEnabled = false;
        YcStopButton.IsEnabled = true;
        YcLogBox.Clear();
        YcVerdictText.Text = "运行中…";
        YcVerdictText.Foreground = System.Windows.Media.Brushes.DarkSlateGray;
        YcVerdictDetail.Text = "";
        _ycStartedAt = DateTime.Now;
        _ycCts = new CancellationTokenSource();

        var runner = new ExternalTool
        {
            Id = "ycruncher", DisplayName = "y-cruncher",
            DownloadUrl = tool!.DownloadUrl, Role = tool.Role
        };

        try
        {
            var result = await runner.RunYcPresetAsync(exe, _ycPreset, _log?.SessionDir ?? AppPaths.LogsRoot,
                line => Dispatcher.Invoke(() =>
                {
                    YcLogBox.AppendText(line + "\n");
                    YcLogBox.ScrollToEnd();
                }),
                status => Dispatcher.Invoke(() => YcIterText.Text = status),
                _ycCts.Token);

            ShowYcVerdict(result);
        }
        catch (Exception ex)
        {
            YcVerdictText.Text = "不通过（运行异常）";
            YcVerdictText.Foreground = System.Windows.Media.Brushes.Firebrick;
            YcVerdictDetail.Text = ex.Message;
        }
        finally
        {
            YcRunButton.IsEnabled = true;
            YcStopButton.IsEnabled = false;
            _ycCts?.Dispose();
            _ycCts = null;
        }
    }

    /// <summary>
    /// 判定必须由程序给出，不能让用户从滚动日志里自己找 "Passed"。
    /// 三种结局分开写：正常通过 / 出现失败 / 被用户中止（中止不算通过）。
    /// </summary>
    private void ShowYcVerdict(ExternalTool.RunResult result)
    {
        double minutes = (DateTime.Now - _ycStartedAt).TotalMinutes;
        YcProgress.Value = result.TimedOut ? YcProgress.Value : 100;

        if (result.ExitCode < 0)
        {
            YcVerdictText.Text = "被中止";
            YcVerdictText.Foreground = System.Windows.Media.Brushes.DarkOrange;
            // 外部终止/用户停止的细节在 Summary 里，展示出来用户才知道发生了什么
            YcVerdictDetail.Text = string.IsNullOrEmpty(result.Summary)
                ? "测试没有跑完，不能算通过。已清理进程树。"
                : $"测试没有跑完，不能算通过。{result.Summary}";
        }
        else if (result.SawFailure)
        {
            YcVerdictText.Text = "不通过";
            YcVerdictText.Foreground = System.Windows.Media.Brushes.Firebrick;
            YcVerdictDetail.Text = $"日志里出现 Failed。共 {result.Iterations} 轮迭代，耗时 {minutes:F1} 分钟。"
                                 + (result.LogPath.Length > 0 ? $"\n日志：{result.LogPath}" : "");
        }
        else if (!result.SawPassed)
        {
            // 无失败也无一轮 Passed：日志没给出任何证据（格式变了/空日志/强杀），
            // 不能宣布通过——宁可让用户去看日志，也不能假绿。
            YcVerdictText.Text = "无法判定";
            YcVerdictText.Foreground = System.Windows.Media.Brushes.DarkOrange;
            YcVerdictDetail.Text = "日志里没有可识别的测试结果（无 Passed 也无 Failed）。"
                                 + "可能是 y-cruncher 版本改了输出格式，或进程被提前终止。"
                                 + (result.LogPath.Length > 0 ? $"\n日志：{result.LogPath}" : "");
        }
        else
        {
            YcVerdictText.Text = "通过";
            YcVerdictText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            YcVerdictDetail.Text = $"{result.Iterations} 轮迭代全部 Passed，耗时 {minutes:F1} 分钟。"
                                 + (result.LogPath.Length > 0 ? $"\n日志：{result.LogPath}" : "");
        }
    }

    private void YcStop_Click(object sender, RoutedEventArgs e)
    {
        try { _ycCts?.Cancel(); } catch { }
        YcVerdictText.Text = "正在停止…";
    }
}
