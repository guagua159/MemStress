namespace MemStress.Core;

public enum StressProfile
{
    /// <summary>快速筛查：移动反转 + 地址即数据。</summary>
    Quick,
    /// <summary>标准：4 种模式（加随机填充与块拷贝），日常筛查的默认档。</summary>
    Standard,
    /// <summary>深度：全部 6 种模式（加走 1 与棋盘格），覆盖位线与相邻单元耦合。</summary>
    Deep,
    /// <summary>
    /// 彻底：在 6 种模式之上加 Modulo-X。慢约 4 倍，但能查出被缓存/写缓冲掩盖的错误
    /// （移动反转类算法「写后立即读」的固有盲区，见 StressEngine.ModuloX.cs 的说明）。
    /// </summary>
    Thorough
}

public sealed class TestOptions
{
    /// <summary>取可用物理内存的百分比（0.5~0.95）。</summary>
    public double MemoryPercent { get; set; } = 0.85;

    /// <summary>非 0 时忽略百分比，按固定 MiB 分配。</summary>
    public int FixedMiB { get; set; }

    /// <summary>0 表示按物理核数。</summary>
    public int Threads { get; set; }

    /// <summary>块大小（MiB）。每块独立分配，便于定位错误地址。</summary>
    public int ChunkMiB { get; set; } = 256;

    public bool UseLargePages { get; set; } = true;

    /// <summary>完整轮数；0 表示按时间跑。</summary>
    public int Passes { get; set; } = 1;

    /// <summary>按时间跑的秒数（Passes==0 时生效）。</summary>
    public int DurationSeconds { get; set; } = 600;

    public StressProfile Profile { get; set; } = StressProfile.Standard;

    public int Seed { get; set; } = 0x5EED;

    /// <summary>验收用：在第 1 块的第 1 个模式里故意写坏一个字节，验证报错链路。</summary>
    public bool InjectError { get; set; }

    /// <summary>维护用：--inject-fault 指定注入的故障类型。默认 ByteFlip（兼容 --inject-error 原语义）。</summary>
    public FaultKind FaultKind { get; set; } = FaultKind.ByteFlip;

    /// <summary>
    /// 自研压测结束后是否接着跑 y-cruncher 交叉验证。默认关闭——它要额外几十分钟，
    /// 不能因为「顺手」就让默认路径多花这么多时间。
    /// </summary>
    public bool CrossCheck { get; set; }

    /// <summary>
    /// 维护用：只跑指定的一个模式（空=按档位）。用于单独测量某个模式的吞吐与检测能力，
    /// 否则档位里几个模式混在一起，说不清慢的是哪个。仅命令行自检使用，界面不暴露。
    /// </summary>
    public string? OnlyPattern { get; set; }

    public string[] Patterns => OnlyPattern is { Length: > 0 } one
        ? new[] { one }
        : Profile switch
        {
            StressProfile.Quick => new[] { "MovingInversion", "AddressAsData" },
            StressProfile.Standard => new[] { "MovingInversion", "AddressAsData", "RandomFill", "BlockCopy" },
            StressProfile.Deep => new[] { "MovingInversion", "AddressAsData", "RandomFill", "BlockCopy", "WalkingOnes", "Checkerboard" },
            _ => new[] { "MovingInversion", "AddressAsData", "RandomFill", "BlockCopy", "WalkingOnes", "Checkerboard", "ModuloX" }
        };

    public TestOptions Clone() => (TestOptions)MemberwiseClone();

    public string Describe()
    {
        string size = FixedMiB > 0 ? $"{FixedMiB} MiB" : $"{MemoryPercent:P0} 可用内存";
        // 给人看的描述用小时（与界面输入单位一致）；引擎与命令行仍用秒。
        string dur = Passes > 0 ? $"{Passes} 轮" : $"{DurationSeconds / 3600.0:F1} 小时";
        return $"{Profile} | {size} | 线程 {Threads} | 块 {ChunkMiB} MiB | {dur} | 大页 {(UseLargePages ? "开" : "关")}";
    }
}
