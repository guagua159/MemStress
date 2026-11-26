using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MemStress.Core;

/// <summary>
/// 外部工具集成（三）：y-cruncher 专用启动路径。
/// 单独成文件是因为它和其它工具的运行方式差别很大——每次带时间戳日志、
/// 按启动前快照做进程树差集清理、从日志增量解析状态。通用路径见 ExternalTool.Run.cs。
/// </summary>
public sealed partial class ExternalTool
{
    /// <summary>
    /// 运行 y-cruncher 预设档位。与 <see cref="RunAsync"/> 的差别都在 y-cruncher 的特殊性上：
    /// 1) 每次用带时间戳的 logfile，避免同名追加导致读到上一次的 Iteration / Test Finished；
    /// 2) 停止时按「启动前快照 + 差集」杀进程树，因为父启动器死了子进程仍会占着内存；
    /// 3) 状态从本次日志增量解析，只读这一个文件。
    /// </summary>
    public async Task<RunResult> RunYcPresetAsync(string exePath, YcPreset preset, string sessionDir,
        Action<string> onLine, Action<string> onStatus, CancellationToken ct)
    {
        Directory.CreateDirectory(sessionDir);
        var result = new RunResult
        {
            StdOutPath = Path.Combine(sessionDir, "y-cruncher.stdout.log"),
            StdErrPath = Path.Combine(sessionDir, "y-cruncher.stderr.log")
        };

        string workDir = Path.GetDirectoryName(exePath) ?? sessionDir;
        string logFile = YcRunner.NewLogPath(workDir);
        string[] args = preset.BuildArgs(logFile);

        onLine($"[y-cruncher] 档位 {preset.Label}：{preset.Description}");
        onLine($"[y-cruncher] 命令行：y-cruncher.exe {string.Join(' ', args)}");
        onLine($"[y-cruncher] 本次日志：{Path.GetFileName(logFile)}（不会写入目录里的历史日志）");

        // 启动前快照：这些 PID 是别人的，绝不能杀
        var before = YcRunner.SnapshotWorkers();
        if (before.Count > 0) onLine($"[y-cruncher] 启动前已有 {before.Count} 个负载进程，本次不会动它们");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine("[stderr] " + e.Data); };

        if (!proc.Start()) throw new InvalidOperationException($"无法启动 {exePath}");
        TrackProcess(Id, proc, isYc: true, before);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var tail = new YcLogTail(logFile);
        bool userStopped = false;
        bool hardKilled = false;

