using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MemStress.Core;

/// <summary>
/// 结构化报错日志：CSV（人看/Excel）+ JSONL（机器解析）。
/// 每行写完立即 Flush —— 压测会把机器压到蓝屏，缓冲区里的错误日志等于没记。
/// 会话开头先写机器信息与参数，事后才知道这份日志是什么配置下跑出来的。
/// </summary>
public sealed class LogWriter : IDisposable
{
    private readonly string _dir;
    private readonly TextWriter _csv;
    private readonly TextWriter _jsonl;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>发生过写失败（磁盘满、目录被删、句柄被占）。读它不需要加锁——单写者、bool。</summary>
    public bool WriteFailed { get; private set; }

    /// <summary>最近一次写失败的原因。给界面/报告用，别再抛一次异常。</summary>
    public string? LastWriteError { get; private set; }

    public string SessionDir { get; }
    public string CsvPath { get; }
    public string JsonlPath { get; }
    public string MetaPath { get; }

    public LogWriter(string? rootDir = null, string? tag = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string name = string.IsNullOrWhiteSpace(tag) ? stamp : $"{stamp}-{tag}";
        _dir = rootDir ?? AppPaths.LogsRoot;
        SessionDir = Path.Combine(_dir, name);
        CsvPath = Path.Combine(SessionDir, "errors.csv");
        JsonlPath = Path.Combine(SessionDir, "errors.jsonl");
        MetaPath = Path.Combine(SessionDir, "session.json");

        try
        {
            Directory.CreateDirectory(SessionDir);
            _csv = new StreamWriter(new FileStream(CsvPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)) { AutoFlush = true };
            _jsonl = new StreamWriter(new FileStream(JsonlPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            // BOM 让 Excel 正确识别 UTF-8 中文
            _csv.Write("timestamp,thread,pass,pattern,block,offset,address,expected,actual,width_bytes\n");
            _csv.Flush();
        }
        catch (Exception ex)
        {
            // 目录被删/盘写满/被占用：不能让「点开始」直接崩（P0-3）。
            // 降级为「无日志继续跑」，WriteFailed 让界面去显示提示。
            _csv = StreamWriter.Null;
            _jsonl = StreamWriter.Null;
            WriteFailed = true;
            LastWriteError = ex.Message;
        }
    }

    public void WriteSessionHeader(JsonObject meta)
    {
        lock (_lock)
        {
            try
            {
                var text = meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                File.WriteAllText(MetaPath, text, new UTF8Encoding(false));
            }
            catch (Exception ex) { MarkFailed(ex); }
            try
            {
                _jsonl.WriteLine(meta.ToJsonString());
                _jsonl.Flush();
            }
            catch (Exception ex) { MarkFailed(ex); }
        }
    }

    public void WriteError(MemoryError e)
    {
        if (_disposed) return;
        lock (_lock)
        {
            try
            {
                _csv.WriteLine(e.ToCsvLine());
                _csv.Flush();
            }
            catch (Exception ex)
            {
                MarkFailed(ex);
                return;
            }
            var obj = new JsonObject
            {
                ["ts"] = e.Timestamp.ToString("O"),
                ["thread"] = e.ThreadId,
                ["pass"] = e.Pass,
                ["pattern"] = e.Pattern,
                ["block"] = e.BlockIndex,
                ["offset"] = e.OffsetInBlock,
                ["address"] = $"0x{e.AbsoluteAddress:X16}",
                ["expected"] = $"0x{e.Expected:X16}",
                ["actual"] = $"0x{e.Actual:X16}",
                ["widthBytes"] = e.WidthBytes
            };
            try
            {
                _jsonl.WriteLine(obj.ToJsonString());
                _jsonl.Flush();
            }
            catch (Exception ex)
            {
                MarkFailed(ex);
            }
        }
    }

    /// <summary>记一次失败。同一原因只记第一条（界面提示不闪烁）。</summary>
    private void MarkFailed(Exception ex)
    {
        WriteFailed = true;
        LastWriteError ??= ex.Message;
    }

    public void WriteNote(string kind, string message, JsonObject? extra = null)
    {
        if (_disposed) return;
        lock (_lock)
        {
            var obj = new JsonObject
            {
                ["ts"] = DateTime.Now.ToString("O"),
                ["kind"] = kind,
                ["message"] = message
            };
            if (extra != null) obj["data"] = extra;
            try
            {
                _jsonl.WriteLine(obj.ToJsonString());
                _jsonl.Flush();
            }
            catch (Exception ex)
            {
                MarkFailed(ex);
            }
        }
    }

    /// <summary>立刻把缓冲刷到磁盘（自检要在仍持有句柄时核对行数）。</summary>
    public void Flush()
    {
        lock (_lock) { _csv.Flush(); _jsonl.Flush(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _csv.Flush(); _csv.Dispose();
            _jsonl.Flush(); _jsonl.Dispose();
        }
    }
}
