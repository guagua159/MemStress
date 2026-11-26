namespace MemStress.Core;

/// <summary>
/// 故障类型。每种对应真实的 DRAM / IMC 失效模式，依据见各类注释中的 URL。
///
/// 分类学依据：[Memory Fault Models, Complete: From Stuck-At to NPSF](https://www.vlsi.kr/en/memory-fault-models-mbist-matrix-en/)
/// 给出的经典集合是 stuck-at / transition / address decoder / 三类耦合故障（CFst、CFid、CFin）。
/// 本枚举在此基础上补充了总线、突发、间歇三类偏系统侧的失效。
/// </summary>
public enum FaultKind
{
    /// <summary>单字节取反（原有行为，保留兼容）。</summary>
    ByteFlip,
    /// <summary>固定位：某一位恒为 0 或恒为 1。</summary>
    StuckAt,
    /// <summary>单比特翻转：一次读回时某一位取反。</summary>
    BitFlip,
    /// <summary>数据总线 lane 故障：整个 64 位 beat 中同一条 lane 出错。</summary>
    BusLane,
    /// <summary>地址译码故障：写入 A 却出现在 B（别名）。</summary>
    AddressAlias,
    /// <summary>方向性相邻单元耦合：只在邻居为 1 时把目标位拉偏。</summary>
    Coupling,
    /// <summary>整行故障：同一 word-line 上连续一段一起坏。</summary>
    RowStripe,
    /// <summary>多比特突发：连续 8–32 字节同时损坏。</summary>
    Burst,
    /// <summary>间歇性：随机时刻才出现，模拟时序临界。</summary>
    Intermittent
}

/// <summary>
/// 故障注入（仅 --inject-error / --inject-fault 自检使用）。
///
/// 目的：验证**每个模式自己的校验代码**能不能抓到被破坏的数据。
///
/// 为什么需要单独实现（教训）：
/// 早期版本是「写坏一个字节，然后手工构造一条 MemoryError 调 Report()」。
/// 那段代码跟模式完全无关——无论哪个模式、校验逻辑对不对，它都必然产生一条错误记录。
/// 于是「六个模式注入全部检出」这句话虽然是真的，却什么也没证明。
///
/// 现在的做法：各模式在**写完数据、开始校验之前**调用 ApplyFault，
/// 由注入器按故障类型破坏内存，让模式自己的校验去发现。
///
/// 多点注入（关键设计）：单点注入对跨步访问的模式（ModuloX 只校验第 0、20、40… 字节）
/// 几乎必然落空，于是得到「注入了却检出 0 条」的假象。改为在块内**均匀注入多个错点**，
/// 点数按注入粒度取，保证任何子采样模式都会命中至少一个。
/// </summary>
public sealed unsafe partial class StressEngine
{
    private FaultKind _faultKind = FaultKind.ByteFlip;
    private bool _faultPending;
    private int _faultSerial;   // 每轮递增，让间歇故障的出现时机不同

    /// <summary>块内均匀注入的错点数量。足够覆盖任何子采样模式（如 ModuloX 的 1/20 跨步）。</summary>
    private const int MultiPointCount = 64;

    /// <summary>
    /// 写入阶段末尾调用。必须在**校验之前**，否则篡改会被后续写入覆盖。
    /// </summary>
    private void ApplyFault(byte* p, long len)
    {
        if (!_faultPending) return;
        _faultPending = false;
        _faultSerial++;

        switch (_faultKind)
        {
            case FaultKind.ByteFlip: DoByteFlip(p, len); break;
            case FaultKind.StuckAt: DoStuckAt(p, len); break;
            case FaultKind.BitFlip: DoBitFlip(p, len); break;
            case FaultKind.BusLane: DoBusLane(p, len); break;
            case FaultKind.AddressAlias: DoAddressAlias(p, len); break;
            case FaultKind.Coupling: DoCoupling(p, len); break;
            case FaultKind.RowStripe: DoRowStripe(p, len); break;
            case FaultKind.Burst: DoBurst(p, len); break;
            case FaultKind.Intermittent: DoIntermittent(p, len); break;
        }
    }

