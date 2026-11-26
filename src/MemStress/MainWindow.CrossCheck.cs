using System.Windows;
using MemStress.Core;

namespace MemStress;

/// <summary>
/// 交叉验证阶段：自研压测自然跑完之后，可选地接着跑一次 y-cruncher。
///
/// 为什么是可选而不是必选：y-cruncher 三档分别要 15 分钟 / 1 小时 / 4 小时，
/// 把它塞进默认路径等于让每次快筛都多花一小时。所以默认关闭，用户显式勾选才跑。
///
/// 为什么单独一个分片：这段逻辑跨「界面状态 + 外部工具 + 报告三态」，
/// 混进压测启停分片会把那条已经很长的收尾路径撑爆。
/// </summary>
public partial class MainWindow
{
    private CrossCheckResult _crossCheck = new();

    /// <summary>交叉验证的取消源。与压测引擎分开：用户在 yc 阶段点停止，不该影响已结束的压测统计。</summary>
    private CancellationTokenSource _crossCheckCts = new();

    /// <summary>
    /// 初始化交叉验证开关：y-cruncher 不在位时置灰并说明原因，
    /// 不让用户点下去才看到报错。
    /// </summary>
    private void InitCrossCheckUi()
    {
        var tool = ExternalTool.Catalog.FirstOrDefault(t => t.Id == "ycruncher");
        string? exe = tool?.ResolvePath();
        if (exe == null)
        {
            CrossCheckBox.IsEnabled = false;
            CrossCheckBox.IsChecked = false;
            CrossCheckPresetBox.IsEnabled = false;
            CrossCheckHint.Text = "未找到 y-cruncher.exe：把它放到 tools\\ycruncher\\ 后重启本程序即可启用。";
            return;
        }

        // 档位下拉：把「选之前就知道要跑多久」写进条目文字，而不是让用户点开才知道
        foreach (var p in YcPreset.All)
            CrossCheckPresetBox.Items.Add($"{p.Label}（约 {p.TotalSeconds / 60} 分钟）");
        CrossCheckPresetBox.SelectedIndex = 0;   // 默认最快的档，避免误选 4 小时
    }

    /// <summary>读取用户选的交叉验证档位。</summary>
    private YcPreset SelectedCrossCheckPreset()
    {
        int i = CrossCheckPresetBox.SelectedIndex;
        return i >= 0 && i < YcPreset.All.Count ? YcPreset.All[i] : YcPreset.Quick;
    }

