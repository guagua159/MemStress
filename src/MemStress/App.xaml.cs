using System.Windows;
using System.Windows.Threading;

namespace MemStress;

public partial class App : Application
{
    /// <summary>
    /// 全局异常兜底（P0-2）。此前 UI 线程任一回调抛异常，进程直接消失——
    /// 测试白跑、无报告、错误日志停在半行，用户还以为测完了。
    ///
    /// 原则：兜底只做「记录 + 让用户知道」，不吞掉导致状态不一致的异常。
    /// 真正致命的（引擎已崩、日志系统已死）会连着第二次抛，那时再让它崩。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // WER 本地转储：崩溃时留 minidump 供事后分析。官方机制，注册表即生效，无需代码。
        // 写失败不致命（多数是权限），所以这里静默——转储是尽力而为的附加品。
        try { WindowsErrorReporting.EnableLocalDumps(); } catch { }

        DispatcherUnhandledException += (s, args) =>
        {
            args.Handled = TryRecordCrash("UI", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.IsTerminating)
                TryRecordCrash("Fatal", (Exception)args.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            // 未观察的任务异常默认在 GC 时才炸进程。记下来并标记已观察：
            // 这类异常几乎都来自后台辅助任务，不该拖死整个进程。
            TryRecordCrash("Task", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        // --help / -h / /? / --h 也必须在这里拦下：早先没有这个分支，参数被当成「开窗」忽略，
        // 于是脚本里跑 `MemStress.exe --help` 会打开一个 GUI 并挂住，等到超时才失败。
        if (e.Args.Any(a => a is "--help" or "-h" or "/?" or "--h"))
        {
            SelfTest.PrintUsage();
            Shutdown(0);
            return;
        }

        if (e.Args.Any(a => a is "--p0test"))
        {
            Shutdown(SelfTest.RunP0SelfTest());
            return;
        }

        if (e.Args.Any(a => a is "--yctailtest"))
        {
            Shutdown(SelfTest.RunYcTailTest());
            return;
        }

        if (e.Args.Any(a => a is "--selftest" or "--inject-error" or "--toolselftest" or "--wheaselftest" or "--toolscan"))
        {
            int code = SelfTest.Run(e.Args);
            Shutdown(code);
            return;
        }

        new MainWindow().Show();
    }

    /// <summary>
    /// 尽力把崩溃写进当前日志会话；返回 true 表示可以继续跑（UI 场景），
    /// false 表示连记录都失败，让默认处理接管（弹窗/退出）。
    /// </summary>
    private static bool TryRecordCrash(string kind, Exception ex)
    {
        try
        {
            string dir = AppPaths.LogsRoot;
            string file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"time: {DateTime.Now:O}");
            sb.AppendLine($"kind: {kind}");
            sb.AppendLine($"clr:  {Environment.Version}");
            sb.AppendLine(ex.ToString());
            // 内层异常全展开——GlobalExceptionHandler 最没用的形态就是只记最外层包装
            var inner = ex.InnerException;
            while (inner != null) { sb.AppendLine($"--- inner ---"); sb.AppendLine(inner.ToString()); inner = inner.InnerException; }
            File.WriteAllText(file, sb.ToString());
            return kind == "UI";  // 只有 UI 线程的异常允许「记下后继续」
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 配置 Windows Error Reporting 的本地转储（官方机制见
/// learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps）。
/// 崩溃时在 logs\dumps\ 下留 minidump + 数据，供厂商/开发者事后分析。
/// </summary>
internal static class WindowsErrorReporting
{
    public static void EnableLocalDumps()
    {
        string dumpDir = Path.Combine(AppPaths.LogsRoot, "dumps");
        Directory.CreateDirectory(dumpDir);

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\MemStress.exe");
        key.SetValue("DumpFolder", dumpDir, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("DumpCount", 5, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("DumpType", 2, Microsoft.Win32.RegistryValueKind.DWord); // 2 = full dump
    }
}