    /// <summary>在块内均匀取第 i 个注入点（i 从 0 起）。</summary>
    private static long PointAt(long len, int i, int total) =>
        total <= 0 ? 0 : Math.Clamp(len * i / total, 0, Math.Max(0, len - 1));

    /// <summary>原有行为：单字节整体取反。保留以兼容 --inject-error 的历史语义。</summary>
    private void DoByteFlip(byte* p, long len)
    {
        for (int i = 0; i < MultiPointCount; i++) p[PointAt(len, i, MultiPointCount)] ^= 0xFF;
    }

    /// <summary>
    /// 固定位故障（SAF）：单元恒为 0 或恒为 1，写什么都读不回原值。
    /// 依据：[Understanding memory fault models](https://www.embedded.com/understanding-memory-fault-models/)——
    /// 「the logic value of a cell is always 0 or 1」，单元无法保存相反值。
    ///
    /// 极性必须自适应：只做 stuck-at-0 时，若该处原值本来就是 0x00（WalkingOnes/ModuloX 的底色），
    /// `&amp;= 0xFE` 什么也没改，于是得到「注入了却检出 0 条」的假象。
    /// 真实 SAF 两个极性都存在，选一个**确实能改变该字节**的极性才是正确的注入。
    /// </summary>
    private void DoStuckAt(byte* p, long len)
    {
        for (int i = 0; i < MultiPointCount; i++)
        {
            long off = PointAt(len, i, MultiPointCount);
            byte v = p[off];
            // 必须选一个**确实会改变该字节**的极性，否则等于没注入：
            //   v == 0x00 → 注入 stuck-at-1（全 1）
            //   否则       → 注入 stuck-at-0，但清掉的是「确实为 1 的最低位」
            //                （用 v & (v-1) 而不是 v & 0xFE，后者对 0x3C 这类偶数字节无效）
            p[off] = v == 0x00 ? (byte)0xFF : (byte)(v & (v - 1));
        }
    }

    /// <summary>
    /// 单比特翻转：一次读回时某一位取反，模拟漏电 / 保持时间不足。
    /// 依据：[DRAM read disturbance 研究](https://arxiv.org/html/2607.28233v1) 中记录的
    /// 位翻转现象（0→1 与 1→0 都会发生）。
    /// 与 SAF 的区别：翻转是「这一次读到的值与写入的不同」，不是永久固定。
    /// </summary>
    private void DoBitFlip(byte* p, long len)
    {
        for (int i = 0; i < MultiPointCount; i++)
        {
            long off = PointAt(len, i, MultiPointCount);
            p[off] ^= (byte)(1 << (i & 7));   // 每次翻不同的位
        }
    }

    /// <summary>
    /// 数据总线 lane 故障：一个 64 位 beat 里同一条 lane（同一字节位置的同一位）出错。
    /// 依据：[Memory fault models and testing](https://www.edn.com/memory-fault-models-and-testing/)——
    /// 故障可发生在 sense amplifier 或 driver 上，表现为整条数据通道固定。
    /// 这里让块内每个 8 字节 beat 的第 3 字节固定为 0xFF，模拟一条 lane 卡在高电平。
    /// </summary>
    private void DoBusLane(byte* p, long len)
    {
        for (long off = 0; off + 8 <= len; off += 8 * 97)   // 97 与 8 互质，避免总落在同一相位
            p[off + 3] = 0xFF;
    }

    /// <summary>
    /// 地址译码故障：写入地址 A 的数据出现在地址 B（别名）。
    /// 依据：[Memory fault models and testing](https://www.edn.com/memory-fault-models-and-testing/)——
    /// 行/列译码器故障会让不同地址映射到同一存储单元。
    /// 这里把 A 处内容复制到 B，模拟「B 被 A 覆盖」，B 处的原值因此丢失。
    /// </summary>
    private void DoAddressAlias(byte* p, long len)
    {
        if (len < 4096) return;
        for (int i = 0; i < MultiPointCount / 2; i++)
        {
            long a = PointAt(len, i * 2, MultiPointCount);
            long b = PointAt(len, i * 2 + 1, MultiPointCount);
            if (a == b) continue;
            p[b] = p[a];        // B 被 A 的内容覆盖
            p[b] ^= 0xFF;       // 再取反，确保与原值不同
        }
    }

