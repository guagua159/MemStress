using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MemStress.Core;

/// <summary>
/// 外部工具集成（一）：工具目录、路径发现、投放点、手动路径持久化。
/// 这一层只回答「工具在哪、该放哪」，不启动任何进程。运行与停止见 ExternalTool.Run.cs。
/// </summary>
public sealed partial class ExternalTool
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string[] SearchDirs { get; init; } = Array.Empty<string>();
    public string DownloadUrl { get; init; } = "";
    public string Role { get; init; } = "";
    public string[] DefaultArgs { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 可执行文件名候选（按优先级）。同一工具在不同发行版里文件名并不一致，
    /// 例如 TestMem5 的官方发行版就是 <c>TM5.exe</c> 而不是 <c>TestMem5.exe</c>，
    /// 只认一个名字会把用户正确放置的文件判成「未安装」。
    /// </summary>
    public string[] ExeNames { get; init; } = Array.Empty<string>();

    /// <summary>主文件名，用于界面提示与说明文件。</summary>
    public string ExeName => ExeNames.Length > 0 ? ExeNames[0] : "";


    /// <summary>工具自己写的日志文件（相对其工作目录），运行结束后归档到会话目录。</summary>
    public string[] ArtifactGlobs { get; init; } = Array.Empty<string>();

    /// <summary>可选预设档位。只有 y-cruncher 有，其它工具为空。</summary>
    public IReadOnlyList<YcPreset> Presets { get; init; } = Array.Empty<YcPreset>();

    public bool HasPresets => Presets.Count > 0;

    /// <summary>程序自带投放点：<c>tools\&lt;id&gt;\</c>（锚定程序自身目录，随文件夹一起分发）。</summary>
    public string DropInDir => AppPaths.ToolDir(Id);

    /// <summary>兼容旧称呼。</summary>
    public string SuggestedDir => DropInDir;

    /// <summary>
    /// 按优先级解析可执行文件：
    /// <list type="number">
    /// <item>用户手动指定的路径（最高优先级，用户明确选过就该尊重）</item>
    /// <item>程序自带投放点 <c>tools\&lt;id&gt;\</c>（含版本号子目录，递归）</item>
    /// <item>机器上其他常见安装位置（可选便利，找不到不影响主流程）</item>
    /// </list>
    /// </summary>
    public string? ResolvePath()
    {
        string? manual = LoadManualPath(Id);
        if (manual != null) return manual;

        if (ExeNames.Length == 0) return null;

        // (1) 投放点优先，且先查本层再递归——用户直接把 exe 丢进 tools\<id>\ 是最常见用法
        foreach (var name in ExeNames)
        {
            string direct = Path.Combine(DropInDir, name);
            if (File.Exists(direct)) return direct;
        }
        string? inDrop = SearchUnder(DropInDir);
        if (inDrop != null) return inDrop;

        // (2) 其他位置仅作便利，找不到也正常
        foreach (var dir in SearchDirs)
        {
            if (string.Equals(dir, DropInDir, StringComparison.OrdinalIgnoreCase)) continue;
            var hit = SearchUnder(dir);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>在指定目录下递归找任一候选文件名；按候选顺序优先，同名字则浅层优先（避免误选备份/旧版本）。</summary>
    private string? SearchUnder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var name in ExeNames)
            {
                var hit = Directory.GetFiles(dir, name, SearchOption.AllDirectories)
                    .OrderBy(p => p.Length)
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }
        catch { return null; }   // 无权限的目录跳过
    }

    /// <summary>给界面显示「到底找了哪些地方」，避免只说「未安装」让人无从下手。</summary>
    public string SearchDescription()
    {
        var parts = new List<string> { DropInDir };
        parts.AddRange(SearchDirs.Where(d => !string.Equals(d, DropInDir, StringComparison.OrdinalIgnoreCase)));
        return string.Join("；", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 首次启动时把投放点和说明文件建出来。
    /// 用户不知道「该放哪」是这类工具最常见的失败点，所以目录要自己长出来、且带说明书。
    /// </summary>
    public static void SeedDropInFolders()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ToolsRoot);
            foreach (var t in Catalog) AppPaths.ToolDir(t.Id);

            string readme = Path.Combine(AppPaths.ToolsRoot, "把第三方工具放在这里.txt");
            if (!File.Exists(readme)) File.WriteAllText(readme, BuildDropInReadme(), new UTF8Encoding(false));
        }
        catch { /* 只读安装目录下无法创建，不影响启动 */ }
    }

    private static string BuildDropInReadme()
    {
        var sb = new StringBuilder();
        sb.AppendLine("MemStress 外部工具投放点");
        sb.AppendLine("=========================");
        sb.AppendLine();
        sb.AppendLine("本程序不下载、也不打包任何第三方工具（授权原因）。");
        sb.AppendLine("请从各工具官网自行下载后，解压到下面各自对应的文件夹里。");
        sb.AppendLine();
        foreach (var t in Catalog)
        {
            sb.AppendLine("● " + t.DisplayName);
            sb.AppendLine("  放到：" + Path.Combine("tools", t.Id) + "\\");
            sb.AppendLine("  文件名：" + string.Join("、", t.ExeNames));
            sb.AppendLine("  官网：" + t.DownloadUrl);
            sb.AppendLine("  作用：" + t.Role);
            sb.AppendLine();
        }
        sb.AppendLine("说明：");
        sb.AppendLine("1. 直接放进对应文件夹即可。如果压缩包自带版本号子目录");
        sb.AppendLine("   （例如 y-cruncher v0.8.6\\），整包解压进来也能识别，程序会递归查找。");
        sb.AppendLine("2. 已经装在别处（例如 C:\\Tools）也不用搬家，程序会自动识别。");
        sb.AppendLine("   仍找不到时可在界面点「指定路径…」手动选择，选过会记住。");
        sb.AppendLine("3. 想知道程序实际搜索了哪些目录，运行：MemStress.exe --toolscan");
        return sb.ToString();
    }

    // ---------- 手动指定路径的持久化 ----------
    // 用户选过一次路径，重启后不该再显示「未安装」逼他重选。

    private static string PrefsPath => Path.Combine(AppPaths.ToolsRoot, "paths.json");

    private static JsonObject LoadPrefs()
    {
        try
        {
            if (File.Exists(PrefsPath))
                return JsonNode.Parse(File.ReadAllText(PrefsPath)) as JsonObject ?? new JsonObject();
        }
        catch { /* 配置文件坏了就当没有，不影响启动 */ }
        return new JsonObject();
    }

    /// <summary>记住用户手动选的路径。</summary>
    public static void SaveManualPath(string toolId, string exePath)
    {
        try
        {
            var prefs = LoadPrefs();
            prefs[toolId] = exePath;
            File.WriteAllText(PrefsPath, prefs.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 记不住也不该让主流程失败 */ }
    }

    /// <summary>取用户手动选过的路径（文件已不存在则返回 null）。</summary>
    public static string? LoadManualPath(string toolId)
    {
        try
        {
            var p = LoadPrefs()[toolId]?.GetValue<string>();
            return p != null && File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>配置文件坏掉时的自愈：备份坏文件并重建，避免用户永远卡在「未安装」。</summary>
    public static string? RepairPrefs()
    {
        try
        {
            if (!File.Exists(PrefsPath)) return null;
            string text = File.ReadAllText(PrefsPath);
            JsonNode.Parse(text);   // 能解析就说明没坏
            return null;
        }
        catch
        {
            try
            {
                string bak = PrefsPath + ".bad";
                File.Move(PrefsPath, bak, overwrite: true);
                return bak;
            }
            catch { return null; }
        }
    }

    public static IReadOnlyList<ExternalTool> Catalog { get; } = new List<ExternalTool>
    {
        new()
        {
            Id = "ycruncher",
            DisplayName = "y-cruncher",
            Role = "非缓存型压力负载（大数运算 + 大页内存），补足纯内存校验测不到的 IMC/缓存层次",
            DownloadUrl = "https://www.numberworld.org/y-cruncher/",
            ExeNames = new[] { "y-cruncher.exe" },
            // 仅「机器上其他位置」的便利搜索；投放点 tools\ycruncher\ 由 DropInDir 单独负责
            SearchDirs = new[] { @"C:\Tools", @"C:\Program Files", @"C:\y-cruncher" },
            // stress 模式的官方语法：stress [-M:{内存}] [-D:{每项秒数}] [-TL:{总时限}] [算法]
            // 三个坑（调研记录见本机 docs/research.md §3，不随仓库分发）：stress 不认 -mt；-o 在 stress 模式无效；
            // 输出走 WriteConsoleW，直接重定向只能拿到空文件，官方途径是启动选项 logfile:"..."
            // 实际运行时由 YcPreset 生成带时间戳的 logfile 与 -M/-D/-TL，这里只是兜底默认值。
            DefaultArgs = YcPreset.Standard.BuildArgs("ycruncher-stress.log"),
            // 时间戳文件名必须用通配，否则本次日志不会被归档
            ArtifactGlobs = new[] { "ycruncher-stress-*.log" },
            Presets = YcPreset.All
        },
    };
}
