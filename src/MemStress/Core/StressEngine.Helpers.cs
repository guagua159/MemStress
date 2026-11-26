using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace MemStress.Core;

/// <summary>
/// 模式内核共用的底层写校验原语：整块填充、整块校验、向量块失配后逐字节定位。
/// 从 StressEngine.Patterns.cs 拆出（该文件涨到 327 行，超过 300 行上限，见 MAINTENANCE.md）。
/// 这里的函数全部是纯读写原语，不含任何故障模型语义，改它们要同步考虑所有六个模式。
/// </summary>
public sealed unsafe partial class StressEngine
{
    // ---------- 底层写校验 ----------

    /// <summary>
    /// 整块填充。<paramref name="nonTemporal"/> 为 true 时用非临时写（绕过缓存直写内存）。
    ///
    /// 依据 [non-temporal stores 的用途](https://www.nic.uoregon.edu/~khuck/ts/acumem-report/manual_html/ch05s03.html)：
    /// 「拷贝大到装不进缓存的内存区域时，用普通写会白白浪费缓存带宽」；
    /// 以及 [LPC 的实测](https://lpc.events/event/18/contributions/1958/attachments/1467/3160/Experiments%20with%20non-temporal%20writes%20and%20application%20architectures%20final.pdf)：
    /// 非临时写直接把数据写进内存、不打扰缓存，可降低内存带宽消耗。
    ///
    /// 这里只对「整块写一次、马上要回读」的填充路径启用：写阶段不占缓存，
    /// 回读阶段就只能从 DRAM 取——既省带宽，又让校验真正落到内存上，而不是在缓存里自说自话。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Fill(byte* p, long len, byte value, bool nonTemporal = false)
    {
        long i = 0;
        {
            var v = Vector256.Create(value);
            if (nonTemporal)
            {
                // 非临时写要求目标地址按 32 字节对齐；块首来自 VirtualAlloc，天然 64 KiB 对齐。
                for (; i + 128 <= len; i += 128)
                {
                    Vector256.StoreAlignedNonTemporal(v, p + i);
                    Vector256.StoreAlignedNonTemporal(v, p + i + 32);
                    Vector256.StoreAlignedNonTemporal(v, p + i + 64);
                    Vector256.StoreAlignedNonTemporal(v, p + i + 96);
                }
            }
            else
            {
                for (; i + 128 <= len; i += 128)
                {
                    Unsafe.Write(p + i, v); Unsafe.Write(p + i + 32, v);
                    Unsafe.Write(p + i + 64, v); Unsafe.Write(p + i + 96, v);
                }
            }
        }
        for (; i < len; i++) p[i] = value;
    }

    private static void VerifyUniform(byte* p, long len, byte value, MemoryBlock block, int pass, string pattern, CancellationToken ct, StressEngine owner)
    {
        long i = 0;
        {
            var v = Vector256.Create(value);
            for (; i + 128 <= len; i += 128)
            {
                var m0 = Unsafe.Read<Vector256<byte>>(p + i);
                var m1 = Unsafe.Read<Vector256<byte>>(p + i + 32);
                var m2 = Unsafe.Read<Vector256<byte>>(p + i + 64);
                var m3 = Unsafe.Read<Vector256<byte>>(p + i + 96);
                if (!Vector256.EqualsAll(m0, v)) ScanByteRun(p + i, 32, value, block, pass, pattern, owner);
                if (!Vector256.EqualsAll(m1, v)) ScanByteRun(p + i + 32, 32, value, block, pass, pattern, owner);
                if (!Vector256.EqualsAll(m2, v)) ScanByteRun(p + i + 64, 32, value, block, pass, pattern, owner);
                if (!Vector256.EqualsAll(m3, v)) ScanByteRun(p + i + 96, 32, value, block, pass, pattern, owner);
                if ((i & 0x3FFFF) == 0) ct.ThrowIfCancellationRequested();
            }
        }
        for (; i < len; i++)
            if (p[i] != value) owner.Report(Mk(pattern, block, p + i, pass, value, p[i]), len / 8);
    }

    /// <summary>
    /// 向量块发现不一致后逐字节定位。<paramref name="p"/> 已经是块内的绝对指针，
    /// 早期版本这里只传了 run 内下标，报出的偏移永远落在块开头附近，定位不到真实位置。
    /// </summary>
    private static void ScanByteRun(byte* p, int len, byte value, MemoryBlock block, int pass, string pattern, StressEngine owner)
    {
        for (int i = 0; i < len; i++)
            if (p[i] != value) owner.Report(Mk(pattern, block, p + i, pass, value, p[i]), len);
    }
}