    /// <summary>
    /// 方向性相邻单元耦合故障（CFst）：写邻居时把目标位拉偏，
    /// 且**只在邻居为 1 时发生**——这是现实中最难检出的极性。
    /// 依据：[Memory Fault Models, Complete](https://www.vlsi.kr/en/memory-fault-models-mbist-matrix-en/)——
    /// 耦合故障分 CFst / CFid / CFin，其中 CFst 是「耦合单元的状态固定影响被耦合单元」。
    ///
    /// 为什么要把侵略位强制置 1：故障的定义是「侵略位=1 时受害位被拉偏」。
    /// 若底色本来就是 0（WalkingOnes / ModuloX 的标记位），侵略条件不成立，
    /// 注入就什么都没发生，会得到「检出 0 条」的假象。
    /// 先把侵略位置 1（这正是现实里触发该故障所需的写操作），再拉偏受害位。
    /// 只做单向，因为双向都做会让检测难度虚低。
    /// </summary>
    private void DoCoupling(byte* p, long len)
    {
        for (int i = 0; i < MultiPointCount; i++)
        {
            long off = PointAt(len, i, MultiPointCount);
            long next = off + 1;
            if (next >= len) continue;
            p[off] |= 0x01;      // 侵略位置 1：这是触发 CFst 的前提条件
            p[next] ^= 0x01;     // 受害位被拉偏
        }
    }

    /// <summary>
    /// 整行故障：同一 word-line（同一 DRAM 行）上连续一段一起坏。
    /// 依据：[Revisiting RowHammer](https://arxiv.org/html/2005.13121v2) 指出
    /// 扰动错误在空间上沿行聚集，同一行的多个单元会一起受影响。
    /// 这里用 256 字节的连续段模拟一行内的坏区。
    /// </summary>
    private void DoRowStripe(byte* p, long len)
    {
        const int stripe = 256;
        for (int i = 0; i < MultiPointCount / 4; i++)
        {
            long start = PointAt(len, i * 4, MultiPointCount);
            long end = Math.Min(len, start + stripe);
            for (long off = start; off < end; off++) p[off] ^= 0xFF;
        }
    }

    /// <summary>
    /// 多比特突发错误：连续 8–32 字节同时损坏，模拟 IMC / SoC 侧的不稳定。
    /// 依据：[DRAM read disturbance](https://arxiv.org/html/2607.28233v1) 与
    /// [RowHammer 实验](https://users.ece.cmu.edu/~yoonguk/papers/kim-isca14.pdf) 都记录到
    /// 多比特同时翻转；系统侧（IMC/SoC）不稳定通常表现为整段数据一起错。
    /// 长度取 32 字节，并故意**跨越 32 字节向量边界**，使按向量整体比较的模式必须回退到逐字节定位。
    /// </summary>
    private void DoBurst(byte* p, long len)
    {
        const int burst = 32;
        for (int i = 0; i < MultiPointCount / 4; i++)
        {
            // 起点偏移 24 字节，让 32 字节的坏段横跨两个 32 字节向量
            long start = PointAt(len, i * 4, MultiPointCount) + 24;
            long end = Math.Min(len, start + burst);
            for (long off = start; off < end; off++) p[off] ^= 0xFF;
        }
    }

    /// <summary>
    /// 间歇性故障：只在随机时刻出现，模拟时序临界 / 温度相关的不稳定。
    /// 依据：[Understanding memory fault models](https://www.embedded.com/understanding-memory-fault-models/)——
    /// 间歇故障（intermittent）只在特定条件（电压、温度、时序余量）下出现，是超频不稳的典型表现。
    /// 用 _faultSerial 决定本轮是否注入，保证多轮运行中时有时无。
    /// </summary>
    private void DoIntermittent(byte* p, long len)
    {
        // 约 2/3 的轮次注入，其余轮次干净——「时有时无」才像真实的时序临界
        if (_faultSerial % 3 == 0) return;
        DoBitFlip(p, len);
    }
}
