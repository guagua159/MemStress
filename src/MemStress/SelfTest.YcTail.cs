using System.Text;
using MemStress.Core;

namespace MemStress;

/// <summary>
/// y-cruncher 日志解析的自检。单独一个文件是因为它验证的是「解析器读得对不对」，
/// 与压测内核、外部工具编排都无关。
/// </summary>
public static partial class SelfTest
{
    /// <summary>
    /// 用构造出来的日志样本验证 <see cref="YcLogTail"/> 的判定，重点是失败措辞的覆盖。
    ///
    /// 为什么需要这个自检：失败判定原来只匹配 "Failed" 一个词，而 y-cruncher 的措辞
    /// 随版本变。这个假设一破，程序会把「测出问题」读成「通过」——方向性错误，
    /// 靠真实跑一次很难覆盖到（要恰好遇到一种没见过的措辞）。
    /// </summary>
    internal static int RunYcTailTest()
    {
        // 每个样本末尾都必须有换行：Poll() 只解析「完整行」，
        // 半行留着下次读——这是实时读日志的正确行为，不是缺陷。
        // WantPass 断言 SawPassed：判定「通过」需要日志里有至少一行 Passed 证据。
        // 没有 Failed 也没有 Passed（比如空日志/格式变了）必须三态分开，不能当通过。
        var cases = new (string Name, string Body, bool WantFail, bool WantFinished, bool WantPass)[]
        {
            ("正常通过",      "Iteration: 1  Total Elapsed Time: 60.0 seconds\nRunning N63: Passed\nTest Finished.\n", false, true, true),
            ("Failed 字面",   "Iteration: 1\nRunning N63: Failed\n", true, false, false),
            ("FAIL 大写",     "Iteration: 1\nRunning VT3: FAIL\n", true, false, false),
            ("Error 措辞",    "Iteration: 1\nRunning FFTv4: Error detected\n", true, false, false),
            ("Mismatch 措辞", "Iteration: 2\nMismatch at offset 0x1000\n", true, false, false),
            ("Corrupt 措辞",  "Iteration: 3\nCorrupt result\n", true, false, false),
            ("未完成无失败",  "Iteration: 1\nRunning N63: Passed\n", false, false, true),
            ("多轮全通过",    "Iteration: 1\nRunning N63: Passed\nIteration: 2\nRunning VT3: Passed\nTest Finished.\n", false, true, true),
            // P2-1 新增：既无失败也无通过 = 无法判定，绝不能当「通过」
            ("空日志",        "", false, false, false),
            ("只有迭代数",    "Iteration: 1\nIteration: 2\n", false, false, false),
        };

        int fail = 0;
        Console.WriteLine("[yctailtest] 验证 y-cruncher 日志解析与失败判定");
        foreach (var c in cases)
        {
            string dir = Path.Combine(AppPaths.LogsRoot, $"yctail-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "sample.log");
            try
            {
                File.WriteAllText(file, c.Body, new UTF8Encoding(false));
                var tail = new YcLogTail(file);
                tail.Poll();

                bool ok = tail.SawFailure == c.WantFail && tail.Finished == c.WantFinished && tail.SawPassed == c.WantPass;
                if (!ok) fail++;
                Console.WriteLine($"[yctailtest]   {(ok ? "OK  " : "FAIL")} {c.Name}：SawFailure={tail.SawFailure}/{c.WantFail} Finished={tail.Finished}/{c.WantFinished} SawPassed={tail.SawPassed}/{c.WantPass}");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        Console.WriteLine(fail == 0 ? "[yctailtest] PASS" : $"[yctailtest] FAIL（{fail} 项不符）");
        return fail == 0 ? 0 : 2;
    }
}
