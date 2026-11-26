using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MemStress.Core;

namespace MemStress;

/// <summary>
/// 主窗口。按职责拆成 partial 文件，每个文件只放一类东西：
/// <list type="bullet">
/// <item>MainWindow.xaml.cs —— 字段、构造、自研压测的启停与轮询</item>
/// <item>MainWindow.Log.cs —— 日志面板、进度显示、报告导出</item>
/// <item>MainWindow.Tools.cs —— 外部工具卡片的编排（发现、启停、预设）</item>
/// <item>MainWindow.Tools.Yc.cs —— y-cruncher 专属启动/停止路径</item>
/// <item>MainWindow.Options.cs —— 参数收集与小时/秒换算</item>
/// <item>MainWindow.Report.cs —— 报告页（程序内显示，不依赖浏览器）</item>
/// <item>MainWindow.CrossCheck.cs —— 压测收尾后的可选 y-cruncher 交叉验证</item>
/// <item>MainWindow.YcTab.cs —— y-cruncher 前端页（它原生没有 GUI）</item>
/// </list>
/// 改界面控件行为看对应 partial；改压测内核看 Core/StressEngine.cs，不要在这里加算法。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ErrorRow> _errors = new();
    private readonly DispatcherTimer _timer;
    private StressEngine? _engine;
    private WheaMonitor? _whea;
    private LogWriter? _log;
    private JsonObject? _systemInfo;
    private DateTime _startedAt;
    private TimeSpan _lastElapsed;
    private long _lastAllocatedBytes;
    /// <summary>停止后引擎会被释放，Arena 随之清空；分配方式与说明必须提前固化，
    /// 否则报告里只能写出「已释放」，事后无法判断那次用的是大页还是 4 KB 页。</summary>
    private string _lastAllocationKind = "";
    private string _lastAllocationNote = "";
    private Metrics? _lastSnapshot;
    private readonly List<WheaMonitor.WheaEvent> _wheaEvents = new();
    private bool _running;
    /// <summary>本次是否跑满预定轮次/时长。只有它为 true 时报告才敢给「通过」结论。</summary>
    private bool _completedNaturally;
    private string _stopReason = "手动停止";

    public MainWindow()
    {
        InitializeComponent();
        ErrorGrid.ItemsSource = _errors;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += Timer_Tick;
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => Shutdown();
        // y-cruncher 不在位时把交叉验证开关置灰并说明原因，别让用户点下去才报错
        InitCrossCheckUi();
        InitYcTab();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitElevationUi();
        UpdatePatternText();
        UpdateLogDir();
        LoadTools();
        Task.Run(() =>
        {
            var info = SystemInfo.Collect();
            Dispatcher.Invoke(() =>
            {
                _systemInfo = info;
                MachineText.Text =
                    $"CPU：{info["cpu"]}（{info["physicalCores"]} 物理核 / {info["cpuThreads"]} 线程）\n" +
                    $"主板：{info["mainboard"]}  BIOS {info["bios"]}\n" +
                    $"内存：{info["totalPhysicalMiB"]} MiB，可用 {info["availablePhysicalMiB"]} MiB\n" +
                    $"大页粒度：{info["largePageMinimumKB"]} KB   NUMA 节点：{info["numaNodes"]}";
                AppendLog($"[信息] {info["cpu"]} / {info["mainboard"]} / 可用 {info["availablePhysicalMiB"]} MiB");
            });
        });
    }

    /// <summary>
    /// 建立外部工具的投放点与说明文件。用户不知道该放哪是这类工具最常见的失败点，
    /// 所以启动时就把 tools\<id>\ 和说明建出来。
    /// </summary>
    private static void LoadTools() => ExternalTool.SeedDropInFolders();


    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        var opt = BuildOptions();
        _errors.Clear();
        _pendingErrors.Clear();  // 上一轮可能还有没来得及上屏的错误，不清会串到这一轮的界面
        _wheaEvents.Clear();
        WheaBanner.Visibility = Visibility.Collapsed;
        ErrorValue.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        _stopReason = opt.Passes > 0 ? $"{opt.Passes} 轮完成" : $"{SecondsToHoursText(opt.DurationSeconds)} 小时到";
        _completedNaturally = false;

        _systemInfo ??= SystemInfo.Collect();
        _log = new LogWriter(AppPaths.LogsRoot, "gui");
        var header = _systemInfo.DeepClone().AsObject();
        header["options"] = opt.Describe();
        header["mode"] = "gui";
        _log.WriteSessionHeader(header);

        _whea = new WheaMonitor();
        _whea.WheaFound += w =>
        {
            Dispatcher.Invoke(() =>
            {
                _wheaEvents.Add(w);
                _log?.WriteNote("whea", $"事件 {w.EventId} {w.Message}");
                WheaBanner.Visibility = Visibility.Visible;
                WheaText.Text = $"事件 {w.EventId}（{w.Level}）于 {w.Time:HH:mm:ss}：{w.Message}";
                AppendLog($"[WHEA] 事件 {w.EventId} {w.Level} {w.Message}");
            });
        };

        _engine = new StressEngine(opt);
        // 错误风暴走批量：worker 线程只入队 + 写日志文件，界面由 500ms 定时器一次取走。
        // 早先是每条错误一次 Dispatcher.Invoke 同步封送—— WalkingOnes 单字节坏点在链式
        // 校验下每秒能报上百条，UI 线程被 marshaling 打满，界面冻结、压测吞吐也跟着失真。
        _engine.ErrorFound += err =>
        {
            _log?.WriteError(err);
            _pendingErrors.Enqueue(err);
        };
        _engine.StatusChanged += s => Dispatcher.Invoke(() => { StatusText.Text = s; AppendLog($"[状态] {s}"); });

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        RestartElevatedButton.IsEnabled = false;  // 运行中重启 = 新旧实例抢同一批内存，几乎必炸
        _running = true;
        _startedAt = DateTime.Now;
        // 新一轮开始：清掉上一轮的交叉验证结论，否则报告会混进上一次的结果。
        _crossCheck = new CrossCheckResult();
        _crossCheckCts.Dispose();
        _crossCheckCts = new CancellationTokenSource();
        Progress.Value = 0;
        AppendLog($"[开始] {opt.Describe()}");
        AppendLog($"[日志] {_log.SessionDir}");

        try
        {
            await Task.Run(() => _engine.Start());
            _whea.Start();
            _timer.Start();
            AllocText.Text = $"已分配 {_engine.TotalBytes / 1048576} MiB\n{_engine.Arena?.Note}";
            AppendLog($"[分配] {_engine.TotalBytes / 1048576} MiB，{_engine.Arena?.Note}");
            // 分配方式必须落进日志：大页与 4 KB 页的检测意义不同（大页绕开 TLB 压力，
            // 部分内存缺陷只有走 4 KB 页才暴露）。事后翻日志时必须能看出那次到底用的哪种，
            // 否则同一台机器两次结果不一致时无从解释。此前只打在界面上，关掉就没了。
            _log?.WriteNote("allocation", $"{_engine.Arena?.Kind} | {_engine.TotalBytes / 1048576} MiB | {_engine.Arena?.Note}");
        }
        catch (Exception ex)
        {
            _running = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            RestartElevatedButton.IsEnabled = true;
            StatusText.Text = "启动失败：" + ex.Message;
            AppendLog($"[错误] 启动失败：{ex.Message}");
            MessageBox.Show(this, $"分配失败：{ex.Message}\n\n常见原因：内存占用率过高、页面文件不足、或系统已无连续内存。\n可调低「内存占用」或关闭大页后重试。",
                "MemStress", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        // 顶部这个「停止」只停自研压测。外部工具各有自己的二合一按钮，
        // 不再从这里连带停止，避免用户分不清到底停了什么。
        //
        // 例外：交叉验证阶段（_running 已为 false，压测已收尾）这个按钮被
        // MainWindow.CrossCheck.cs 重新启用，此时它是中止 y-cruncher 的唯一入口——
        // 取消 _crossCheckCts，让 RunYcPresetAsync 走用户停止分支并清理进程树。
        if (!_running)
        {
            _stopReason = "手动停止交叉验证";
            AppendLog("[停止] 中止交叉验证…");
            try { _crossCheckCts.Cancel(); } catch { }
            return;
        }
        _stopReason = "手动停止";
        StopInternal();
    }

    private Task StopInternal()
    {
        if (!_running) return Task.CompletedTask;
        _running = false;
        _timer.Stop();
        _whea?.Stop();
        try { _engine?.Stop(); } catch { }
        _lastElapsed = _engine?.Snapshot().Elapsed ?? DateTime.Now - _startedAt;
        _lastAllocatedBytes = _engine?.TotalBytes ?? 0;
        _lastAllocationKind = _engine?.Arena?.Kind.ToString() ?? "";
        _lastAllocationNote = _engine?.Arena?.Note ?? "";
        // 引擎随后会在后台线程被释放，Snapshot() 就取不到了。这里先把最终统计固化下来，
        // 否则停止后再「导出报告」会写出 0 读写量（界面上明明跑了几百 GiB）。
        _lastSnapshot = _engine?.Snapshot();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        RestartElevatedButton.IsEnabled = true;
        StatusText.Text = $"已停止（{_stopReason}）";
        // 复用上面刚取到的 _lastSnapshot：再调一次 Snapshot() 拿到的是同一时刻的同一组数，
        // 而引擎马上要在后台线程被释放，多取一次既浪费也可能取到 null。
        AppendLog($"[停止] {_stopReason}，累计 {(_lastSnapshot?.BytesDone ?? 0) / Metrics.BytesPerGiB:F1} GiB，错误 {_errors.Count}");
        if (_lastSnapshot is { } snap) UpdateProgress(snap);
        // 释放内存，任务管理器里应能看到回落
        // 返回释放任务：交叉验证要等它完成才能启动。
        // 两者都抢大页，压测内存没放干净就去申请，y-cruncher 会直接拿到 Win32 1450
        //（系统资源不足），报告里出现「分配失败」而不是「内存有问题」——误导性很强。
        return Task.Run(() =>
        {
            try { _engine?.Dispose(); } catch { }
            _engine = null;
            Dispatcher.Invoke(() => AllocText.Text = "已释放（测试结束）");
        });
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_engine == null) return;
        DrainPendingErrors();
        var m = _engine.Snapshot();
        _lastElapsed = m.Elapsed;
        ElapsedValue.Text = m.Elapsed.ToString(@"hh\:mm\:ss");
        ThroughputValue.Text = $"{m.GigaBytesPerSecond:F2} GiB/s";
        BytesValue.Text = $"{m.BytesDone / Metrics.BytesPerGiB:F1} GiB";
        PassValue.Text = m.Pass.ToString();
        UpdateProgress(m);

        if (!_engine.IsRunning && _running)
        {
            var opt = BuildOptions();
            _stopReason = opt.Passes > 0 ? "全部轮次完成" : "时长到";
            _completedNaturally = true;
            var released = StopInternal();

            // 自研压测自然跑完后，若用户勾了交叉验证，接着跑第二工具。
            // 只在「自然收尾」时触发：用户手动停止说明他不想要更多测试了。
            if (opt.CrossCheck)
                _ = RunCrossCheckAfterReleaseAsync(opt, released, _crossCheckCts.Token);
        }
    }

    /// <summary>
    /// 刷新进度条与说明行。<paramref name="m"/> 由调用方传入而不是在这里重新 Snapshot()——
    /// Snapshot 要读多个 Interlocked 计数并算一次百分比，500ms 定时器里取两次是白算。
    /// </summary>
    private void UpdateProgress(Metrics m)
    {
        Progress.Value = m.Percent;
        ProgressText.Text = $"当前轮进度 {m.Percent:F1}% · 单轮 {m.BytesTotal / Metrics.BytesPerGiB:F2} GiB · 已运行 {_lastElapsed:hh\\:mm\\:ss}";
    }

    private void MemPercentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MemPercentLabel != null)
            MemPercentLabel.Text = $"内存占用：{e.NewValue:F0}% 可用内存";
    }

    private void UpdatePatternText()
    {
        // SelectionChanged 在 InitializeComponent 期间就会触发，此时控件还没建好
        if (PatternText == null || ProfileBox == null) return;
        var opt = new TestOptions { Profile = (StressProfile)Math.Max(0, ProfileBox.SelectedIndex) };
        var sb = new StringBuilder();
        foreach (var p in opt.Patterns)
        {
            var k = Patterns.Parse(p);
            sb.AppendLine($"• {Patterns.Name(k)}");
            sb.AppendLine($"  {Patterns.Description(k)}");
        }
        PatternText.Text = sb.ToString();
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePatternText();
}
