using System.Text;
using System.Text.Json.Nodes;

namespace MemStress.Core;

/// <summary>
/// 报告导出：Markdown（贴论坛/工单）与自包含 HTML（双击可看，无外部依赖）。
/// 结论先行：顶部直接给「通过 / 不通过」判定，再给证据。
/// </summary>
public static class ReportWriter
{
    public sealed class ReportData
    {
        public required JsonObject SystemInfo { get; init; }
        public required string Options { get; init; }
        public required string SessionDir { get; init; }
        public required TimeSpan Elapsed { get; init; }
        public required long TotalBytes { get; init; }
        /// <summary>实际分配的内存量（不是读写量）。TotalBytes 是「分配量 × 模式数」。</summary>
        public long AllocationBytes { get; init; }
        public required long BytesDone { get; init; }
        public required long ErrorCount { get; init; }
        public required string AllocationNote { get; init; }
        public required string AllocationKind { get; init; }
        public required int Passes { get; init; }
        public required List<MemoryError> Errors { get; init; }
        public required List<WheaMonitor.WheaEvent> Whea { get; init; }

        /// <summary>交叉验证结果。默认未启用，保证旧调用方不受影响。</summary>
        public CrossCheckResult CrossCheck { get; init; } = new();
        /// <summary>事件日志能否读取。false 时报告必须写「未监控」，不能写成「无事件」。</summary>
        public bool WheaQueryable { get; init; } = true;
        public required bool Completed { get; init; }
        public required string StopReason { get; init; }

        public string Verdict
        {
            get
            {
                if (Whea.Count > 0) return "不通过（出现 WHEA 硬件错误事件，即使内存校验零错误也不稳定）";
                if (ErrorCount > 0) return "不通过（内存校验发现错误）";
                if (!Completed) return "未完成（测试被中止，结果不作为稳定性结论）";
                // WHEA 读不到时不能给「通过」：内存校验零错误只证明压测没抓到，
                // 而 WHEA 那一半（硬件是否报过错）根本没看。给「通过」等于把「没看」说成「没问题」。
                if (!WheaQueryable) return "内存校验通过，但 WHEA 未监控（无法读取事件日志，硬件错误这一半没有结论）";
                return "通过（本次配置下未发现错误，仍需结合其他工具交叉验证）";
            }
        }
    }