        // 状态轮询：日志增量 + 负载进程存活检查
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 启动器进程被外部杀死（任务管理器/脚本 taskkill/崩溃）时有个 .NET 行为坑：
        // WaitForExitAsync 在「重定向 stdout + 进程被杀」的组合下可能不返回
        //（continuation 等 stdout 泵排空，而死进程的泵已断）——界面就永远「运行中」。
        // 所以 poller 里自己盯 proc.HasExited：启动器死了且日志没有 Test Finished，
        // 就取消等待、按被中止处理。实测（v1 的标志位方案）证明 flag 传不到已挂起的
        // await——必须用取消才能打断它。
        using var launcherDeathCts = new CancellationTokenSource();
        var poller = Task.Run(async () =>
        {
            while (!pollCts.IsCancellationRequested)
            {
                try
                {
                    var fresh = tail.Poll();
                    foreach (var line in fresh) onLine(line);
                    onStatus(tail.StatusLine());
                    if (tail.Finished) break;
                    if (proc.HasExited && !tail.Finished)
                    {
                        onLine("[y-cruncher] 启动器进程已终止（外部结束/崩溃），中止等待并清理…");
                        launcherDeathCts.Cancel();
                        break;
                    }
                }
                catch { }
                try { await Task.Delay(1000, pollCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, pollCts.Token);

        try
        {
            // -TL 到点后 y-cruncher 会打印 "Test Finished"，但它不一定立刻退出：
            // 实测见过停在 "Waiting for threads to terminate..." 再也不动的进程（工作集已降到 16 KB）。
            // 所以除了 preset 自己的时限，还要有一道兜底：超过总时限 + 宽限期仍不退出就强制收尾。
            // 没有这道兜底，界面会永远停在「交叉验证中」，用户只能杀进程。
            int graceSeconds = 120;
            using var hardCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(preset.TotalSeconds + graceSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct, hardCts.Token, launcherDeathCts.Token);
            try
            {
                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                result.ExitCode = proc.ExitCode;
            }
            catch (OperationCanceledException) when (hardCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // 超过「时限 + 宽限」还没退：多数时候日志已写完（Test Finished），按已完成收尾；
                // 但若日志也没收尾（卡在半程），ExitCode 0 会在下面被清掉——
                // 强杀是我们干的，不是 y-cruncher 的正常退出，不能冒充「通过」的证据。
                onLine($"[y-cruncher] 超过时限 {graceSeconds} 秒仍未退出，强制收尾");
                YcRunner.StopAll(proc, before);
                hardKilled = true;
                result.ExitCode = 0;
            }
        }
        catch (OperationCanceledException)
        {
            userStopped = true;
            onLine("[y-cruncher] 收到停止请求，正在清理进程树…");
            var residue = YcRunner.StopAll(proc, before);
            result.ExitCode = -1;
            if (residue.Count == 0)
                onLine("[y-cruncher] 已停止，父子进程均已退出，内存已释放");
            else
                onLine($"[y-cruncher] 残留进程 PID {string.Join(",", residue)}（已再次尝试强制终止）");
            result.Summary = residue.Count == 0
                ? "用户停止，进程树已清理干净"
                : $"用户停止，但有残留进程 PID {string.Join(",", residue)}";
        }

        try { pollCts.Cancel(); } catch { }
        try { await poller.ConfigureAwait(false); } catch { }

        // 正常退出路径也要做一次清理确认：y-cruncher 自己退出时子进程一般会跟着退，
        // 但 -TL 到点与异常退出行为不同，查一遍才算数。
        if (!userStopped)
        {
            var residue = YcRunner.StopAll(null, before);
            result.Summary = residue.Count == 0
                ? $"退出码 {result.ExitCode}，进程已全部退出"
                : $"退出码 {result.ExitCode}，残留进程 PID {string.Join(",", residue)}";
            if (residue.Count > 0) onLine($"[y-cruncher] 残留进程 PID {string.Join(",", residue)}");
        }

        // 把日志解析结果带回去，供「交叉验证」一节使用。
        // 不解析就让调用方自己去读日志文件，等于把同一件事做两遍。
        result.LogPath = logFile;
        result.Iterations = Math.Max(0, tail.Iteration);
        result.SawFailure = tail.SawFailure;
        result.SawPassed = tail.SawPassed;
        // 强杀兜底给的 0 不是 y-cruncher 自己的退出码，不能当「正常结束」证据。
        // 没看到 Test Finished 就清掉这个 0，让下游按「无证据」处理而不是「通过」。
        if (hardKilled && !tail.Finished) result.ExitCode = -1;

        UntrackProcess(Id);

        // 只归档本次这个日志，历史日志不动
        try
        {
            if (File.Exists(logFile))
            {
                string dest = Path.Combine(sessionDir, Path.GetFileName(logFile));
                File.Copy(logFile, dest, overwrite: true);
                result.Summary += $"；已归档 {Path.GetFileName(dest)}";
                onLine($"[y-cruncher] 已归档 {Path.GetFileName(logFile)}");
            }
            else
            {
                result.Summary += "；未生成日志文件";
                onLine("[y-cruncher] 警告：本次没有生成日志文件");
            }
        }
        catch (Exception ex) { onLine($"[y-cruncher] 归档失败：{ex.Message}"); }

        return result;
    }
}
