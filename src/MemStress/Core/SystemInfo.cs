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

    public static JsonObject Collect()
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
