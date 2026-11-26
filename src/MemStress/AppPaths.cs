namespace MemStress;

/// <summary>
/// 路径约定。exe 直接位于安装根下，所以「程序所在目录」就是全部数据的锚点：
/// <list type="bullet">
/// <item><see cref="Root"/> 安装根，logs 放这里。</item>
/// <item><see cref="ToolsRoot"/> 第三方工具投放点 <c>tools\</c>。</item>
/// </list>
/// 整个文件夹因此是自洽的可分发单元——复制到任何机器上都能直接用，不会指向原作者路径。
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// 安装根 = exe 所在目录。
    /// 不再向上搜索 csproj/docs：exe 已经直接落在根下，向上找反而会在某些布局下跳到父目录。
    /// </summary>
    public static string Root => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string LogsRoot
    {
        get
        {
            var p = Path.Combine(Root, "logs");
            try { Directory.CreateDirectory(p); } catch { /* 只读安装目录：仍返回路径供显示 */ }
            return p;
        }
    }

    /// <summary>第三方工具的固定投放点：<c>&lt;安装根&gt;\tools\</c>。</summary>
    public static string ToolsRoot => Path.Combine(Root, "tools");

    /// <summary>某个工具的存放目录（<c>tools\&lt;id&gt;\</c>）。创建失败不抛异常——只读目录下也要能启动。</summary>
    public static string ToolDir(string toolId)
    {
        var p = Path.Combine(ToolsRoot, toolId);
        try { Directory.CreateDirectory(p); } catch { /* 只读目录：仍然返回路径，让界面能显示 */ }
        return p;
    }
}
