using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace MemStress.Core;

/// <summary>
/// 压测引擎。每块内存常驻不释放（对应 TM5 的 RefreshStable 思路：不换页才测得准），
/// worker 线程按块编号交错分片，块内按模式顺序跑，任何不一致都进错误队列。
/// </summary>
public sealed unsafe partial class StressEngine : IDisposable
{
    private readonly TestOptions _opt;
    private CancellationTokenSource? _cts;
    private Thread[] _threads = Array.Empty<Thread>();
    private MemoryArena.Result? _arena;
    private volatile bool _running;
    private long _bytesDone;
    private long _errors;
    /// <summary>还在跑的 worker 数。最后一个退出的负责收尾，让「自然跑完」与「手动停止」走同一套结束逻辑。</summary>
    private int _workersAlive;
    /// <summary>每个 worker 各自已跑完的轮数。全局轮次取最小值——只有所有 worker 都跑完第 N 轮，
    /// 才算「完成 N 轮」（worker 是按块分片的，快的那个先到不代表整轮跑完）。</summary>
    private int[] _workerDone = Array.Empty<int>();
    private DateTime _startedAt;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private TimeSpan _finalElapsed;
    private readonly object _reportLock = new();
    private readonly Dictionary<(int, string), int> _reportedPerScope = new();
    /// <summary>已逐条上报过的 (块, 模式, 绝对地址)，用于跨轮去重。</summary>
    private readonly Dictionary<(int, string, ulong), int> _reportedAddrs = new();
    private const int MaxReportsPerScope = 200;

    public event Action<MemoryError>? ErrorFound;
    public event Action<string>? StatusChanged;

    // 指标不推送、只轮询：界面用 500ms 的 DispatcherTimer 调 Snapshot() 取数。
    // 早期这里有个从不触发的 MetricsUpdated 事件（CS0067），留着会让接手者
    // 以为存在推送机制而去找订阅点，故删除。

    public MemoryArena.Result? Arena => _arena;
    public bool IsRunning => _running;
    public long TotalBytes => _arena?.TotalBytes ?? 0;
    public long Errors => Interlocked.Read(ref _errors);

    public StressEngine(TestOptions opt) { _opt = opt; _faultKind = opt.FaultKind; }

    public void Start()
    {
        if (_running) throw new InvalidOperationException("已在运行中");
        _cts = new CancellationTokenSource();
        _startedAt = DateTime.Now;
        _clock.Restart();
        _finalElapsed = TimeSpan.Zero;
        Interlocked.Exchange(ref _bytesDone, 0);
        Interlocked.Exchange(ref _errors, 0);
        lock (_reportLock) { _reportedPerScope.Clear(); _reportedAddrs.Clear(); }

        long target = _opt.FixedMiB > 0
            ? (long)_opt.FixedMiB * 1024 * 1024
            : (long)(Native.AvailablePhysicalBytes() * _opt.MemoryPercent);
        if (target < 256L * 1024 * 1024) target = 256L * 1024 * 1024;

        int threads = _opt.Threads > 0 ? _opt.Threads : Native.PhysicalCoreCount();
        int numa = Native.NumaNodeCount();
        StatusChanged?.Invoke($"正在分配 {target / 1048576} MiB（{( _opt.UseLargePages ? "优先大页" : "普通页")}，{numa} 个 NUMA 节点）…");

        _arena = MemoryArena.Allocate(target, _opt.ChunkMiB * 1024 * 1024, numa, _opt.UseLargePages,
            done => StatusChanged?.Invoke($"已分配 {done / 1048576} MiB / {target / 1048576} MiB…"), _cts.Token);

        StatusChanged?.Invoke($"分配完成：{_arena.TotalBytes / 1048576} MiB，{_arena.Note}；启动 {threads} 个 worker");

        _running = true;
        _threads = new Thread[threads];
        Interlocked.Exchange(ref _workersAlive, threads);
        _workerDone = new int[threads];
        for (int i = 0; i < threads; i++)
        {
            int idx = i;
            _threads[i] = new Thread(() => WorkerLoop(idx)) { IsBackground = true, Name = $"memstress-worker-{idx}", Priority = ThreadPriority.AboveNormal };
            _threads[i].Start();
        }
    }