    public static string BuildMarkdown(ReportData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MemStress 内存压力测试报告");
        sb.AppendLine();
        sb.AppendLine($"**结论：{d.Verdict}**");
        sb.AppendLine();
        sb.AppendLine($"- 开始时间：{d.SystemInfo["sessionStart"]}");
        sb.AppendLine($"- 运行时长：{d.Elapsed:hh\\:mm\\:ss}");
        sb.AppendLine($"- 测试参数：{d.Options}");
        sb.AppendLine($"- 分配方式：{d.AllocationKind}（{d.AllocationNote}）");
        // 注意：TotalBytes 是「分配量 × 模式数」，即跑满一轮需要读写的字节总量，
        // 不是分配了多少内存。早先这里写成「GiB 分配」，在 100 GiB 的机器上会写出不可能的数字。
        double allocGiB = d.AllocationBytes / Metrics.BytesPerGiB;
        double roundGiB = d.TotalBytes / Metrics.BytesPerGiB;
        sb.AppendLine($"- 内存占用：{allocGiB:F1} GiB 已分配（整轮不释放）");
        // 不足一整轮时写「0 轮」会让人误以为没跑，改为按轮次的百分比表述
        string roundText = d.Passes > 0
            ? $"完成 {d.Passes} 轮"
            : d.TotalBytes > 0 ? $"完成 {d.BytesDone * 100.0 / d.TotalBytes:F0}% 的单轮（未满 1 轮）" : "未产生读写";
        sb.AppendLine($"- 有效覆盖：{d.BytesDone / Metrics.BytesPerGiB:F1} GiB 读写 / 单轮 {roundGiB:F1} GiB，{roundText}");
        sb.AppendLine($"- 错误数：{d.ErrorCount}");
        sb.AppendLine($"- WHEA 事件：{d.Whea.Count}");
        sb.AppendLine($"- 日志目录：`{d.SessionDir}`");
        sb.AppendLine();

        sb.AppendLine("## 机器信息");
        sb.AppendLine();
        sb.AppendLine("| 项目 | 值 |");
        sb.AppendLine("|---|---|");
        foreach (var kv in d.SystemInfo)
        {
            if (kv.Key == "memoryModules") continue;
            sb.AppendLine($"| {kv.Key} | {kv.Value?.ToString()?.Replace("\n", " ")} |");
        }
        if (d.SystemInfo["memoryModules"] is JsonArray mods && mods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| 插槽 | 容量 MiB | 标称频率 | 实际频率 | 厂商 | 型号 |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var m in mods)
            {
                if (m is not JsonObject mo) continue;
                sb.AppendLine($"| {mo["slot"]} | {mo["capacityMiB"]} | {mo["ratedSpeed"]} | {mo["configuredSpeed"]} | {mo["manufacturer"]} | {mo["partNumber"]} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## 错误明细（前 200 条）");
        sb.AppendLine();
        if (d.Errors.Count == 0)
        {
            sb.AppendLine("无。");
        }
        else
        {
            sb.AppendLine("| 时间 | 轮次 | 模式 | 块 | 偏移 | 绝对地址 | 期望 | 实际 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var e in d.Errors.Take(200))
                sb.AppendLine($"| {e.Timestamp:HH:mm:ss.fff} | {e.Pass} | {e.Pattern} | {e.BlockIndex} | {e.OffsetInBlock} | 0x{e.AbsoluteAddress:X16} | 0x{e.Expected:X16} | 0x{e.Actual:X16} |");
            if (d.Errors.Count > 200) sb.AppendLine($"\n> 仅列出前 200 条，完整记录见 errors.csv / errors.jsonl。");
        }

        sb.AppendLine();
        sb.AppendLine("## WHEA 硬件错误事件");
        sb.AppendLine();
        if (!d.WheaQueryable)
            sb.AppendLine("**未监控**：无法读取系统事件日志（可能缺少权限或 Event Log 服务异常），本次测试没有 WHEA 结论，请手动在「事件查看器 → Windows 日志 → 系统」中筛选来源 Microsoft-Windows-WHEA-Logger 核对。");
        else if (d.Whea.Count == 0) sb.AppendLine("测试期间未捕获到 Microsoft-Windows-WHEA-Logger 事件。");
        else
        {
            sb.AppendLine("| 时间 | 事件 ID | 级别 | 摘要 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var w in d.Whea)
                sb.AppendLine($"| {w.Time:HH:mm:ss} | {w.EventId} | {w.Level} | {w.Message.Replace("\n", " ").Replace("|", "\\|")} |");
            sb.AppendLine();
            sb.AppendLine("**事件 ID 含义（社区经验，非厂商文档，仅供参考）**");
            sb.AppendLine();
            sb.AppendLine("- **19**：多与 **FCLK（Infinity Fabric）不稳定**相关。可先降 FCLK/内存频率，或小幅提高 SoC 电压后复测。");
            sb.AppendLine("- **18 / 20**：通常代表更严重的不稳定（多在 SoC / 内存控制器侧），建议先恢复默认频率验证。");
            sb.AppendLine("- **47**：已纠正的内存硬件错误，常见于重负载下，是内存时序或电压偏紧的信号。");
            sb.AppendLine("- 依据：[社区实测经验](https://www.reddit.com/r/overclocking/comments/111wa0a/are_these_worrying_signs_or_no/) 指出 WHEA 19 大多与 FCLK 相关。");
        }

        sb.AppendLine();
        sb.AppendLine("## 交叉验证（第二工具）");
        sb.AppendLine();
        sb.AppendLine(d.CrossCheck.Describe());
        if (d.CrossCheck.State == CrossCheckState.Completed)
        {
            sb.AppendLine();
            sb.AppendLine($"- 档位：{d.CrossCheck.PresetLabel}");
            sb.AppendLine($"- 迭代次数：{d.CrossCheck.Iterations}");
            sb.AppendLine($"- 耗时：{d.CrossCheck.ElapsedSeconds / 60:F1} 分钟");
            sb.AppendLine($"- 判定：{(d.CrossCheck.Passed ? "通过" : "不通过")}");
            if (d.CrossCheck.LogPath.Length > 0)
                sb.AppendLine($"- 日志：`{d.CrossCheck.LogPath}`");
        }
        else if (d.CrossCheck.State == CrossCheckState.ToolNotFound)
        {
            sb.AppendLine();
            sb.AppendLine("> 这一节不是「没问题」，是「没测」——上面自研算法的结论没有被第二工具验证过。");
        }

        sb.AppendLine();
        sb.AppendLine("## 判定依据与局限");
        sb.AppendLine("- 通过不等于内存绝对可靠：单一工具、单一负载类型无法覆盖所有失效模式，业界做法是用不同类型负载的第二个工具交叉验证（本项目内置 y-cruncher 交叉验证阶段，默认关闭）。");
        sb.AppendLine("- 出现任一内存错误或 WHEA 事件即判不通过；错误集中在同一块、同一模式时，优先怀疑该地址区间或时序参数。");
        sb.AppendLine("- 所有模式都覆盖整块内存，不做采样截断（走 1 早期版本只测每块前 32 MiB，现已改为链式遍历整块，覆盖率提升 8 倍）。");
        sb.AppendLine();
        sb.AppendLine($"> 由 MemStress 生成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}，停止原因：{d.StopReason}");
        return sb.ToString();
    }
}
