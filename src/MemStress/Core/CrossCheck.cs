namespace MemStress.Core;

/// <summary>
/// 交叉验证的三种状态。必须分开表达——把「没开」「找不到工具」「跑了但没过」混成一句
/// 「未运行」，就等于用一句模糊话掩盖了三种完全不同的处境，违反本项目
/// 「测不到就说测不到」的红线。
/// </summary>
public enum CrossCheckState
{
    /// <summary>用户没开这个开关。</summary>
    NotRequested,

    /// <summary>开了，但在约定位置没找到 y-cruncher.exe。这不是「没问题」，是「没测」。</summary>
    ToolNotFound,

    /// <summary>正在跑。此时导出报告不能写成「未启用」，也不能写成「已完成」。</summary>
    Running,

    /// <summary>跑了。结果看 <see cref="CrossCheckResult.Passed"/>。</summary>
    Completed
}

/// <summary>交叉验证的结果快照，用于报告与界面。</summary>
public sealed class CrossCheckResult
{
    public CrossCheckState State { get; init; } = CrossCheckState.NotRequested;

    /// <summary>使用的档位名（如「标准验证」）。未执行时为空。</summary>
    public string PresetLabel { get; init; } = "";

    /// <summary>y-cruncher 自报的迭代次数。</summary>
    public int Iterations { get; init; }

    /// <summary>是否通过。<see cref="CrossCheckState.Completed"/> 之外无意义。</summary>
    public bool Passed { get; init; }

    /// <summary>耗时秒数。</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>本次运行的日志文件路径（y-cruncher 自己的 logfile）。</summary>
    public string LogPath { get; init; } = "";

    /// <summary>给报告用的一行说明。三态各有明确措辞，不允许都写成「未运行」。</summary>
    public string Describe() => State switch
    {
        CrossCheckState.NotRequested =>
            "未启用（自研压测已单独完成；交叉验证需要额外时间，默认关闭）",
        CrossCheckState.ToolNotFound =>
            "未执行：未找到 y-cruncher.exe（放到 tools\\ycruncher\\ 后可用）。本次结论仅基于自研算法，未经第二工具交叉验证",
        CrossCheckState.Running =>
            $"进行中（{PresetLabel}），本报告生成时尚未跑完，结论待定",
        CrossCheckState.Completed =>
            $"已执行：{PresetLabel}，{Iterations} 轮迭代，耗时 {ElapsedSeconds / 60:F1} 分钟，判定 {(Passed ? "通过" : "不通过")}",
        _ => "未执行"
    };
}
