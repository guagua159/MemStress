using System.Diagnostics;
using System.Text.Json.Nodes;

namespace MemStress.Core;

/// <summary>
/// WHEA 监控。业界共识：压测零错误但有 WHEA-Logger 事件，系统仍然判为不稳定。
/// 事件语义：17 = 已纠正的硬件错误，最常见于 PCIe Root Port；47 = 已纠正的内存/机器检查
/// （Component: Memory，DIMM 定位藏在 CPER 结构里）；18/46 是不可纠正版本。
/// 这是纯内存校验类工具最常被漏掉的一环。
/// 实现方式：把查询脚本写到临时 .ps1 再调用，避免命令行里嵌引号被转义吃掉（wevtutil 的 /q 就栽在这里）。
/// </summary>
public sealed class WheaMonitor : IDisposable
{
    private const string Provider = "Microsoft-Windows-WHEA-Logger";
    private readonly System.Threading.Timer _timer;
    private readonly List<WheaEvent> _events = new();
    private readonly object _lock = new();
    private DateTime _since;
    private bool _disposed;
    private bool _queryable = true;
    private string? _scriptPath;
    /// <summary>挂起（睡眠）时刻。唤醒时用它把查询窗口接上，睡眠段的事件才不漏。</summary>
    private DateTime _suspendAt;

    public IReadOnlyList<WheaEvent> Events { get { lock (_lock) return _events.ToList(); } }
    /// <summary>事件日志是否可读。不可读时报告里要写明「未监控」，不能写成「无事件」。</summary>
    public bool Queryable => _queryable;
    public int NewEventCount { get { lock (_lock) return _events.Count; } }
    public event Action<WheaEvent>? WheaFound;

