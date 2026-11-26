using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MemStress.Core;

/// <summary>
/// 外部工具集成（二）：拉起子进程、实时回传输出、归档工具自有日志、按 id 停止。
/// 进程登记只有一张表（见下），界面靠它判断按钮状态，退出时靠它清孤儿进程。
/// y-cruncher 的特殊路径见 ExternalTool.Yc.cs。
/// </summary>
public sealed partial class ExternalTool
{
    /// <summary>
    /// 按工具 id 登记的运行句柄，一张表同时承担两件事：
    /// 一是界面判断「这个工具在不在跑」，二是主程序退出时把所有子进程带走。
    /// 早期这里是两套登记（HashSet&lt;Process&gt; + 按 id 的字典），各自增删容易漏掉一边，
    /// 漏掉的那一边就会变成孤儿进程，现已合并为一套。
    /// </summary>
    private sealed class ToolHandle
    {
        public Process? Proc;
        public HashSet<int> Before = new();
        public bool IsYc;
    }

    private static readonly Dictionary<string, ToolHandle> _handles = new();

    private static void TrackProcess(string id, Process proc, bool isYc, HashSet<int>? before = null)
    {
        lock (_handles)
        {
            _handles[id] = new ToolHandle { Proc = proc, IsYc = isYc, Before = before ?? new HashSet<int>() };
        }
    }

    private static void UntrackProcess(string id)
    {
        lock (_handles) _handles.Remove(id);
    }

    /// <summary>该工具当前是否在跑（供界面决定按钮显示「运行」还是「停止」）。</summary>
    public static bool IsToolRunning(string id)
    {
        lock (_handles)
        {
            if (!_handles.TryGetValue(id, out var h) || h.Proc == null) return false;
            try { return !h.Proc.HasExited; } catch { return false; }
        }
    }

    /// <summary>
    /// 停掉指定工具。y-cruncher 走差集清理（父启动器死了子进程还占着内存），
    /// 其它工具直接杀进程树。返回是否确实找到并处理了进程。
    /// </summary>
    public static bool StopTool(string id)
    {
        ToolHandle? h;
        lock (_handles) { if (!_handles.TryGetValue(id, out h)) return false; }
        if (h.IsYc)
        {
            // 必须用启动时记录的快照：空集合会把用户在程序外自己起的 y-cruncher 一并杀掉
            YcRunner.StopAll(h.Proc, h.Before);
            return true;
        }
        try
        {
            if (h.Proc is { HasExited: false }) { h.Proc.Kill(entireProcessTree: true); return true; }
        }
        catch { }
        return false;
    }

    /// <summary>终止所有仍在运行的外部工具（含其子进程树）。主程序退出时调用。</summary>
    /// <summary>
    /// 尽力终止整棵进程树。失败一律忽略——调用点都在清理路径上，
    /// 此时进程可能已自行退出、句柄可能已失效，抛出来只会掩盖真正的错误。
    /// </summary>
    private static void KillQuietly(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
    }

    public static void KillRunning()
    {
        lock (_handles)
        {
            foreach (var h in _handles.Values)
            {
                KillQuietly(h.Proc);
                try { h.Proc?.Dispose(); } catch { }
            }
            _handles.Clear();
        }
    }

    public sealed class RunResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string StdOutPath { get; set; } = "";
        public string StdErrPath { get; set; } = "";
        public string Summary { get; set; } = "";

        /// <summary>本次运行的工具自有日志路径（y-cruncher 的 logfile）。没有则为空。</summary>
        public string LogPath { get; set; } = "";

        /// <summary>从工具日志里解析出的迭代次数。0 表示没解析到。</summary>
        public int Iterations { get; set; }

        /// <summary>日志里见过至少一行 Passed。判定「通过」的必要证据（见 YcLogTail.SawPassed）。</summary>
        public bool SawPassed { get; set; }

        /// <summary>工具日志里出现过 Failed。仅 y-cruncher 这类可解析日志的工具会置位。</summary>
        public bool SawFailure { get; set; }
    }

    /// <summary>拉起工具并把输出落到同一会话目录，输出同时实时回传给界面。</summary>
    /// <summary>
    /// 等子进程退出，超时或取消时杀掉它并标记结果。
    /// <paramref name="flushPendingOutput"/> 为 true 时再 WaitForExit 一次，
    /// 确保异步读事件全部投递完（重定向输出的路径需要，否则末尾几行会丢）。
    /// </summary>
    private static async Task WaitOrKillAsync(Process proc, int timeoutSeconds, CancellationToken ct,
        RunResult result, bool flushPendingOutput)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            if (flushPendingOutput) proc.WaitForExit();
            result.ExitCode = proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            result.TimedOut = !ct.IsCancellationRequested;
            KillQuietly(proc);
            result.ExitCode = -1;
        }
    }

    public async Task<RunResult> RunAsync(string exePath, IEnumerable<string> args, string sessionDir,
        Action<string> onLine, int timeoutSeconds, CancellationToken ct)
    {
        Directory.CreateDirectory(sessionDir);
        string tag = Path.GetFileNameWithoutExtension(exePath);
        string outPath = Path.Combine(sessionDir, $"{tag}.stdout.log");
        string errPath = Path.Combine(sessionDir, $"{tag}.stderr.log");
        var result = new RunResult { StdOutPath = outPath, StdErrPath = errPath };

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? sessionDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var outWriter = new StreamWriter(outPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        using var errWriter = new StreamWriter(errPath, false, new UTF8Encoding(false)) { AutoFlush = true };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var errLines = new List<string>();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (outWriter) outWriter.WriteLine(e.Data);
            onLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (errWriter) errWriter.WriteLine(e.Data);
            lock (errLines) errLines.Add(e.Data);
            onLine("[stderr] " + e.Data);
        };

        if (!proc.Start()) throw new InvalidOperationException($"无法启动 {exePath}");
        TrackProcess(Id, proc, isYc: false);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await WaitOrKillAsync(proc, timeoutSeconds, ct, result, flushPendingOutput: true).ConfigureAwait(false);

        var errText = string.Join(" | ", errLines.Take(3));
        result.Summary = result.TimedOut
            ? $"超时 {timeoutSeconds}s 后终止"
            : $"退出码 {result.ExitCode}" + (errText.Length > 0 ? $"，stderr: {errText}" : "");
        UntrackProcess(Id);

        // 归档工具自己写的日志（y-cruncher 的 stress 模式只认 logfile: 启动选项，标准输出重定向拿不到内容）
        string workDir = Path.GetDirectoryName(exePath) ?? sessionDir;
        foreach (var glob in ArtifactGlobs)
        {
            try
            {
                foreach (var f in Directory.GetFiles(workDir, glob))
                {
                    string dest = Path.Combine(sessionDir, $"{tag}.{Path.GetFileName(f)}");
                    File.Copy(f, dest, overwrite: true);
                    result.Summary += $"；工具日志已归档 {Path.GetFileName(dest)}";
                }
            }
            catch { /* 归档失败不影响结论，日志里已有 stdout/stderr */ }
        }
        return result;
    }
}
