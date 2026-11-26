using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemStress.Core;

public enum AllocationKind { LargePages, Normal }

/// <summary>一块已分配并可直接读写的测试内存。</summary>
public sealed unsafe class MemoryBlock
{
    public int Index { get; init; }
    public byte* Base { get; init; }
    public long Size { get; init; }
    public AllocationKind Kind { get; init; }
    public int NumaNode { get; init; }
    private int _released;

    /// <summary>
    /// 释放本块。可重复调用，且并发调用安全——用 Interlocked 抢释放权。
    ///
    /// 这不是防御性编程：GUI 里「停止」会在后台线程 Dispose 引擎，而关窗时主线程又会 Dispose 一次，
    /// 两条路径都会走到这里。原先是 `if (Released) return;` 的 check-then-act，
    /// 两个线程同时通过检查就会对同一地址调用两次 VirtualFree——第二次释放的是已被回收的地址，
    /// 在开了「终止堆损坏」的调试环境里会直接崩。
    /// </summary>
    public void Release()
    {
        if (Base == null) return;
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        Native.VirtualFree((IntPtr)Base, UIntPtr.Zero, Native.MEM_RELEASE);
    }

    public bool Released => Volatile.Read(ref _released) != 0;
}

/// <summary>
/// 内存分配器：优先大页（每块按大页尺寸对齐），失败或权限不足时整体回退到普通页。
/// 全部大页失败的原因通常是「锁定页面」特权未授予——该权限在客户端 Windows 默认是「未定义」，
/// 需在 secpol.msc 里授权且下次登录生效；AdjustTokenPrivileges 返回 ERROR_NOT_ALL_ASSIGNED 就是直接信号。
/// </summary>
public static unsafe class MemoryArena
{
    public sealed class Result
    {
        public List<MemoryBlock> Blocks { get; } = new();
        public AllocationKind Kind { get; set; } = AllocationKind.Normal;
        public string Note { get; set; } = "";
        public long TotalBytes => Blocks.Sum(b => b.Size);
    }

    /// <summary>
    /// 按块分配。largePageFirst=true 时先试大页；任何一块大页失败则全部释放改用普通页，
    /// 避免「一半大页一半 4K」造成覆盖率不一致却不自知。
    /// </summary>
    public static Result Allocate(long totalBytes, int chunkBytes, int numaNodes, bool largePageFirst,
        Action<long>? progress = null, CancellationToken ct = default)
    {
        chunkBytes = Math.Max(chunkBytes, 1 << 20);
        int chunkCount = (int)Math.Max(1, totalBytes / chunkBytes);

        if (largePageFirst)
        {
            uint lpMin = Native.LargePageMinimum;
            if (lpMin > 0 && Native.TryEnableLockPagesPrivilege())
            {
                // 大页要求单次分配大小是 lpMin 的整数倍。早先只把「单块大小」向上对齐，
                // 块数却仍按未对齐的块大小算，于是块大小 < lpMin 时总量直接翻倍
                // （实测：请求 1216 MiB / 1 MiB 块 → 实际分配 2432 MiB）。
                // 这里先把块大小对齐，再用对齐后的大小算块数，总量才对得上。
                int alignedChunk = (int)AlignUp(chunkBytes, lpMin);
                int alignedCount = (int)Math.Max(1, totalBytes / alignedChunk);
                var large = TryAllocateChunks(alignedChunk, alignedCount, numaNodes, lpMin, progress, ct, out string err);
                if (large != null)
                {
                    large.Kind = AllocationKind.LargePages;
                    large.Note = $"大页 {lpMin / 1024} KB，共 {large.Blocks.Count} 块";
                    return large;
                }
                // 大页失败，回退
                var normal = TryAllocateChunks(chunkBytes, chunkCount, numaNodes, 0, progress, ct, out string err2);
                if (normal == null) throw new OutOfMemoryException($"普通页分配也失败：{err2}");
                normal.Kind = AllocationKind.Normal;
                normal.Note = $"大页不可用：{err}；已回退 4 KB 页";
                return normal;
            }
            var fb = TryAllocateChunks(chunkBytes, chunkCount, numaNodes, 0, progress, ct, out string err3);
            if (fb == null) throw new OutOfMemoryException($"分配失败：{err3}");
            fb.Kind = AllocationKind.Normal;
            fb.Note = "大页不可用（未授予「锁定页面」特权，secpol.msc → 本地策略 → 用户权限分配 → 锁定页面），已回退 4 KB 页";
            return fb;
        }

        var plain = TryAllocateChunks(chunkBytes, chunkCount, numaNodes, 0, progress, ct, out string e4);
        if (plain == null) throw new OutOfMemoryException($"分配失败：{e4}");
        plain.Kind = AllocationKind.Normal;
        plain.Note = "按配置使用 4 KB 页";
        return plain;
    }

    private static Result? TryAllocateChunks(int chunkBytes, int chunkCount, int numaNodes, uint align,
        Action<long>? progress, CancellationToken ct, out string error)
    {
        error = "";
        var result = new Result();
        long size = align > 1 ? AlignUp(chunkBytes, align) : chunkBytes;
        long done = 0;
        try
        {
            for (int i = 0; i < chunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                uint flags = Native.MEM_COMMIT | Native.MEM_RESERVE | (align > 1 ? Native.MEM_LARGE_PAGES : 0);
                int node = numaNodes > 1 ? i % numaNodes : 0;
                IntPtr p = Native.VirtualAllocExNuma(Native.GetCurrentProcess(), IntPtr.Zero, (UIntPtr)(ulong)size,
                    flags, Native.PAGE_READWRITE, (uint)node);
                if (p == IntPtr.Zero)
                {
                    int code = Marshal.GetLastWin32Error();
                    error = $"VirtualAllocExNuma 失败，Win32 错误 {code}（{DescribeWin32(code)}），已分配 {done / 1048576} MiB";
                    foreach (var b in result.Blocks) b.Release();
                    result.Blocks.Clear();
                    return null;
                }
                var block = new MemoryBlock { Index = i, Base = (byte*)p, Size = size, NumaNode = node, Kind = align > 1 ? AllocationKind.LargePages : AllocationKind.Normal };
                result.Blocks.Add(block);
                done += size;
                progress?.Invoke(done);
            }
            return result;
        }
        catch (Exception)
        {
            // 大页一旦提交就只能显式释放。这里原先只捕 OperationCanceledException，
            // 中途抛出的其它异常（List 扩容失败等）会把已提交的大页永久漏掉——
            // 大页是系统级稀缺资源，泄漏后连下次分配都会失败。一律先还再抛。
            foreach (var b in result.Blocks) b.Release();
            result.Blocks.Clear();
            throw;
        }
    }

    private static long AlignUp(long v, uint align) => (v + align - 1) / align * align;

    public static string DescribeWin32(int code) => code switch
    {
        5 => "拒绝访问（大页需要「锁定页面」特权）",
        8 => "内存不足",
        87 => "参数错误（大页要求分配大小与地址均为大页倍数）",
        1314 => "权限不足（缺少 SeLockMemoryPrivilege）",
        1450 => "系统资源不足：大页是系统级稀缺资源，已被其他进程占满；可关闭大页重试，或先退出占用大页的程序",
        1455 => "页面文件太小",
        _ => "未知"
    };
}
