using System.Windows;
using MemStress.Core;

namespace MemStress;

/// <summary>
/// 无界面入口（--selftest / --inject-error）：验收脚本用，也方便在 CI 里跑一遍。
/// 用法：
///   MemStress.exe --selftest [--seconds 8] [--percent 0.2] [--inject-error] [--report out.md]
/// </summary>
public static partial class SelfTest
{
    public static int Run(string[] args)
    {
        // 外部工具编排的自检单独走一条路：不跑压测，只验证「拉起子进程 → 捕获输出 → 归档 → 落盘」这条链
        if (args.Contains("--toolselftest")) return RunToolSelfTest();
        // 工具发现自检：验证「找得到已安装的第三方工具」，并打印实际搜索了哪些目录
        if (args.Contains("--toolscan")) return RunToolScan();
        // WHEA 解析链路自检：用合成事件验证「查询 → JSON → 解析 → 过滤 → 判定 → 报告」，
        // 真实机器上未必有 WHEA 事件可测（本机就是零事件）
        if (args.Contains("--wheaselftest")) return RunWheaSelfTest();
        var opt = new TestOptions
        {
            MemoryPercent = GetDouble(args, "--percent", 0.2),
            // 默认 0=按时长跑，便于自检固定时长；--passes N 可改为按轮次跑，
            // 用来验证「轮数是否真的被线程数抵消」这类缺陷（见 CHANGELOG 2026-11-25）。
            Passes = (int)GetDouble(args, "--passes", 0),
            DurationSeconds = (int)GetDouble(args, "--seconds", 8),
            InjectError = args.Contains("--inject-error"),
            Threads = (int)GetDouble(args, "--threads", 0),
            ChunkMiB = (int)GetDouble(args, "--chunk", 64),
            UseLargePages = !args.Contains("--no-large-pages"),
            Profile = ParseProfile(args),
            // 维护用：--pattern MovingInversion 只跑这一个模式，用于单独量化它的吞吐与检测力。
            OnlyPattern = GetString(args, "--pattern", "") is { Length: > 0 } op ? op : null,
            // 维护用：--inject-fault <类型> 指定注入的故障类型（默认 ByteFlip，保持 --inject-error 原语义）。
            FaultKind = ParseFaultKind(GetString(args, "--inject-fault", ""))
        };

        Console.WriteLine($"[selftest] 参数: {opt.Describe()}");
        // 矩阵回归用：跳过 WMI 机器信息采集。4 次 PowerShell 查询本机实测约 2.1 秒，
        // 63 格就是 2 分钟白等，而矩阵只关心「这一格能否检出注入的错误」，不关心机器型号。
        var info = SystemInfo.Collect(skipSlow: args.Contains("--skip-sysinfo"));
        Console.WriteLine($"[selftest] 机器: {info["cpu"]} / {info["mainboard"]} / 物理核 {info["physicalCores"]} / 可用 {info["availablePhysicalMiB"]} MiB");

        using var log = new LogWriter(AppPaths.LogsRoot, "selftest");
        using var whea = new WheaMonitor();
        var errors = new List<MemoryError>();
        int wheaCount = 0;

        using var engine = new StressEngine(opt);
        engine.ErrorFound += e =>
        {
            errors.Add(e);
            log.WriteError(e);
            if (errors.Count <= 5)
                Console.WriteLine($"[错误] 轮 {e.Pass} {e.Pattern} 块{e.BlockIndex} 偏移 0x{e.OffsetInBlock:X} 期望 0x{e.Expected:X16} 实际 0x{e.Actual:X16}");
        };
        engine.StatusChanged += s => Console.WriteLine($"[状态] {s}");
        whea.WheaFound += w => { wheaCount++; log.WriteNote("whea", $"事件 {w.EventId} {w.Message}"); };

        var header = info;
        header["options"] = opt.Describe();
        header["mode"] = "selftest";
        log.WriteSessionHeader(header);

        whea.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        engine.Start();
        Console.WriteLine($"[selftest] 分配 {engine.TotalBytes / 1048576} MiB，运行 {opt.DurationSeconds}s…");
        // 与 GUI 路径保持一致：分配方式必须落进日志，事后才能解释「两次结果为何不同」。
        log.WriteNote("allocation", $"{engine.Arena?.Kind} | {engine.TotalBytes / 1048576} MiB | {engine.Arena?.Note}");
        log.WriteNote("cache", $"L3={Native.LastLevelCacheBytes() / 1048576} MiB | 切片={StressEngine.SliceBytes / 1048576} MiB");

        while (sw.Elapsed.TotalSeconds < opt.DurationSeconds && engine.IsRunning)
        {
            Thread.Sleep(1000);
            var m = engine.Snapshot();
            Console.WriteLine($"[进度] {m.Elapsed.TotalSeconds:F0}s {m.Percent:F1}% {m.GigaBytesPerSecond:F2} GiB/s 错误 {m.Errors}");
        }
        engine.Stop();
        whea.Stop();
        sw.Stop();

        var snap = engine.Snapshot();
        var report = new ReportWriter.ReportData
        {
            SystemInfo = header,
            Options = opt.Describe(),
            SessionDir = log.SessionDir,
            Elapsed = sw.Elapsed,
            TotalBytes = engine.TotalBytes * opt.Patterns.Length,
            AllocationBytes = engine.TotalBytes,
            BytesDone = snap.BytesDone,
            ErrorCount = errors.Count,
            AllocationKind = engine.Arena?.Kind.ToString() ?? "?",
            AllocationNote = engine.Arena?.Note ?? "",
            Passes = snap.Pass,
            Errors = errors,
            Whea = whea.Events.ToList(),
            WheaQueryable = whea.Queryable,
            Completed = true,
            StopReason = "selftest 时长到"
        };

        string reportPath = GetString(args, "--report", Path.Combine(log.SessionDir, "report.md"));
        File.WriteAllText(reportPath, ReportWriter.BuildMarkdown(report));

        Console.WriteLine($"[selftest] 完成: {snap.BytesDone / Metrics.BytesPerGiB:F2} GiB 读写, {snap.GigaBytesPerSecond:F2} GiB/s, 错误 {errors.Count}, WHEA {wheaCount}");
        Console.WriteLine($"[selftest] 日志: {log.SessionDir}");
        Console.WriteLine($"[selftest] 报告: {reportPath}");
        log.Flush();
        int csvLines = CountLines(log.CsvPath);
        int jsonlLines = CountLines(log.JsonlPath);
        Console.WriteLine($"[selftest] CSV 行数: {csvLines}, JSONL 行数: {jsonlLines}");

        // 验收判据：注入了错误就必须在 CSV/JSONL 里各多一行
        bool ok = true;
        if (opt.InjectError)
        {
            int csvRows = CountLines(log.CsvPath) - 1;
            int jsonlErrorRows = CountLines(log.JsonlPath, "\"pattern\"");
            Console.WriteLine($"[selftest] 注入校验: CSV 错误行 {csvRows}, JSONL 错误行 {jsonlErrorRows}, 内存中 {errors.Count}");
            ok = csvRows > 0 && jsonlErrorRows > 0 && errors.Count > 0;
            if (!ok) Console.WriteLine("[selftest] 失败：注入的错误没有完整落盘");
        }
        if (snap.BytesDone <= 0) { Console.WriteLine("[selftest] 失败：没有产生任何读写量"); ok = false; }
        Console.WriteLine(ok ? "[selftest] PASS" : "[selftest] FAIL");
        return ok ? 0 : 2;
    }

    /// <summary>
    /// 外部工具编排自检：用系统自带的一个小工具当被测对象，验证
    /// 「拉起子进程 → 逐行捕获 stdout/stderr → 写日志文件 → 归档工具自有日志 → 结果落 JSONL」整条链路。
    /// 真实工具（y-cruncher）未安装时也能验证这段代码不会在用户机器上炸。
    /// </summary>
}
