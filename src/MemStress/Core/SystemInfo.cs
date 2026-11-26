using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MemStress.Core;

/// <summary>
/// 机器信息采集。内存条信息走 Win32_PhysicalMemory（SMBIOS 内存设备表）——它比 WMI 的
/// Win32_MemoryDevice 更可靠，也是 RunMemtestStartup 采用 SMBIOS 而非 CPU-Z 的同一个理由：
/// 免管理员、即时可用。采集失败不阻断压测，字段留空并在报告里标注。
/// </summary>
public static class SystemInfo
{
    public sealed class MemModule
    {
        public string Locator { get; set; } = "";
        public long CapacityBytes { get; set; }
        public int Speed { get; set; }
        public int ConfiguredSpeed { get; set; }
        public string Manufacturer { get; set; } = "";
        public string PartNumber { get; set; } = "";
    }

    /// <summary>
    /// 采集机器信息。
    /// <para>
    /// <paramref name="skipSlow"/> 为 true 时跳过 4 次 PowerShell/WMI 查询（CPU 型号、主板、BIOS、内存条），
    /// 只保留 BCL 与 Win32 能直接拿到的字段。这 4 次查询本机实测约 2.1 秒，占了
    /// `--selftest` 单次运行的 40%——63 格故障注入矩阵因此白花 2 分钟。
    /// 矩阵只关心「这一格能否检出注入的错误」，不关心机器型号，所以可以跳过。
    /// 代价是报告里的 cpu/mainboard/bios/memoryModules 为空，因此只在明确知道不需要时传 true。
    /// </para>
    /// </summary>
    public static JsonObject Collect(bool skipSlow = false)
    {
        var o = new JsonObject
        {
            ["app"] = "MemStress",
            ["version"] = typeof(SystemInfo).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            ["sessionStart"] = DateTime.Now.ToString("O"),
            ["machineName"] = Environment.MachineName,
            ["os"] = Environment.OSVersion.VersionString,
            ["osBuild"] = Environment.OSVersion.Version.Build,
            ["dotnet"] = Environment.Version.ToString(),
            ["processorCount"] = Environment.ProcessorCount,
            ["physicalCores"] = Native.PhysicalCoreCount(),
            ["numaNodes"] = Native.NumaNodeCount(),
            ["totalPhysicalMiB"] = (long)(Native.TotalPhysicalBytes() / 1048576),
            ["availablePhysicalMiB"] = (long)(Native.AvailablePhysicalBytes() / 1048576),
            ["largePageMinimumKB"] = Native.LargePageMinimum / 1024,
            ["isElevated"] = IsElevated(),
            ["is64BitProcess"] = Environment.Is64BitProcess
        };

        if (skipSlow)
        {
            // 只保留 BCL / Win32 能直接拿到的字段。cpu 与 memoryModules 留空，
            // 报告里会显示为未知——矩阵测试不读这两个字段。
            o["memoryModules"] = new JsonArray();
            return o;
        }

        var cpu = Query("Get-CimInstance Win32_Processor | Select-Object -First 1 Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed");
        if (cpu != null)
        {
            o["cpu"] = (cpu["Name"]?.ToString() ?? "").Trim();
            o["cpuCores"] = cpu["NumberOfCores"]?.GetValue<int>() ?? 0;
            o["cpuThreads"] = cpu["NumberOfLogicalProcessors"]?.GetValue<int>() ?? 0;
            o["cpuMaxMHz"] = cpu["MaxClockSpeed"]?.GetValue<int>() ?? 0;
        }

        var board = Query("Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer,Product");
        if (board != null) o["mainboard"] = $"{board["Manufacturer"]} {board["Product"]}".Trim();

        var bios = Query("Get-CimInstance Win32_BIOS | Select-Object SMBIOSBIOSVersion,ReleaseDate");
        if (bios != null) o["bios"] = bios["SMBIOSBIOSVersion"]?.ToString() ?? "";

        var mods = new JsonArray();
        var mem = Query("Get-CimInstance Win32_PhysicalMemory | Select-Object BankLabel,DeviceLocator,Capacity,Speed,ConfiguredClockSpeed,Manufacturer,PartNumber");
        if (mem is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not JsonObject m) continue;
                mods.Add(new JsonObject
                {
                    ["slot"] = m["DeviceLocator"]?.ToString() ?? m["BankLabel"]?.ToString() ?? "",
                    ["capacityMiB"] = (long)(m["Capacity"]?.GetValue<long>() ?? 0) / 1048576,
                    ["ratedSpeed"] = m["Speed"]?.GetValue<int>() ?? 0,
                    ["configuredSpeed"] = m["ConfiguredClockSpeed"]?.GetValue<int>() ?? 0,
                    ["manufacturer"] = (m["Manufacturer"]?.ToString() ?? "").Trim(),
                    ["partNumber"] = (m["PartNumber"]?.ToString() ?? "").Trim()
                });
            }
        }
        o["memoryModules"] = mods;
        return o;
    }

    private static JsonNode? Query(string psCommand)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psCommand.Replace("\"", "\\\"")} | ConvertTo-Json -Compress -Depth 4\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string json = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(15000);
            if (string.IsNullOrWhiteSpace(json) || !json.StartsWith('{') && !json.StartsWith('[')) return null;
            return JsonNode.Parse(json);
        }
        catch { return null; }
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