    /// <summary>
    /// 等自研压测的内存真正释放完，再启动交叉验证。
    ///
    /// 为什么必须等：两者都申请大页。压测占着 20 GiB 大页时 y-cruncher 去申请，
    /// 会直接拿到 Win32 1450「系统资源不足」，报告里写「分配失败」——
    /// 用户会以为内存有毛病，其实是程序没把自己的内存放掉。
    /// </summary>
    private async Task RunCrossCheckAfterReleaseAsync(TestOptions opt, Task released, CancellationToken ct)
    {
        AppendLog("[交叉验证] 等待压测内存释放…");
        try { await released.ConfigureAwait(true); }
        catch { /* 释放失败也要继续，下面会因为申请不到内存而如实报错 */ }
        await RunCrossCheckAsync(opt, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 在自研压测收尾后调用。只有用户勾选时才真正启动外部工具。
    /// 返回的 <see cref="CrossCheckResult"/> 会被写进报告的三态之一。
    /// </summary>
    private async Task RunCrossCheckAsync(TestOptions opt, CancellationToken ct)
    {
        if (!opt.CrossCheck)
        {
            _crossCheck = new CrossCheckResult { State = CrossCheckState.NotRequested };
            return;
        }

        var tool = ExternalTool.Catalog.FirstOrDefault(t => t.Id == "ycruncher");
        string? exe = tool?.ResolvePath();
        if (exe == null)
        {
            // 界面上已置灰，但用户可能在运行期间删掉了文件——仍然要如实记成「没测」
            _crossCheck = new CrossCheckResult { State = CrossCheckState.ToolNotFound };
            AppendLog("[交叉验证] 未找到 y-cruncher.exe，跳过。本次结论仅基于自研算法。");
            return;
        }

        var preset = SelectedCrossCheckPreset();
        AppendLog($"[交叉验证] 启动 y-cruncher（{preset.Label}），预计 {preset.TotalSeconds / 60} 分钟…");
        StatusText.Text = $"交叉验证中：y-cruncher {preset.Label}…";

        // exe 非空即说明 tool 非空（exe 来自 tool?.ResolvePath()），这里用 ! 明确表态
        var runner = new ExternalTool
        {
            Id = "ycruncher", DisplayName = "y-cruncher",
            DownloadUrl = tool!.DownloadUrl, Role = tool.Role
        };

        // 立刻标记「进行中」：用户可能在交叉验证跑到一半时导出报告，
        // 那一刻若仍是默认的 NotRequested，报告会写成「未启用」——把「正在测」说成「没测」。
        _crossCheck = new CrossCheckResult
        {
            State = CrossCheckState.Running,
            PresetLabel = preset.Label
        };

        // 交叉验证期间同样禁用「以管理员重启」：此刻 _running 已是 false（压测收尾了），
        // 但 y-cruncher 正占着大页内存——这时重启，旧实例退出会先杀掉 yc 进程树，
        // 新实例又立刻申请同样大的大页分配，几乎必然失败。
        RestartElevatedButton.IsEnabled = false;

        // 重新启用停止按钮：StopInternal 在压测收尾时把它置灰了，但交叉验证可能
        // 跑十几分钟（深度档更久），期间用户没有任何办法中止 y-cruncher，只能关窗口。
        // 关窗口会走 Shutdown 的清理路径，虽然也能收掉进程，但那是「被迫退出」不是
        // 「停止测试」——报告里会缺一次正常的收尾记录。
        StopButton.IsEnabled = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int iterations = 0;
        string logPath = "";
        try
        {
            var result = await runner.RunYcPresetAsync(exe, preset, _log?.SessionDir ?? AppPaths.LogsRoot,
                line => Dispatcher.Invoke(() => AppendLog($"[yc] {line}")),
                status => Dispatcher.Invoke(() => StatusText.Text = $"交叉验证中：{status}"),
                ct);

            sw.Stop();
            logPath = result.LogPath ?? "";
            iterations = result.Iterations;
            _crossCheck = new CrossCheckResult
            {
                State = CrossCheckState.Completed,
                PresetLabel = preset.Label,
                Iterations = iterations,
                // 通过 = 正常退出 + 无失败 + 日志里有 Passed 证据。三者缺一不可：
                // 强杀兜底会给 0 退出码，没有日志证据就不能宣布通过。
                Passed = !result.TimedOut && !result.SawFailure && result.ExitCode == 0 && result.SawPassed,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                LogPath = logPath
            };
            AppendLog($"[交叉验证] 完成：{_crossCheck.Describe()}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            // 跑了但出错 ≠ 没跑。记成已完成但判定不通过，不要退回「未启用」。
            _crossCheck = new CrossCheckResult
            {
                State = CrossCheckState.Completed,
                PresetLabel = preset.Label,
                Iterations = iterations,
                Passed = false,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                LogPath = logPath
            };
            AppendLog($"[交叉验证] 异常：{ex.Message}（按不通过计）");
        }
        finally
        {
            // 只有非提权才显示过这个按钮；提权状态下它本来就是 Collapsed，
            // 恢复 IsEnabled 无副作用（可见性由 InitElevationUi 管）。
            if (!SystemInfo.IsElevated()) RestartElevatedButton.IsEnabled = true;
            // 交叉验证结束后停止按钮回到禁用态：压测已经停了，没有东西可停。
            StopButton.IsEnabled = false;
            StatusText.Text = $"已停止（{_stopReason}）";
        }
    }
}