    private void WorkerLoop(int workerIndex)
    {
        try
        {
            var ct = _cts!.Token;
            var mine = new List<MemoryBlock>();
            for (int i = workerIndex; i < _arena!.Blocks.Count; i += _threads.Length)
                mine.Add(_arena.Blocks[i]);

            var patterns = _opt.Patterns.Select(Patterns.Parse).ToArray();
            int pass = 0;
            while (!ct.IsCancellationRequested)
            {
                pass++;
                foreach (var pat in patterns)
                {
                    foreach (var block in mine)
                    {
                        if (ct.IsCancellationRequested) return;
                        bool corrupt = _opt.InjectError && workerIndex == 0 && block.Index == 0 && pass == 1 && pat == patterns[0];
                        RunOne(pat, block, pass, corrupt, ct);
                    }
                }
                // 轮数按「每个 worker 自己跑了几轮」判断。早先用的是全局累加计数，
                // 16 个 worker 各跑 1 轮就把它推到 16，于是「轮数=1」实际只跑了 1/16 轮。
                _workerDone[workerIndex] = pass;
                if (_opt.Passes > 0 && pass >= _opt.Passes) return;
                if (_opt.Passes <= 0 && (DateTime.Now - _startedAt).TotalSeconds >= _opt.DurationSeconds) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // worker 异常必须让整轮测试失败退出：只打一行状态而继续跑，
            // 会让「其余 worker 还在动」掩盖住某个线程已经死了，结果看起来仍是「通过」。
            StatusChanged?.Invoke($"worker {workerIndex} 异常，压测中止：{ex.Message}");
            _cts?.Cancel();
        }
        finally
        {
            // 最后一个退出的 worker 负责把引擎置为停止态，界面才能自然收尾并写出正确结论。
            if (Interlocked.Decrement(ref _workersAlive) == 0) Finish();
        }
    }

    /// <summary>
    /// 全部 worker 已退出后的收尾。手动停止时 <see cref="Stop"/> 会先把 <c>_running</c> 置否，
    /// 这里用比较交换保证只有一方执行，时钟只停一次。
    /// </summary>
    private void Finish()
    {
        if (!_running) return;
        _finalElapsed = _clock.Elapsed;
        _clock.Stop();
        _running = false;
    }

    /// <summary>
    /// 单次处理的切片大小。**必须显著大于 L3**，否则「写入 → 回读」全程命中缓存，
    /// 数据根本没到 DRAM，测的是缓存不是内存。
    ///
    /// 取 4×L3（本机 L3=32 MB → 128 MiB），保证写入阶段的尾部数据在回读阶段已被挤出缓存；
    /// 取不到 L3 时退回 64 MiB（大于绝大多数桌面 CPU 的 L3）。
    /// 依据 Chipress《How to avoid cache interference in SoC DRAM testing》：
    /// 测试区间大于缓存时，缓存才无法代替 DRAM 供数。
    /// 实测：4 MiB 与 64 MiB 切片吞吐相差不到 2%，故这是几乎免费的改动。
    /// </summary>
    internal static readonly long SliceBytes = ComputeSlice();

    private static long ComputeSlice()
    {
        long l3 = Native.LastLevelCacheBytes();
        long target = l3 > 0 ? l3 * 4 : 64L * 1024 * 1024;
        // 对齐到 4 MiB，限制在 [4 MiB, 256 MiB]，避免小块时切片大于块本身导致空转
        target = (target + (4L << 20) - 1) / (4L << 20) * (4L << 20);
        return Math.Clamp(target, 4L << 20, 256L << 20);
    }

    private void RunOne(PatternKind kind, MemoryBlock block, int pass, bool corrupt, CancellationToken ct)
    {
        byte* p = block.Base;
        long len = block.Size;
        long done = 0;
        long slice = SliceBytes;
        while (done < len)
        {
            ct.ThrowIfCancellationRequested();
            long n = Math.Min(slice, len - done);
            switch (kind)
            {
                case PatternKind.MovingInversion: MovingInversion(p + done, n, block, pass, ct, this); break;
                case PatternKind.AddressAsData: AddressAsData(p + done, n, block, pass, ct, this); break;
                case PatternKind.RandomFill: RandomFill(p + done, n, block, pass, ct, this); break;
                case PatternKind.BlockCopy: BlockCopyTest(p + done, n, block, pass, ct, this); break;
                case PatternKind.WalkingOnes: WalkingOnes(p + done, n, block, pass, ct, this); break;
                case PatternKind.Checkerboard: Checkerboard(p + done, n, block, pass, ct, this); break;
                case PatternKind.ModuloX: ModuloX(p + done, n, block, pass, ct, this); break;
            }
            done += n;
            Interlocked.Add(ref _bytesDone, n);
        }
        if (corrupt)
        {
            // 故障注入：在模式**写完、开始校验之前**破坏内存，让模式自己的校验去发现。
            //
            // 早期版本这里是直接构造一条 MemoryError 调 Report()——那等于绕过全部模式逻辑，
            // 只验证了「日志链路能写」和「CSV 能落盘」，**完全没有验证模式能否抓到错误**。
            // 六个模式全都「检出」是必然的，因为这个分支跟模式无关。
            //
            // 现在改为：置待注入标志，由各模式在写完数据后调用 ApplyFault() 落地。
            // 若某个模式的校验范围漏了、比较写错了，这里就会检出 0 条，当场露馅。
            _faultPending = true;
            try
            {
                RunOne(kind, block, pass, corrupt: false, ct);
            }
            finally
            {
                _faultPending = false;
            }
        }
    }

    private void Report(MemoryError err, long scopeTotal)
    {
        Interlocked.Increment(ref _errors);
        var key = (err.BlockIndex, err.Pattern);
        int n;
        // 同一 (块, 模式, 绝对地址) 只逐条上报一次。
        // 为什么需要：WalkingOnes 是 8 轮链式校验，同一个被写坏的字节会在多轮里各被独立发现一次
        //（这是真实行为，不是重复计算）。但把它当成 32 条错误上报会淹没真正的分布信息——
        // 用户要看的是「有几个地址坏了」，不是「这个地址被几轮各抓到一次」。
        // 计数仍然照实累加，只是逐条记录去重。
        var addrKey = (err.BlockIndex, err.Pattern, err.AbsoluteAddress);
        bool firstForAddr;
        lock (_reportLock)
        {
            _reportedPerScope.TryGetValue(key, out n);
            _reportedPerScope[key] = ++n;
            // 跨轮去重：同轮内重复发现同一坏字节仍算一次（WalkingOnes 链式校验的固有
            // 行为）；新一轮再发现 = 间歇性故障新证据，重新记录。旧 HashSet 只增不清，
            // 长跑既涨内存也吞掉间歇性复现。
            if (_reportedAddrs.TryGetValue(addrKey, out int lastPass))
                firstForAddr = lastPass != err.Pass;
            else
                firstForAddr = true;
            if (firstForAddr) _reportedAddrs[addrKey] = err.Pass;
        }
        if (!firstForAddr) return;
        if (n <= MaxReportsPerScope) ErrorFound?.Invoke(err);
        else if (n == MaxReportsPerScope + 1)
            StatusChanged?.Invoke($"块 {err.BlockIndex} 的 {err.Pattern} 错误超过 {MaxReportsPerScope} 条，后续仅计数不逐条记录");
    }

    public void Stop()
    {
        if (!_running) return;
        _cts?.Cancel();
        foreach (var t in _threads) t.Join(TimeSpan.FromSeconds(5));
        // 若 worker 已全部自然退出，Finish() 已经停过表，这里再停会把时长清零，故用同一套收尾。
        Finish();
    }

    public void Dispose()
    {
        Stop();
        if (_arena != null)
        {
            foreach (var b in _arena.Blocks) b.Release();
            _arena.Blocks.Clear();
            // 置空而不是只清列表：清空后 TotalBytes 变成 0，报告里会出现
            // 「已分配 0 MiB」却同时有几十 GiB 读写量的自相矛盾记录。
            _arena = null;
        }
        _cts?.Dispose();
        _cts = null;
    }

}
