using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace MemStress.Core;

/// <summary>
/// 自研压测的六个模式内核 + 校验辅助。
/// 每个模式对应一种经典内存故障模型（出处见 Patterns.cs 注释）；
/// 线程调度、进度记账、报错去重在 StressEngine.cs，这里只管「怎么读写、怎么校验」。
/// 加新模式：在 PatternKind 里加枚举值，在这里加一个方法，再到 RunOne 里接上分支。
/// </summary>
public sealed unsafe partial class StressEngine
{
    // ---------- 具体测试模式 ----------
    // 所有模式的错误记录统一经 Mk() 构造：块内偏移、绝对地址、期望值、实际值都由失败地址反推，
    // 不再让各模式各自填——早期版本只有实际值是真的，期望值与绝对地址恒为 0，报错定位不到具体位置。

    /// <summary>
    /// 移动反转：MemTest86 官方算法的核心是「走到哪、先校验、再取反」，正反各一趟。
    ///
    /// 原实现只是「填 0xA5 → 校验 → 填 0x5A → 校验」，那是两次整块填充校验，不是移动反转。
    /// 两者检出能力的差别是实质性的：移动反转在写入位置 i 之前先校验 i，
    /// 因此任何「写 i-1 时把 i 带坏了」的耦合故障会当场暴露；而整块填满再统一校验时，
    /// 坏掉的位置会被后续写入覆盖掉，故障消失得无影无踪。
    /// 这是本项目出现频率最高的模式（三档都含），也是唯一被文档描述为「压数据总线与写缓冲」的模式。
    /// </summary>
    private static void MovingInversion(byte* p, long len, MemoryBlock block, int pass, CancellationToken ct, StressEngine owner)
    {
        long bytes = len & ~7L;
        // 整块写一次就回读，用非临时写：写阶段不污染缓存，回读只能从 DRAM 取。
        Fill(p, bytes, 0xA5, nonTemporal: true);
        owner.ApplyFault(p, bytes);   // 注入自检：写完、校验前篡改
        WalkInvert(p, bytes, 0xA5, forward: true, block, pass, ct, owner);   // 校验 0xA5 → 写 0x5A
        WalkInvert(p, bytes, 0x5A, forward: false, block, pass, ct, owner);  // 校验 0x5A → 写 0xA5
    }

    /// <summary>
    /// 单向「校验后取反」遍历。<paramref name="forward"/> 为 false 时从高地址往低地址走，
    /// 两个方向都要跑：写操作对**前一个**地址的干扰在正向能查出，对**后一个**地址的干扰只有反向能查出。
    /// </summary>
    private static void WalkInvert(byte* p, long bytes, byte expected, bool forward,
        MemoryBlock block, int pass, CancellationToken ct, StressEngine owner)
    {
        byte inv = (byte)~expected;
        long alignedEnd = bytes & ~31L;

        {
            var ve = Vector256.Create(expected);
            var vi = Vector256.Create(inv);
            if (forward)
            {
                for (long i = 0; i < alignedEnd; i += 32)
                {
                    if (!Vector256.EqualsAll(Unsafe.Read<Vector256<byte>>(p + i), ve))
                        ScanByteRun(p + i, 32, expected, block, pass, "MovingInversion", owner);
                    Unsafe.Write(p + i, vi);
                    if ((i & 0x3FFFF) == 0) ct.ThrowIfCancellationRequested();
                }
                for (long i = alignedEnd; i < bytes; i++)
                {
                    if (p[i] != expected) owner.Report(Mk("MovingInversion", block, p + i, pass, expected, p[i]), bytes);
                    p[i] = inv;
                }
            }
            else
            {
                for (long i = bytes - 1; i >= alignedEnd; i--)
                {
                    if (p[i] != expected) owner.Report(Mk("MovingInversion", block, p + i, pass, expected, p[i]), bytes);
                    p[i] = inv;
                }
                for (long i = alignedEnd - 32; i >= 0; i -= 32)
                {
                    if (!Vector256.EqualsAll(Unsafe.Read<Vector256<byte>>(p + i), ve))
                        ScanByteRun(p + i, 32, expected, block, pass, "MovingInversion", owner);
                    Unsafe.Write(p + i, vi);
                    if ((i & 0x3FFFF) == 0) ct.ThrowIfCancellationRequested();
                }
            }
        }
    }

