using System.Diagnostics;
using System.Text;

namespace MemStress.Core;


/// <summary>
/// y-cruncher 日志的增量读取器。只读本次运行那一个文件，不碰目录里别的日志。
/// 解析依据：Iteration: N  Total Elapsed Time: X seconds / Running &lt;算法&gt;: Passed|Failed
/// </summary>
public sealed class YcLogTail
{
    private readonly string _path;
    private long _offset;
    private readonly StringBuilder _partial = new();

    public int Iteration { get; private set; } = -1;
    public string CurrentAlgorithm { get; private set; } = "";
    public double ElapsedSeconds { get; private set; }
    public bool Finished { get; private set; }
    public bool SawFailure { get; private set; }

    /// <summary>
    /// 日志里见过至少一行「...: Passed」。
    /// 判定「通过」必须同时有退出码和日志证据——退出码可以撒谎（强杀兜底会给 0），
    /// 但日志里一行 Passed 都没有就宣布通过，是把「没测出来」当成「没问题」。
    /// </summary>
    public bool SawPassed { get; private set; }

    public YcLogTail(string path) { _path = path; }

    /// <summary>读新增内容并更新状态。返回本次新出现的完整行，供界面显示。</summary>
    public List<string> Poll()
    {
        var fresh = new List<string>();
        try
        {
            if (!File.Exists(_path)) return fresh;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= _offset) return fresh;
            fs.Seek(_offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string chunk = sr.ReadToEnd();
            _offset = fs.Position;
            _partial.Append(chunk);

            string all = _partial.ToString();
            int lastNl = all.LastIndexOf('\n');
            if (lastNl < 0) return fresh;   // 还没有完整行
            string complete = all.Substring(0, lastNl);
            _partial.Clear();
            _partial.Append(all.Substring(lastNl + 1));

            foreach (var raw in complete.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                fresh.Add(line);
                Parse(line);
            }
        }
        catch { /* 文件被占用/正在写，下次再读 */ }
        return fresh;
    }

    private void Parse(string line)
    {
        // 失败检查放在最前面、对**每一行**做，而不是只在 "Running ..." 分支里做。
        // 早先只检查 Running 行，于是 "Mismatch at offset ..."、"Corrupt result"
        // 这类不带 Running 前缀的失败被整行忽略——自检 --yctailtest 抓到了这一点。
        if (LooksFailed(line)) SawFailure = true;

        if (line.StartsWith("Iteration:", StringComparison.Ordinal))
        {
            // Iteration: 1  Total Elapsed Time: 7.096 seconds  ( 0.118 minutes )
            int i = line.IndexOf("Iteration:", StringComparison.Ordinal) + "Iteration:".Length;
            var num = new StringBuilder();
            for (; i < line.Length; i++)
            {
                if (char.IsDigit(line[i])) num.Append(line[i]);
                else if (num.Length > 0) break;
            }
            if (int.TryParse(num.ToString(), out int it)) Iteration = it;

            int t = line.IndexOf("Total Elapsed Time:", StringComparison.Ordinal);
            if (t >= 0)
            {
                string rest = line.Substring(t + "Total Elapsed Time:".Length).Trim();
                var sb = new StringBuilder();
                foreach (char c in rest)
                {
                    if (char.IsDigit(c) || c == '.') sb.Append(c);
                    else if (sb.Length > 0) break;
                }
                if (double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double sec))
                    ElapsedSeconds = sec;
            }
        }
        else if (line.StartsWith("Running ", StringComparison.Ordinal))
        {
            // Running N63: Passed  Test Speed: ...
            int colon = line.IndexOf(':');
            if (colon > "Running ".Length)
                CurrentAlgorithm = line.Substring("Running ".Length, colon - "Running ".Length).Trim();
            if (line.Contains(": Passed", StringComparison.OrdinalIgnoreCase)) SawPassed = true;
        }
        else if (line.Contains("Test Finished", StringComparison.Ordinal))
        {
            Finished = true;
        }
    }

    /// <summary>
    /// 判断一行输出是否表示失败。
    ///
    /// 不能只匹配 "Failed"：y-cruncher 的措辞随版本变（实测见过 Passed，官方文档也用过
    /// "FAIL"、"Error"）。只认一个词等于把判定绑在别人的文案上——它一改，我们就把
    /// 「测出问题」读成「通过」，这是最危险的错误方向。
    /// 因此按「出现否定词」判定，宁可误报不可漏报。
    /// </summary>
    private static bool LooksFailed(string line)
    {
        // 先排除误伤：Test Finished 本身含 "Finished" 不含这些词，不受影响
        foreach (var bad in FailedWords)
            if (line.Contains(bad, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>失败措辞清单。新增项要能在日志里找到出处。</summary>
    private static readonly string[] FailedWords =
    {
        "Failed", "FAIL", "Error", "Mismatch", "Incorrect", "Corrupt"
    };

    public string StatusLine() =>
        Iteration < 0 ? "等待 y-cruncher 启动…"
        : $"迭代 {Iteration}　当前 {CurrentAlgorithm}　已跑 {TimeSpan.FromSeconds(ElapsedSeconds):mm\\:ss}" +
          (SawFailure ? "　【出现失败】" : Finished ? "　【已完成】" : "");
}
