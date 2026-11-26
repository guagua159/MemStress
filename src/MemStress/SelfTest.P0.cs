using System.Text;
using MemStress.Core;

namespace MemStress;

/// <summary>
/// P0 修复的自检：日志降级 + CSV 转义。
/// 模拟「盘写满/目录被删」用不可构造的路径：Windows 上保留名 CON 是最可靠的必败写目标。
/// </summary>
public static partial class SelfTest
{
    internal static int RunP0SelfTest()
    {
        int fail = 0;
        Console.WriteLine("[p0test] 1) CSV 转义：含逗号/引号的 Pattern 必须被引号包裹");

        var e = new MemoryError
        {
            Pattern = "fake,pattern\"x",
            ThreadId = 1, Pass = 1, BlockIndex = 0, OffsetInBlock = 8,
            AbsoluteAddress = 0x1234, Expected = 0xAA, Actual = 0xBB, WidthBytes = 8
        };
        string csv = e.ToCsvLine();
        // 期望：fake,pattern"x 变成 "fake,pattern""x"，字段数仍是 10 列
        bool escOk = csv.Contains(",\"fake,pattern\"\"x\",");
        // 用 Microsoft.VisualBasic 的 TextFieldParser 会引依赖，这里手写一个最小列切分验证
        int colCount = CountCsvColumns(csv);
        bool colsOk = colCount == 10;
        Console.WriteLine($"[p0test]   {(escOk ? "OK  " : "FAIL")} 转义形态: {csv}");
        Console.WriteLine($"[p0test]   {(colsOk ? "OK  " : "FAIL")} 列数={colCount}（期望 10）");
        if (!escOk || !colsOk) fail++;

        Console.WriteLine("[p0test] 2) 日志写失败降级：目录不可建时不抛异常、WriteFailed=true");
        // 阻断方式：让 logs 根目录路径上有一个【文件】挡路——
        // Directory.CreateDirectory 会抛 IOException，正好落进 LogWriter 的降级 catch。
        string holder = Path.Combine(Path.GetTempPath(), $"p0test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(holder);
        string badRoot = Path.Combine(holder, "blocker");
        File.WriteAllText(badRoot, "I am a file, not a directory");
        try
        {
            var log = new LogWriter(badRoot, "x");   // 构造不应抛
            log.WriteSessionHeader(new System.Text.Json.Nodes.JsonObject { ["k"] = "v" });
            log.WriteError(e);                        // 不应抛
            log.WriteNote("t", "m");
            bool ok = log.WriteFailed;
            Console.WriteLine($"[p0test]   {(ok ? "OK  " : "FAIL")} WriteFailed={log.WriteFailed}，原因={log.LastWriteError}");
            if (!ok) fail++;
            log.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[p0test]   FAIL 构造/写入抛了异常：{ex.GetType().Name}: {ex.Message}");
            fail++;
        }
        finally
        {
            try { Directory.Delete(holder, true); } catch { }
        }

        Console.WriteLine("[p0test] 3) 正常路径不受影响：写一份真日志并读回");
        {
            string goodRoot = Path.Combine(Path.GetTempPath(), $"p0test-ok-{Guid.NewGuid():N}");
            try
            {
                using var log = new LogWriter(goodRoot, "ok");
                log.WriteError(e);
                log.Flush();
                using var fs = new FileStream(log.CsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string text = sr.ReadToEnd();
                bool ok = text.Split('\n').Length >= 3 && text.Contains("fake");
                Console.WriteLine($"[p0test]   {(ok ? "OK  " : "FAIL")} 真日志已落盘且含转义行");
                if (!ok) fail++;
            }
            finally { try { Directory.Delete(goodRoot, true); } catch { } }
        }

        Console.WriteLine(fail == 0 ? "[p0test] PASS" : $"[p0test] FAIL（{fail} 项）");
        return fail == 0 ? 0 : 2;
    }

    /// <summary>按 RFC 4180 切列（够本测试用：引号段内的逗号不算分隔）。</summary>
    private static int CountCsvColumns(string line)
    {
        int cols = 1; bool inQ = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') inQ = !inQ;
            else if (c == ',' && !inQ) cols++;
        }
        return cols;
    }
}
