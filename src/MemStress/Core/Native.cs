using System.Runtime.InteropServices;

namespace MemStress.Core;

/// <summary>Win32 互操作：大页分配、NUMA、内存状态、物理核数。</summary>
internal static class Native
{
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint MEM_LARGE_PAGES = 0x20000000;
    public const uint PAGE_READWRITE = 0x04;

    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocExNuma(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize,
        uint flAllocationType, uint flProtect, uint nndPreferred);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr GetLargePageMinimum();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNumaHighestNodeNumber(out uint HighestNodeNumber);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNumaNodeProcessorMaskEx(ushort Node, out GROUP_AFFINITY GroupMask);

    [StructLayout(LayoutKind.Sequential)]
    public struct GROUP_AFFINITY
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;

    /// <summary>可用物理内存（字节）。</summary>
    public static ulong AvailablePhysicalBytes()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref ms) ? ms.ullAvailPhys : 0;
    }

    public static ulong TotalPhysicalBytes()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref ms) ? ms.ullTotalPhys : 0;
    }

    /// <summary>开启「锁定页面」特权，大页分配的前提。</summary>
    public static bool TryEnableLockPagesPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeLockMemoryPrivilege", out LUID luid))
                return false;
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
            };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;
            // AdjustTokenPrivileges 在权限未授予时返回 true 但 GetLastError == ERROR_NOT_ALL_ASSIGNED(1300)
            return Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    public static uint LargePageMinimum => (uint)GetLargePageMinimum().ToUInt64();

    public static int NumaNodeCount()
    {
        return GetNumaHighestNodeNumber(out uint highest) ? (int)highest + 1 : 1;
    }

    /// <summary>物理核数量（非超线程数）。</summary>
    public static int PhysicalCoreCount()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0) return Math.Max(1, Environment.ProcessorCount / 2);
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len))
                    return Math.Max(1, Environment.ProcessorCount / 2);
                int count = 0;
                int offset = 0;
                while (offset < len)
                {
                    int rel = Marshal.ReadInt32(buf, offset);
                    int size = Marshal.ReadInt32(buf, offset + 4);
                    if (size <= 0) break;
                    if (rel == RelationProcessorCore) count++;
                    offset += size;
                }
                return Math.Max(1, count);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return Math.Max(1, Environment.ProcessorCount / 2); }
    }

    /// <summary>
    /// 最后一级缓存（L3）的大小，字节。取不到时返回 0。
    ///
    /// 为什么要这个数：压测的「写入后回读」若切片小于 L3，回读会命中缓存而**根本没碰到 DRAM**，
    /// 于是测的是缓存不是内存——缺陷检测直接失效，但吞吐数字反而更漂亮。
    /// 依据 Chipress《How to avoid cache interference in SoC DRAM testing》：
    /// 测试区间必须大于缓存容量，否则缓存会代替 DRAM 供数。
    /// </summary>
    public static long LastLevelCacheBytes()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref len);
            if (len == 0) return 0;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationCache, buf, ref len)) return 0;
                long max = 0;
                int offset = 0;
                while (offset < len)
                {
                    int rel = Marshal.ReadInt32(buf, offset);
                    int size = Marshal.ReadInt32(buf, offset + 4);
                    if (size <= 0) break;
                    if (rel == RelationCache)
                    {
                        // CACHE_DESCRIPTOR：Level(1B) @+8, Associativity @+9, LineSize(2B) @+10, CacheSize(4B) @+12
                        byte level = Marshal.ReadByte(buf, offset + 8);
                        int cacheSize = Marshal.ReadInt32(buf, offset + 12);
                        if (level >= 3 && cacheSize > max) max = cacheSize;
                    }
                    offset += size;
                }
                return max;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return 0; }
    }
}