    private static void AddressAsData(byte* p, long len, MemoryBlock block, int pass, CancellationToken ct, StressEngine owner)
    {
        ulong* q = (ulong*)p;
        long count = len / 8;
        ulong addrBase = (ulong)p;
        for (long i = 0; i < count; i++) q[i] = (addrBase + (ulong)i * 8) ^ 0x9E3779B97F4A7C15UL;
        owner.ApplyFault(p, len);   // 注入自检：写完、校验前篡改
        for (long i = 0; i < count; i++)
        {
            ulong expect = (addrBase + (ulong)i * 8) ^ 0x9E3779B97F4A7C15UL;
            ulong actual = q[i];
            if (actual != expect) owner.Report(Mk("AddressAsData", block, p + i * 8, pass, expect, actual), count);
            if ((i & 0x3FFFF) == 0) ct.ThrowIfCancellationRequested();
        }
    }

    private static void RandomFill(byte* p, long len, MemoryBlock block, int pass, CancellationToken ct, StressEngine owner)
    {
        ulong seed = 0x243F6A8885A308D3UL ^ (ulong)(block.Index * 0x100000001B3L) ^ (ulong)pass;
        ulong* q = (ulong*)p;
        long count = len / 8;

        // 写阶段用非临时写：这块数据只写一次，随后马上回读校验，
        // 让写不占缓存、回读只能从 DRAM 取（依据见 Fill 的注释）。
        // 一次生成 8 个数（64 字节）拼成两个 256 位向量再存，减少循环开销与状态推进次数。
        ulong s0 = seed, s1 = seed ^ 0x13198A2E03707344UL;
        ulong s2 = seed ^ 0xA4093822299F31D0UL, s3 = seed ^ 0x082EFA98EC4E6C89UL;
        long i = 0;
        {
            for (; i + 8 <= count; i += 8)
            {
                var lo = Vector256.Create(
                    XorShift(ref s0), XorShift(ref s1), XorShift(ref s2), XorShift(ref s3));
                var hi = Vector256.Create(
                    XorShift(ref s0), XorShift(ref s1), XorShift(ref s2), XorShift(ref s3));
                Vector256.StoreAlignedNonTemporal(lo, (ulong*)(p + i * 8));
                Vector256.StoreAlignedNonTemporal(hi, (ulong*)(p + i * 8 + 32));
                if ((i & 0x3FFFF) == 0) ct.ThrowIfCancellationRequested();
            }
        }
        for (; i < count; i++) q[i] = XorShift(ref s0);

        owner.ApplyFault(p, len);   // 注入自检：写完、校验前篡改

        // 回读校验：同样的种子重放同一序列
        s0 = seed; s1 = seed ^ 0x13198A2E03707344UL;
        s2 = seed ^ 0xA4093822299F31D0UL; s3 = seed ^ 0x082EFA98EC4E6C89UL;
        i = 0;
        {
            for (; i + 8 <= count; i += 8)
            {
                ulong e0 = XorShift(ref s0), e1 = XorShift(ref s1), e2 = XorShift(ref s2), e3 = XorShift(ref s3);
                ulong e4 = XorShift(ref s0), e5 = XorShift(ref s1), e6 = XorShift(ref s2), e7 = XorShift(ref s3);
                var exp = Vector256.Create(e0, e1, e2, e3);
                var exp2 = Vector256.Create(e4, e5, e6, e7);
                var got = Unsafe.Read<Vector256<ulong>>(p + i * 8);
                var got2 = Unsafe.Read<Vector256<ulong>>(p + i * 8 + 32);
                // 向量整体相等时完全跳过逐数比较，热路径只剩两次比较
                if (!Vector256.EqualsAll(got, exp))
                {
                    if (got.GetElement(0) != e0) owner.Report(Mk("RandomFill", block, p + i * 8, pass, e0, got.GetElement(0)), count);
                    if (got.GetElement(1) != e1) owner.Report(Mk("RandomFill", block, p + i * 8 + 8, pass, e1, got.GetElement(1)), count);
                    if (got.GetElement(2) != e2) owner.Report(Mk("RandomFill", block, p + i * 8 + 16, pass, e2, got.GetElement(2)), count);
                    if (got.GetElement(3) != e3) owner.Report(Mk("RandomFill", block, p + i * 8 + 24, pass, e3, got.GetElement(3)), count);
                }
                if (!Vector256.EqualsAll(got2, exp2))
                {
                    if (got2.GetElement(0) != e4) owner.Report(Mk("RandomFill", block, p + i * 8 + 32, pass, e4, got2.GetElement(0)), count);
                    if (got2.GetElement(1) != e5) owner.Report(Mk("RandomFill", block, p + i * 8 + 40, pass, e5, got2.GetElement(1)), count);
                    if (got2.GetElement(2) != e6) owner.Report(Mk("RandomFill", block, p + i * 8 + 48, pass, e6, got2.GetElement(2)), count);
                    if (got2.GetElement(3) != e7) owner.Report(Mk("RandomFill", block, p + i * 8 + 56, pass, e7, got2.GetElement(3)), count);
                }
                if ((i & 0x3FFFF) == 0) ct.ThrowIfCancellationRequested();
            }
        }
        for (; i < count; i++)
        {
            ulong e = XorShift(ref s0);
            if (q[i] != e) owner.Report(Mk("RandomFill", block, p + i * 8, pass, e, q[i]), count);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong XorShift(ref ulong s)
    {
        s ^= s << 13; s ^= s >> 7; s ^= s << 17;
        return s;
    }

    /// <summary>
    /// 统一的错误记录构造。由失败地址反推块内偏移与绝对地址，期望值必须由调用方给出。
    /// 这两项早期是各模式自己填的，结果只有 AddressAsData 填对，其余全是 0。
    /// </summary>
    private static MemoryError Mk(string pattern, MemoryBlock block, byte* addr, int pass, ulong expected, ulong actual) => new()
    {
        ThreadId = Environment.CurrentManagedThreadId, Pass = pass, Pattern = pattern,
        BlockIndex = block.Index,
        OffsetInBlock = (long)(addr - block.Base),
        AbsoluteAddress = (ulong)addr,
        Expected = expected, Actual = actual
    };

    private static void BlockCopyTest(byte* p, long len, MemoryBlock block, int pass, CancellationToken ct, StressEngine owner)
    {
        long half = len / 2 & ~7L;
        Fill(p, half, 0x3C, nonTemporal: true);
        Buffer.MemoryCopy(p, p + half, half, half);
        // 注入点必须在拷贝**之后**：拷贝会覆盖掉之前写入的目标区，
        // 放在拷贝前篡改等于白改（第一版就是这么错的，检出 0 条）。
        owner.ApplyFault(p + half, half);
        VerifyUniform(p + half, half, 0x3C, block, pass, "BlockCopy", ct, owner);
        // 反向：把后半搬到前半，检查控制器双向一致性
        Fill(p + half, half, 0xC3, nonTemporal: true);
        Buffer.MemoryCopy(p + half, p, half, half);
        VerifyUniform(p, half, 0xC3, block, pass, "BlockCopy", ct, owner);
    }


    /// <summary>向量块发现不一致后逐字定位。<paramref name="p"/> 已是块内绝对地址，报错才能指到真实位置。</summary>
    private static void ScanWordRun(byte* p, int words, ulong expected, MemoryBlock block, int pass, string pattern, StressEngine owner)
    {
        ulong* q = (ulong*)p;
        for (int i = 0; i < words; i++)
            if (q[i] != expected) owner.Report(Mk(pattern, block, p + i * 8, pass, expected, q[i]), words);
    }

    /// <summary>
    /// 棋盘格：交替 0x55/0xAA 填充，抓相邻存储单元耦合故障。
    ///
    /// 必须跑**两种极性**。只跑 0x55/0xAA 时，每个相邻对永远是「奇单元=1、偶单元=0」；
    /// 而耦合故障（一个单元被写入时影响邻居）常常是方向性的——只在邻居为 1 时把 0 拉成 1，
    /// 反向不发生。只测一种极性会漏掉一半的耦合故障。第二趟把两种值互换，覆盖另一个方向。
    /// 这与 MemTest86 的 Test 3 用 ones&zeros 两种模式、Test 4 用 walking ones 和 zeros 同一个道理。
    /// </summary>
    private static void Checkerboard(byte* p, long len, MemoryBlock block, int pass, CancellationToken ct, StressEngine owner)
    {
        ulong* q = (ulong*)p;
        long count = len / 8;
        const ulong Even = 0x5555555555555555UL;
        const ulong Odd = 0xAAAAAAAAAAAAAAAAUL;

        for (int polarity = 0; polarity < 2; polarity++)
        {
            ulong a = polarity == 0 ? Even : Odd;
            ulong b = polarity == 0 ? Odd : Even;
            for (long i = 0; i < count; i++) q[i] = (i & 1) == 0 ? a : b;
        owner.ApplyFault(p, len);   // 注入自检：写完、校验前篡改
            for (long i = 0; i < count; i++)
            {
                ulong expect = (i & 1) == 0 ? a : b;
                if (q[i] != expect) owner.Report(Mk("Checkerboard", block, p + i * 8, pass, expect, q[i]), count);
                if ((i & 0x3FFFF) == 0) ct.ThrowIfCancellationRequested();
            }
            ct.ThrowIfCancellationRequested();
        }
    }

}
