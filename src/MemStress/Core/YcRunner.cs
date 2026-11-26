using System.Diagnostics;
using System.Text;

namespace MemStress.Core;


/// <summary>
/// y-cruncher 运行期控制：时间戳日志、进程树清理、日志增量解析。
/// 单独立类是因为 y-cruncher 有三个别的工具没有的麻烦：
/// 1) exe 只是启动器，负载在 Binaries\&lt;型号&gt; ~ &lt;代号&gt;.exe 子进程里，杀父进程不带走子进程；
/// 2) 日志只能靠 logfile: 启动选项拿到，同名文件会追加，必须每次换名；
/// 3) 状态要从日志增量解析，不能读目录里别的历史日志。
/// </summary>
public static class YcRunner
{
    /// <summary>本次运行的日志文件路径，tools\ycruncher\ycruncher-stress-yyyyMMdd-HHmmss.log。</summary>
    public static string NewLogPath(string toolDir)
    {
        Directory.CreateDirectory(toolDir);
        for (int i = 0; i < 100; i++)
        {
            string name = $"ycruncher-stress-{DateTime.Now:yyyyMMdd-HHmmss}" + (i == 0 ? "" : $"-{i}") + ".log";
            string path = Path.Combine(toolDir, name);
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(toolDir, $"ycruncher-stress-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
    }

    /// <summary>启动前记录已在运行的 y-cruncher 负载进程，用于启动后求差集。</summary>
    public static HashSet<int> SnapshotWorkers()
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try { if (IsYcWorker(p)) set.Add(p.Id); } catch { }
            finally { p.Dispose(); }
        }
        return set;
    }

    /// <summary>
    /// 判断是不是 y-cruncher 的负载子进程。特征是 Binaries 目录下的 "型号 ~ 代号.exe"，
    /// 文件名里带 " ~ "，且父进程或路径与本机 y-cruncher 相关。
    /// </summary>
    private static bool IsYcWorker(Process p)
    {
        string? path = null;
        try { path = p.MainModule?.FileName; } catch { return false; }
        if (string.IsNullOrEmpty(path)) return false;
        if (!path.Contains(@"\Binaries\", StringComparison.OrdinalIgnoreCase)) return false;
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Contains(" ~ ", StringComparison.Ordinal) || name.Contains("y-cruncher", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 停掉本次启动产生的全部 y-cruncher 进程：先杀负载子进程（新出现的），再杀父进程。
    /// 返回残留的 PID 列表，空表示清理干净。
    /// </summary>
    public static List<int> StopAll(Process? parent, HashSet<int> before)
    {
        var killed = new List<int>();
        // 1) 先杀新增的负载子进程。父进程还活着时杀子进程，父进程不会重新拉起它们。
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (before.Contains(p.Id)) continue;
                if (!IsYcWorker(p)) continue;
                p.Kill(entireProcessTree: true);
                killed.Add(p.Id);
            }
            catch { }
            finally { p.Dispose(); }
        }

        // 2) 再杀父启动器
        try
        {
            if (parent is { HasExited: false })
            {
                parent.Kill(entireProcessTree: true);
                killed.Add(parent.Id);
            }
        }
        catch { }

        // 3) 等它们真正消失，再把残留查出来
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var left = new List<int>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (before.Contains(p.Id)) continue;
                    if (!IsYcWorker(p)) continue;
                    if (parent != null && p.Id == parent.Id) left.Add(p.Id);
                    else left.Add(p.Id);
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (left.Count == 0) return new List<int>();
            Thread.Sleep(300);
        }

        // 4) 兜底：再硬杀一轮，然后如实报告
        var residue = new List<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (before.Contains(p.Id)) continue;
                if (!IsYcWorker(p)) continue;
                try { p.Kill(entireProcessTree: true); } catch { }
                residue.Add(p.Id);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return residue;
    }

    /// <summary>当前仍在运行的、本次新增的 y-cruncher 负载进程数。</summary>
    public static int CountWorkers(HashSet<int> before)
    {
        int n = 0;
        foreach (var p in Process.GetProcesses())
        {
            try { if (!before.Contains(p.Id) && IsYcWorker(p)) n++; }
            catch { }
            finally { p.Dispose(); }
        }
        return n;
    }
}