    public WheaMonitor()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
        // 睡眠唤醒补偿（audit P3-2）：挂起时轮询也挂起，唤醒后若只从「现在-2s」查起，
        // 睡眠段+唤醒瞬间的事件就要等下一拍甚至查不到（TimeCreated 在挂起段之后才被
        // 写入的场景）。SystemEvents 是 .NET 标准订阅点，无 Win32 依赖。
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            _suspendAt = DateTime.Now;
        }
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            // 把查询窗口拨回「挂起前 2 秒」：睡眠期间系统时钟不走但事件日志的
            // TimeCreated 用的是唤醒后校正过的时间，两条时间线的交接处最易漏。
            lock (_lock)
            {
                if (_suspendAt != default && _suspendAt < _since) _since = _suspendAt.AddSeconds(-2);
            }
            // 立刻补一拍，不等下一个 15s 周期
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Poll());
        }
    }

    public sealed class WheaEvent
    {
        public DateTime Time { get; init; }
        public int EventId { get; init; }
        public string Level { get; init; } = "";
        public string Message { get; init; } = "";
        /// <summary>事件日志里的记录号，去重的主要依据。取不到时为 0。</summary>
        public long RecordId { get; init; }
    }

    /// <summary>记录起始时间，之后只报告新事件——跑之前就有的旧事件不算本次测试的责任。</summary>
    public void Start()
    {
        lock (_lock) { _events.Clear(); _since = DateTime.Now.AddSeconds(-2); }
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>同步探测一次，供自检使用。</summary>
    public void PollNow() => Poll();

    private string ScriptPath
    {
        get
        {
            // 测试钩子：指向一个输出合成事件的脚本，用来验证「查询 → JSON → 解析 → 过滤 → 落盘」整条链，
            // 因为真实机器上未必有 WHEA 事件可供验证。生产路径不会设置这个变量。
            string? overridePath = Environment.GetEnvironmentVariable("MEMSTRESS_WHEA_SCRIPT");
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

            if (_scriptPath != null) return _scriptPath;
            string path = Path.Combine(Path.GetTempPath(), "memstress-whea.ps1");
            File.WriteAllText(path, """
$ErrorActionPreference = 'Stop'
try {
  $ev = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger'} -MaxEvents 20 -ErrorAction Stop
} catch {
  # 区分「查不到事件」与「查不了事件日志」：
  # NoMatchingEventsFound 说明日志可读、只是没有 WHEA 事件（正常的「无事件」）；
  # 其余异常（权限不足、日志服务未启动、筛选器语法错）属于「未监控」，
  # 必须如实上报，绝不能伪装成 events:[] 让上层判成「零错误」。
  if ($_.FullyQualifiedErrorId -like '*NoMatchingEventsFound*') {
    Write-Output '{"ok":true,"events":[]}'
  } else {
    $msg = ($_.Exception.Message -replace '\\s+', ' ')
    Write-Output ('{"ok":false,"error":"' + $msg.Replace('"', "'") + '"}')
  }
  exit 0
}
$out = @()
foreach ($e in $ev) {
  $out += [pscustomobject]@{
    t = $e.TimeCreated.ToString('o')
    id = $e.Id
    level = $e.LevelDisplayName
    msg = ($e.Message -replace '\s+', ' ')
    rid = $e.RecordId
  }
}
ConvertTo-Json -InputObject @{ ok = $true; events = $out } -Compress -Depth 4
""", new System.Text.UTF8Encoding(true));
            _scriptPath = path;
            return path;
        }
    }

    private void Poll()
    {
        if (_disposed) return;
        try
        {
            var events = QueryEvents();
            if (events == null) { _queryable = false; return; }
            _queryable = true;
            foreach (var ev in events)
            {
                if (ev.Time < _since) continue;
                bool isNew;
                lock (_lock)
                {
                    // 去重优先用事件日志记录号（唯一且稳定）。没有记录号时退回「时间+ID+消息前 80 字」，
                    // 因为同一秒内可能有多条同 ID 事件，只用「时间+ID」会误判成同一条而漏报。
                    isNew = ev.RecordId > 0
                        ? _events.All(x => x.RecordId != ev.RecordId)
                        : _events.All(x => x.Time != ev.Time || x.EventId != ev.EventId || !SameHead(x.Message, ev.Message));
                    if (isNew) _events.Add(ev);
                }
                if (isNew) WheaFound?.Invoke(ev);
            }
        }
        catch { _queryable = false; }
    }

    private static bool SameHead(string a, string b) =>
        string.CompareOrdinal(a.Length > 80 ? a[..80] : a, b.Length > 80 ? b[..80] : b) == 0;

    private List<WheaEvent>? QueryEvents()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        using var p = Process.Start(psi);
        if (p == null) return null;
        string json = p.StandardOutput.ReadToEnd().Trim();
        string err = p.StandardError.ReadToEnd().Trim();
        p.WaitForExit(15000);
        if (json.Length == 0)
        {
            if (err.Length > 0) return null;
            return new List<WheaEvent>();
        }

        var node = JsonNode.Parse(json);
        if (node is not JsonObject root) return null;
        if (root["ok"] is not JsonValue okv || !okv.TryGetValue<bool>(out bool ok) || !ok) return null;
        var list = new List<WheaEvent>();
        if (root["events"] is JsonArray arr)
        {
            foreach (var it in arr)
            {
                if (it is not JsonObject o) continue;
                if (!DateTime.TryParse(o["t"]?.ToString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var time))
                    time = DateTime.Now;
                list.Add(new WheaEvent
                {
                    Time = time.ToLocalTime(),
                    EventId = o["id"]?.GetValue<int>() ?? 0,
                    Level = o["level"]?.ToString() ?? "",
                    Message = Truncate(o["msg"]?.ToString() ?? "", 400),
                    RecordId = long.TryParse(o["rid"]?.ToString(), out long rid) ? rid : 0
                });
            }
        }
        return list;
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    public JsonArray ToJson()
    {
        var arr = new JsonArray();
        foreach (var e in Events)
            arr.Add(new JsonObject { ["time"] = e.Time.ToString("O"), ["eventId"] = e.EventId, ["level"] = e.Level, ["message"] = e.Message });
        return arr;
    }

    public void Dispose()
    {
        _disposed = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Dispose();
        try { if (_scriptPath != null && File.Exists(_scriptPath)) File.Delete(_scriptPath); } catch { }
    }
}
