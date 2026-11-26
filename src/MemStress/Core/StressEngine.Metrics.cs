namespace MemStress.Core;

/// <summary>
/// 压测引擎的指标部分：单轮有效覆盖量、轮次、进度。
/// 拆出来是因为 StressEngine.cs 原本已逼近 300 行上限（见 MAINTENANCE.md「文件规模」），
/// 而这几项全是只读的派生计算，与线程调度无关，改动风险低。
/// </summary>
public sealed unsafe partial class StressEngine
{
    /// <summary>
    /// 单轮有效覆盖字节 = 块总量 × 模式数。所有模式现在都覆盖整块，
    /// 不再有 WalkingOnes 只采样 32 MiB 的特例（那个特例是它 64 位 × 32 MiB 实现留下的，
    /// 现已改为 8 位 × 整块，工作量不变而覆盖率提升 8 倍，详见 StressEngine.Patterns.cs）。
    /// </summary>
    private long PassEffectiveBytes()
    {
        long perBlock = _arena?.TotalBytes ?? 0;
        return Math.Max(1, perBlock * _opt.Patterns.Length);
    }

    public Metrics Snapshot()
    {
        long passTotal = PassEffectiveBytes();
        long done = Interlocked.Read(ref _bytesDone);
        return new Metrics
        {
            Elapsed = _running ? _clock.Elapsed : _finalElapsed,
            BytesDone = done,
            BytesTotal = passTotal,
            Errors = Interlocked.Read(ref _errors),
            // 轮次取所有 worker 的最小值：按块分片时快的 worker 会先跑完，
            // 用 done/passTotal 估算会把「第 1 轮跑了 60%」显示成 0 轮，进度条与轮次互相矛盾。
            Pass = CompletedPasses(),
            ActivePatterns = _opt.Patterns
        };
    }

    /// <summary>已完成轮数 = 所有 worker 都跑完的轮数（取最小值）。</summary>
    private int CompletedPasses()
    {
        var arr = _workerDone;
        if (arr.Length == 0) return 0;
        int min = int.MaxValue;
        for (int i = 0; i < arr.Length; i++) { int v = Volatile.Read(ref arr[i]); if (v < min) min = v; }
        return min == int.MaxValue ? 0 : min;
    }
}
