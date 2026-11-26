using System.Runtime.CompilerServices;

namespace MemStress.Core;

/// <summary>单条内存错误。字段与 TM5 的报错信息对齐：轮次、子测试、块、偏移、期望/实际。</summary>
public sealed class MemoryError
{
    // 跨线程可见性说明（曾经的疑虑，分析后不改）：
    // Timestamp 在 worker 线程构造，UI/日志线程读。DateTime 是 8 字节结构体，
    // x64 JIT 对对齐的 8 字节用单条 MOV 加载，实际原子；且对象引用的发布经过
    // ConcurrentQueue.Enqueue（有 full fence 语义），读侧不可能看到半构造对象。
    // 若将来换平台/改结构，把这里改成 long ticks 再在读侧还原。
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public int ThreadId { get; init; }
    public int Pass { get; init; }
    public string Pattern { get; init; } = "";
    public int BlockIndex { get; init; }
    public long OffsetInBlock { get; init; }
    public ulong AbsoluteAddress { get; init; }
    public ulong Expected { get; init; }
    public ulong Actual { get; init; }
    public int WidthBytes { get; init; } = 8;

    public string ToCsvLine() =>
        $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff},{ThreadId},{Pass},{Csv(Pattern)},{BlockIndex},{OffsetInBlock},0x{AbsoluteAddress:X16},0x{Expected:X16},0x{Actual:X16},{WidthBytes}";

    /// <summary>
    /// RFC 4180 转义：含逗号/引号/换行的字段用引号包起，内部引号翻倍。
    /// 现在的 7 个模式名都不含这些字符，但 Pattern 来自字符串、下游按列名解析 CSV——
    /// 加一个转义是买保险，比「将来谁加了个带逗号的名字、整个 CSV 静默错位」便宜得多。
    /// 数字与十六进制字段不可能含分隔符，不过。
    /// </summary>
    private static string Csv(string field) =>
        field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;
}

public sealed class Metrics
{
    /// <summary>1 GiB 的字节数。界面、自检、报告都按它换算，避免各写一份字面量。</summary>
    public const double BytesPerGiB = 1073741824.0;

    public TimeSpan Elapsed { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public long Errors { get; init; }
    public int Pass { get; init; }
    public double GigaBytesPerSecond => Elapsed.TotalSeconds > 0.05 ? BytesDone / Elapsed.TotalSeconds / BytesPerGiB : 0;
    /// <summary>单轮内进度：BytesDone 跨轮累加，取模避免第二轮起一直显示 100%。</summary>
    public double Percent => BytesTotal > 0 ? Math.Min(100, BytesDone % BytesTotal * 100.0 / BytesTotal) : 0;
    public string[] ActivePatterns { get; init; } = Array.Empty<string>();
}
