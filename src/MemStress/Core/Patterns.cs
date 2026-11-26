namespace MemStress.Core;

public enum PatternKind
{
    MovingInversion,
    AddressAsData,
    RandomFill,
    BlockCopy,
    WalkingOnes,
    Checkerboard,
    ModuloX
}

/// <summary>
/// 测试模式的元数据。设计参照 TestMem5 的四类子测试：
/// RefreshStable（写非常量 pattern 后回读校验，唯一做数据完整性判定）、SimpleTest（通用读写打满内存 I/O）、
/// MirrorMove / MirrorMove128（把测试块里外翻转、按奇偶条纹多趟搬运，破坏控制器对访问模式的自适应）。
/// 本项目对应关系：MovingInversion/Checkerboard ≈ SimpleTest，RandomFill ≈ RefreshStable，
/// AddressAsData 抓地址译码，BlockCopy ≈ MirrorMove。所有模式都在本进程内做完整回读校验。
/// </summary>
public static class Patterns
{
    public static readonly PatternKind[] All =
    {
        PatternKind.MovingInversion, PatternKind.AddressAsData, PatternKind.RandomFill,
        PatternKind.BlockCopy, PatternKind.WalkingOnes, PatternKind.Checkerboard
    };

    public static string Name(PatternKind k) => k switch
    {
        PatternKind.MovingInversion => "MovingInversion",
        PatternKind.AddressAsData => "AddressAsData",
        PatternKind.RandomFill => "RandomFill",
        PatternKind.BlockCopy => "BlockCopy",
        PatternKind.WalkingOnes => "WalkingOnes",
        PatternKind.Checkerboard => "Checkerboard",
        PatternKind.ModuloX => "ModuloX",
        _ => k.ToString()
    };

    /// <summary>每个模式压什么，界面上直接展示，避免用户只看进度不懂含义。</summary>
    public static string Description(PatternKind k) => k switch
    {
        PatternKind.MovingInversion => "移动反转：走到哪先校验该位置再写补码，正反各一趟，压数据总线与写缓冲",
        PatternKind.AddressAsData => "地址即数据：把块内偏移写进内存再校验，专抓地址译码错误（错误会指出错在哪一行）",
        PatternKind.RandomFill => "随机填充：用种子生成随机字节流写入后整块比对，覆盖面最广，最慢",
        PatternKind.BlockCopy => "块间搬运：相邻块互为源和目的整块拷贝再校验，压内存控制器与缓存一致性",
        PatternKind.WalkingOnes => "走 1：单个比特位在字节内依次置位并覆盖整块，压位线故障与相邻位串扰",
        PatternKind.Checkerboard => "棋盘格：0x55/0xAA 交替填充并跑正反两种极性，抓方向性相邻单元耦合故障",
        PatternKind.ModuloX => "模 20 错位：写与读之间隔开整块遍历，绕开缓存与写缓冲的干扰，专抓移动反转漏掉的错误",
        _ => ""
    };

    public static PatternKind Parse(string name) =>
        Enum.TryParse<PatternKind>(name, true, out var k) ? k : PatternKind.MovingInversion;
}
