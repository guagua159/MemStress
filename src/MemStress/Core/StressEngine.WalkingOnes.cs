using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace MemStress.Core;

/// <summary>
/// 「走 1」模式族：WalkingOnes 及其链式遍历原语。
/// 从 StressEngine.Patterns.cs 拆出（该文件涨到 350 行，超过 300 行上限）。
/// 职责边界：这里只处理「8 个位位置轮流置位」这一种故障模型，不涉及其他模式。
/// </summary>
public sealed unsafe partial class StressEngine
{
    /// <summary>
    /// 走 1：单个比特位在字节内依次置位，压位线故障、相邻位串扰、以及「某一位恒为 0/1」的硬故障。
    ///
    /// 关键取舍（依据 MemTest86 官方算法说明）：
    /// 其 Test 4 用「8 bit 宽度的 walking ones/zeros」，注释明确说这样才能查出宽位内存芯片的细微错误；
    /// 而 DRAM 芯片的数据宽度普遍只有 4–16 位，无法单独读写某一位，只能靠模式覆盖相邻位的全部 0/1 组合。
    /// 本项目原先是 64 个位值 × 每块前 32 MiB，工作量 2 GiB 却只覆盖 0.03 GiB——因为 64 位 walk 的
    /// 高 32 位在 byte 级毫无区别，等于把同一件事重复了 64 遍。
    ///
    /// 第二阶段改为 8 个位值 × 整块，覆盖率提升 8 倍；但每个位位置仍是「写满整块 → 再整块校验」，
    /// 8 个位位置 = 16 次全块遍历，实测占 deep 档 40% 的时间。
    ///
    /// 本轮改为**链式**：位位置 b 的遍历校验 b-1 的图案、同时写入 b 的图案，
    /// 只需 1 次初始填充 + 8 次遍历 + 1 次收尾校验 = 10 次遍历。
    /// 检测力不降反升：写 i 时把 i-1 带坏，下一次遍历会在 i-1 处当场发现；
    /// 而「写满再查」会让坏值被后续写入覆盖，故障消失。
    /// 这与 MemTest86 的 Test 4（移动反转 + 8 位走 1）思路一致：边走边校验，而不是分两趟。
    /// </summary>
    private static void WalkingOnes(byte* p, long len, MemoryBlock block, int pass, CancellationToken ct, StressEngine owner)
    {
        long bytes = len & ~7L;
        Fill(p, bytes, 0x00, nonTemporal: true);
        owner.ApplyFault(p, bytes);   // 注入自检：写完、校验前篡改
        byte prev = 0x00;
        for (int bit = 0; bit < 8; bit++)
        {
            ct.ThrowIfCancellationRequested();
            byte cur = (byte)(1 << bit);
            WalkSet(p, bytes, prev, cur, block, pass, ct, owner);
            prev = cur;
        }
        VerifyUniform(p, bytes, prev, block, pass, "WalkingOnes", ct, owner);
    }

    /// <summary>
    /// 逐位置「校验期望值 → 写入新值」。与 WalkInvert 的区别是写入的不是补码而是指定值，
    /// 这样才能在 8 个走 1 图案之间链式传递，省掉每个图案各自一次的整块填充。
    /// </summary>
    private static void WalkSet(byte* p, long bytes, byte expected, byte next,
        MemoryBlock block, int pass, CancellationToken ct, StressEngine owner)
    {
        long i = 0;
        {
            var vExp = Vector256.Create(expected);
            var vNext = Vector256.Create(next);
            // 与 VerifyUniform 一样一次展开 4 个向量：单向量循环的循环开销会吃掉带宽
            for (; i + 128 <= bytes; i += 128)
            {
                var m0 = Unsafe.Read<Vector256<byte>>(p + i);
                var m1 = Unsafe.Read<Vector256<byte>>(p + i + 32);
                var m2 = Unsafe.Read<Vector256<byte>>(p + i + 64);
                var m3 = Unsafe.Read<Vector256<byte>>(p + i + 96);
                bool ok0 = Vector256.EqualsAll(m0, vExp), ok1 = Vector256.EqualsAll(m1, vExp);
                bool ok2 = Vector256.EqualsAll(m2, vExp), ok3 = Vector256.EqualsAll(m3, vExp);
                Unsafe.Write(p + i, vNext); Unsafe.Write(p + i + 32, vNext);
                Unsafe.Write(p + i + 64, vNext); Unsafe.Write(p + i + 96, vNext);
                // 只在失配时才走慢路径，正常情况下热循环里没有分支开销
                if (!ok0) ScanByteRun(p + i, 32, expected, block, pass, "WalkingOnes", owner);
                if (!ok1) ScanByteRun(p + i + 32, 32, expected, block, pass, "WalkingOnes", owner);
                if (!ok2) ScanByteRun(p + i + 64, 32, expected, block, pass, "WalkingOnes", owner);
                if (!ok3) ScanByteRun(p + i + 96, 32, expected, block, pass, "WalkingOnes", owner);
                if ((i & 0x3FFFF) == 0) ct.ThrowIfCancellationRequested();
            }
        }
        for (; i < bytes; i++)
        {
            if (p[i] != expected) owner.Report(Mk("WalkingOnes", block, p + i, pass, expected, p[i]), bytes);
            p[i] = next;
        }
    }
}
