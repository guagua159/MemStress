using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MemStress.Core;

public sealed partial class ExternalTool
{
    /// <summary>把外部工具运行结果写进会话元数据，报告里能引用。</summary>
    public static JsonObject ToJson(string toolId, RunResult r) => new()
    {
        ["tool"] = toolId,
        ["exitCode"] = r.ExitCode,
        ["timedOut"] = r.TimedOut,
        ["stdout"] = r.StdOutPath,
        ["stderr"] = r.StdErrPath,
        ["summary"] = r.Summary
    };
}
