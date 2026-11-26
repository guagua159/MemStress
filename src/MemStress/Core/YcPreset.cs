using System.Diagnostics;
using System.Text;

namespace MemStress.Core;


/// <summary>
/// y-cruncher 的一键压测预设。
/// 参数依据（docs/research.md §7）：N63 与 VT3 是社区公认对内存/IMC 最敏感的两个算法，
/// FFTv4 补足浮点与缓存层次路径；三段时长分别对应快筛 / 确认 / 长驻留。
/// 算法名取自官方命令行手册 v0.8.6+ 的有效集合，N32/N64/HNT/VST/C17 已废弃。
/// </summary>
public sealed class YcPreset
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    /// <summary>每项测试秒数，对应 -D。</summary>
    public required int PerTestSeconds { get; init; }
    /// <summary>总时限秒数，对应 -TL。到点 y-cruncher 自行退出。</summary>
    public required int TotalSeconds { get; init; }
    /// <summary>算法列表，对应命令行末尾的位置参数。</summary>
    public required string[] Algorithms { get; init; }
    /// <summary>按物理内存总量的百分比申请 -M。</summary>
    public required double MemoryPercent { get; init; }
    public string Description => $"{string.Join(" + ", Algorithms)}，每项 {PerTestSeconds}s，总 {TotalSeconds / 60} 分钟，内存 {MemoryPercent * 100:F0}%";

    public static readonly YcPreset Quick = new()
    {
        Key = "quick", Label = "快速筛查", PerTestSeconds = 60, TotalSeconds = 900,
        Algorithms = new[] { "N63", "VT3" }, MemoryPercent = 0.70
    };

    public static readonly YcPreset Standard = new()
    {
        Key = "standard", Label = "标准验证", PerTestSeconds = 120, TotalSeconds = 3600,
        Algorithms = new[] { "FFTv4", "N63", "VT3" }, MemoryPercent = 0.85
    };

    public static readonly YcPreset Deep = new()
    {
        Key = "deep", Label = "深度驻留", PerTestSeconds = 300, TotalSeconds = 14400,
        Algorithms = new[] { "FFTv4", "N63", "VT3" }, MemoryPercent = 0.90
    };

    public static readonly IReadOnlyList<YcPreset> All = new[] { Quick, Standard, Deep };

    /// <summary>
    /// 按物理内存总量算出 -M 的 GiB 值。向下取整，最小 4 GiB，且不超过总量的 90%。
    /// 上限用 Math.Min 再夹一次，是为了让 Deep 的 90% 不会因取整而越界。
    /// </summary>
    public long MemoryGiB()
    {
        const long GiB = 1024L * 1024 * 1024;
        ulong total = Native.TotalPhysicalBytes();
        if (total == 0) return 4;
        double pct = Math.Min(MemoryPercent, 0.90);
        long gib = (long)(total * pct / GiB);
        return Math.Max(4, gib);
    }

    public string MemoryArg() => $"-M:{MemoryGiB()}G";

    /// <summary>组装完整命令行参数（不含 exe 本身）。logFile 必须是 Windows 绝对路径。</summary>
    public string[] BuildArgs(string logFile)
    {
        var list = new List<string> { $"logfile:\"{logFile}\"", "stress", MemoryArg(), $"-D:{PerTestSeconds}", $"-TL:{TotalSeconds}" };
        list.AddRange(Algorithms);
        return list.ToArray();
    }
}
