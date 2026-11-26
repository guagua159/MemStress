using System.Runtime.CompilerServices;

namespace MemStress.Core;

/// <summary>
/// Modulo-X：专为绕过缓存与写缓冲设计的模式。
///
/// 为什么需要它（依据 MemTest86 官方算法说明）：
/// 移动反转一类算法有一个固有弱点——「写之后立刻读」。写操作可能还停在 store buffer 里、
/// 或数据还在缓存中，紧接着的读会读到缓冲/缓存里的新值，而不是 DRAM 中的真实值。
/// 于是内存明明有故障，测试却显示通过。官方原文：
/// 「caching, buffering and out of order execution will interfere with the moving inversions algorithm
/// and make [it] less effective」，为此设计了 Modulo-X。
///
/// 官方算法：
///   对起始偏移 0–20：把每第 20 个位置写成目标图案；把其余位置写成其补码；重复若干次；最后校验标记位。
/// 关键在于写与读之间隔着**跨越整个地址空间的遍历**，缓存与写缓冲必然已被刷干净。
///
/// 性能要点（第一版写错过的地方）：
/// 最初对每个偏移都重新整块填充一次 → 2 极性 × 20 偏移 = **40 次全块写**，实测只有 0.25 GiB/s。
/// 但补码底图在所有偏移下都是同一个，只需**填充一次**；之后每个偏移只触碰 1/20 的标记位。
/// 于是总流量降到「1 次填充 + 20 × (写标记 + 读标记)」≈ 3 个整块当量，快一个数量级。
/// 这不会削弱绕开写缓冲的效果：一次标记位遍历要写约 1/20 块（本机 128 MiB 切片下约 6.7 MB，
/// 十万量级缓存行），远超 store buffer 的几十条容量，也远超 L3，因此后续读必然落到 DRAM。
/// </summary>
public sealed unsafe partial class StressEngine
{
    private const int ModuloStride = 20;

    private static void ModuloX(byte* p, long len, MemoryBlock block, int pass, CancellationToken ct, StressEngine owner)
    {
        if (len < ModuloStride * 2) return;

        // 两趟极性：标记位先写 0x00（底图 0xFF），再互换。
        // 只跑一种极性会漏掉方向性耦合故障（邻居为 1 时把 0 拉成 1，反向不发生）。
        for (int polarity = 0; polarity < 2; polarity++)
        {
            ct.ThrowIfCancellationRequested();
            byte marker = polarity == 0 ? (byte)0x00 : (byte)0xFF;
            byte other = (byte)~marker;

            // 底图只铺一次：所有偏移共用同一个补码底图
            Fill(p, len, other, nonTemporal: true);

            // 关键：**先写完所有偏移，再统一校验**。
            //
            // 第一版写成「写 offset i、校验 offset i-1」，两次之间只隔 1/20 块
            //（128 MiB 切片下约 6.7 MB），小于本机 L3（32 MB）——数据仍在缓存里，
            // 绕开写缓冲与缓存的目的根本没达到，等于白做。
            //
            // 官方要求「写与读之间隔着整块遍历」。写成两段式后，第一个偏移的写与它的读之间
            // 隔着其余 19 个偏移的写 + 它之前的校验，总量已是一整个块，缓存必然被挤出。
            for (int offset = 0; offset < ModuloStride; offset++)
            {
                ct.ThrowIfCancellationRequested();
                WriteMarkers(p, len, offset, marker);
            }
            // 注入点必须落在**标记位**上：ModuloX 只校验第 offset、offset+20… 这些位置，
            // 注入到非标记位不会被任何一轮校验覆盖到，会得到「注入了却检出 0 条」的假象，
            // 误判成模式漏检。
            // 注入后要立刻把该位改回 marker：否则接下来 offset=0 的写标记位会把它覆盖掉。
            // 正确顺序是：所有标记位写完 → 注入 → 校验，中间不能再有写。
            owner.ApplyFault(p, len);
            for (int offset = 0; offset < ModuloStride; offset++)
            {
                ct.ThrowIfCancellationRequested();
                VerifyMarkers(p, len, offset, marker, block, pass, owner);
            }
        }
    }

    /// <summary>
    /// 把第 offset、offset+20、offset+40… 个字节写成 <paramref name="value"/>。
    ///
    /// 跨步 20 的访问天然浪费带宽：每个标记位都要拉进一整条 64 字节缓存行，
    /// 却只改其中 1 字节。这不是实现问题，是 Modulo-X 的固有代价
    ///（官方亦说明「20 是速度与彻底性之间的折中」）。
    /// 因此本模式不放进任何档位，只作为专项检查用 `--pattern ModuloX` 单独运行。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void WriteMarkers(byte* p, long len, int offset, byte value)
    {
        for (long i = offset; i < len; i += ModuloStride) p[i] = value;
    }

    /// <summary>
    /// 校验标记位。只读标记位，非标记位不检查——它们本就是补码，
    /// 检查它们等于又做了一遍普通填充校验，反而稀释了 Modulo-X 的针对性。
    /// </summary>
    private static void VerifyMarkers(byte* p, long len, int offset, byte value,
        MemoryBlock block, int pass, StressEngine owner)
    {
        for (long i = offset; i < len; i += ModuloStride)
        {
            if (p[i] != value)
                owner.Report(Mk("ModuloX", block, p + i, pass, value, p[i]), len);
        }
    }
}
