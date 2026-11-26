using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MemStress.Core;

namespace MemStress;

/// <summary>
/// CLI 自检（二）：外部工具相关。--toolselftest / --wheaselftest / --toolscan 都在这里，
/// 避免主文件被这些一次性诊断代码占满。总入口与自研压测自检见 SelfTest.cs。
/// </summary>
public static partial class SelfTest
{
    private static int RunToolSelfTest()
    {
        Console.WriteLine("[toolselftest] 验证外部工具编排链路");

        using var log = new LogWriter(AppPaths.LogsRoot, "toolselftest");
        var tool = ExternalTool.Catalog.First(t => t.Id == "ycruncher");

        // 用一个确定存在、参数行为可预期的系统程序代替真实压测工具。
        // 注意必须把它复制到可写的临时目录再跑：RunAsync 会把工作目录设为可执行文件所在目录，
        // 而工具自己写的日志就落在那里——直接用 System32 里的 exe 会把测试残留写进系统目录。
        string exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Console.WriteLine($"[toolselftest] 被测可执行文件: {exe}");
        if (!File.Exists(exe)) { Console.WriteLine("[toolselftest] FAIL: 找不到 cmd.exe"); return 2; }

        string sandbox = Path.Combine(Path.GetTempPath(), "memstress-toolselftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);
        string sandboxExe = Path.Combine(sandbox, "cmd.exe");
        File.Copy(exe, sandboxExe, overwrite: true);
        Console.WriteLine($"[toolselftest] 隔离工作目录: {sandbox}");

        var lines = new List<string>();
        var runner = new ExternalTool
        {
            Id = tool.Id, DisplayName = tool.DisplayName, Role = tool.Role,
            DownloadUrl = tool.DownloadUrl,
            // 故意写一个文件，验证「工具自有日志归档」这条分支真的会把文件搬回来
            ArtifactGlobs = new[] { "toolselftest-artifact.txt" }
        };

        // cmd /c "echo ... > artifact && echo out && echo err 1>&2"
        var argv = new[]
        {
            "/c",
            "echo artifact-body>toolselftest-artifact.txt && echo hello-stdout && echo hello-stderr 1>&2 && exit 7"
        };

        ExternalTool.RunResult result;
        try
        {
            result = runner.RunAsync(sandboxExe, argv, log.SessionDir, l => lines.Add(l), 30, CancellationToken.None)
                            .GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }

        Console.WriteLine($"[toolselftest] 退出码: {result.ExitCode}（期望 7）");
        Console.WriteLine($"[toolselftest] 摘要: {result.Summary}");
        Console.WriteLine($"[toolselftest] stdout 行数: {lines.Count}");

        bool ok = true;
        if (result.ExitCode != 7) { Console.WriteLine("[toolselftest] FAIL: 退出码未正确捕获"); ok = false; }
        if (!File.Exists(result.StdOutPath)) { Console.WriteLine("[toolselftest] FAIL: stdout 日志未生成"); ok = false; }
        else if (!File.ReadAllText(result.StdOutPath).Contains("hello-stdout")) { Console.WriteLine("[toolselftest] FAIL: stdout 内容未落盘"); ok = false; }
        if (!File.Exists(result.StdErrPath)) { Console.WriteLine("[toolselftest] FAIL: stderr 日志未生成"); ok = false; }
        else if (!File.ReadAllText(result.StdErrPath).Contains("hello-stderr")) { Console.WriteLine("[toolselftest] FAIL: stderr 内容未落盘"); ok = false; }

        // 归档分支：cmd 的工作目录是 System32，工具日志写在那里；归档后应出现在会话目录
        string archived = Path.Combine(log.SessionDir, "cmd.toolselftest-artifact.txt");
        if (!File.Exists(archived)) { Console.WriteLine($"[toolselftest] WARN: 工具自有日志未归档到 {archived}（可能无写权限）"); }
        else { Console.WriteLine("[toolselftest] 工具自有日志已归档 ✓"); try { File.Delete(archived); } catch { } }

        // 结果落 JSONL
        log.WriteNote("external-tool", ExternalTool.ToJson(tool.Id, result).ToJsonString());
        log.Flush();

        int jsonlRows = CountLines(log.JsonlPath, "external-tool");
        Console.WriteLine($"[toolselftest] JSONL 中 external-tool 记录: {jsonlRows}");
        if (jsonlRows < 1) { Console.WriteLine("[toolselftest] FAIL: 运行结果未写入 JSONL"); ok = false; }

        Console.WriteLine($"[toolselftest] 日志目录: {log.SessionDir}");
        Console.WriteLine(ok ? "[toolselftest] PASS" : "[toolselftest] FAIL");
        return ok ? 0 : 2;
    }

    /// <summary>
    /// WHEA 解析链路自检。真实机器未必有 WHEA 事件（本机就是零事件），所以用一份合成事件走完整条链：
    /// 查询脚本 → JSON → 解析 → 时间过滤 → 判定为不通过 → 进报告与 JSONL。
    /// 合成脚本路径由 MEMSTRESS_WHEA_SCRIPT 指定，生产路径不会用到。
    /// </summary>
    private static int RunWheaSelfTest()
    {
        Console.WriteLine("[wheaselftest] 验证 WHEA 事件解析与判定链路");

        string fake = Path.Combine(AppPaths.Root, "scripts", "fake-whea.ps1");
        if (!File.Exists(fake))
        {
            Console.WriteLine($"[wheaselftest] FAIL: 缺少合成事件脚本 {fake}");
            return 2;
        }

        var original = Environment.GetEnvironmentVariable("MEMSTRESS_WHEA_SCRIPT");
        Environment.SetEnvironmentVariable("MEMSTRESS_WHEA_SCRIPT", fake);
        try
        {
            using var log = new LogWriter(AppPaths.LogsRoot, "wheaselftest");
            using var whea = new WheaMonitor();
            int found = 0;
            whea.WheaFound += w => { found++; log.WriteNote("whea", $"事件 {w.EventId} {w.Message}"); };

            whea.Start();
            whea.PollNow();
            whea.Stop();
            log.Flush();

            var events = whea.Events;
            Console.WriteLine($"[wheaselftest] 解析到事件: {events.Count}（期望 2）");
            foreach (var e in events) Console.WriteLine($"[wheaselftest]   ID {e.EventId} {e.Level} {e.Message[..Math.Min(60, e.Message.Length)]}");

            bool ok = true;
            if (events.Count != 2) { Console.WriteLine("[wheaselftest] FAIL: 事件数不符"); ok = false; }
            if (found != 2) { Console.WriteLine("[wheaselftest] FAIL: WheaFound 未按事件触发"); ok = false; }
            if (!events.Any(e => e.EventId == 47)) { Console.WriteLine("[wheaselftest] FAIL: 未解析到 47"); ok = false; }
            if (!events.Any(e => e.EventId == 17)) { Console.WriteLine("[wheaselftest] FAIL: 未解析到 17"); ok = false; }
            if (!events.All(e => e.Time.Year > 2000)) { Console.WriteLine("[wheaselftest] FAIL: 时间未正确解析"); ok = false; }
            if (!whea.Queryable) { Console.WriteLine("[wheaselftest] FAIL: Queryable 应为 true"); ok = false; }

            // 判定：零内存错误 + 有 WHEA 事件，结论必须是不通过
            var report = new ReportWriter.ReportData
            {
                SystemInfo = SystemInfo.Collect(),
                Options = "wheaselftest",
                SessionDir = log.SessionDir,
                Elapsed = TimeSpan.FromSeconds(1),
                TotalBytes = 1, BytesDone = 1, ErrorCount = 0,
                AllocationKind = "Test", AllocationNote = "",
                Passes = 1, Errors = new List<MemoryError>(),
                Whea = events.ToList(), WheaQueryable = whea.Queryable,
                Completed = true, StopReason = "wheaselftest"
            };
            string md = ReportWriter.BuildMarkdown(report);
            bool verdictOk = md.Contains("不通过") && md.Contains("WHEA");
            Console.WriteLine($"[wheaselftest] 判定（0 错误 + 2 条 WHEA）: {(verdictOk ? "不通过 ✓" : "错误 ✗")}");
            if (!verdictOk) ok = false;

            string path = Path.Combine(log.SessionDir, "report.md");
            File.WriteAllText(path, md);
            Console.WriteLine($"[wheaselftest] 报告: {path}");
            Console.WriteLine(ok ? "[wheaselftest] PASS" : "[wheaselftest] FAIL");
            return ok ? 0 : 2;
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEMSTRESS_WHEA_SCRIPT", original);
        }
    }

    /// <summary>
    /// 工具发现自检：把每个外部工具的「建议目录 / 搜索目录 / 是否找到 / 找到的路径」全部打出来。
    /// 这条路径原本无法验证，导致「下载了工具但程序找不到」这种问题只能靠猜。
    /// </summary>
    private static int RunToolScan()
    {
        Console.WriteLine("[toolscan] 外部工具发现结果");
        // 与 GUI 启动走同一条路径：确保投放点和说明文件确实被建出来，而不是只在界面里存在
        ExternalTool.SeedDropInFolders();
        Console.WriteLine($"[toolscan] 建议目录根: {AppPaths.ToolsRoot}");
        string? repaired = ExternalTool.RepairPrefs();
        if (repaired != null) Console.WriteLine($"[toolscan] 注意: paths.json 损坏，已备份为 {repaired}");
        int found = 0;
        foreach (var t in ExternalTool.Catalog)
        {
            string? path = ExternalTool.LoadManualPath(t.Id) ?? t.ResolvePath();
            string manual = ExternalTool.LoadManualPath(t.Id) != null ? "（手动指定，已记住）" : "";
            Console.WriteLine($"[toolscan] {t.DisplayName}: {(path != null ? "已找到" : "未找到")}{manual}");
            if (path != null)
            {
                found++;
                Console.WriteLine($"[toolscan]   路径: {path}");
            }
            Console.WriteLine($"[toolscan]   搜索范围: {t.SearchDescription()}");
        }
        Console.WriteLine($"[toolscan] 共 {found}/{ExternalTool.Catalog.Count} 个工具可用");
        return 0;
    }

    /// <summary>边读边数，避免与仍打开的写入句柄抢文件。</summary>
    private static int CountLines(string path, string? contains = null)
    {
        int n = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
            if (contains == null || line.Contains(contains, StringComparison.Ordinal)) n++;
        return n;
    }

    private static StressProfile ParseProfile(string[] args)
    {
        string p = GetString(args, "--profile", "Standard");
        return Enum.TryParse<StressProfile>(p, true, out var v) ? v : StressProfile.Standard;
    }

    /// <summary>
    /// 解析 --inject-fault 的故障类型。空值返回 ByteFlip（保持 --inject-error 的历史语义）。
    /// 名字大小写不敏感，与 FaultKind 枚举成员同名。
    /// </summary>
    private static FaultKind ParseFaultKind(string name)
    {
        if (string.IsNullOrEmpty(name)) return FaultKind.ByteFlip;
        return Enum.TryParse<FaultKind>(name, true, out var v) ? v : FaultKind.ByteFlip;
    }

    private static double GetDouble(string[] args, string name, double def)
    {
        int i = Array.IndexOf(args, name);
        if (i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        return def;
    }

    private static string GetString(string[] args, string name, string def)
    {
        int i = Array.IndexOf(args, name);
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];
        return def;
    }
}
