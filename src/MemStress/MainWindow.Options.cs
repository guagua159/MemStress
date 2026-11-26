using System.Windows.Controls;
using MemStress.Core;

namespace MemStress;

/// <summary>
/// 从界面收集测试参数，以及时长单位的换算。
///
/// 为什么单独一个分片：界面上时长以**小时**输入（允许 1 位小数），
/// 而引擎与命令行都用**秒**。换算只发生在这一层，混在主分片里容易被误改到
/// TestOptions 里，导致「界面上填 0.5 结果跑了 0.5 秒」这类事故。
/// </summary>
public partial class MainWindow
{
    private const double DefaultHours = 0.5;
    private const double MinHours = 0.1;     // 6 分钟，再短测不出什么
    private const double MaxHours = 48;

    private TestOptions BuildOptions()
    {
        int Int(TextBox box, int def) => int.TryParse(box.Text.Trim(), out int v) ? v : def;
        return new TestOptions
        {
            MemoryPercent = MemPercentSlider.Value / 100.0,
            Threads = Math.Max(0, Int(ThreadsBox, 0)),
            ChunkMiB = Math.Max(16, Int(ChunkBox, 256)),
            Passes = Math.Max(0, Int(PassesBox, 1)),
            DurationSeconds = HoursToSeconds(DurationBox.Text),
            UseLargePages = LargePageCheck.IsChecked == true,
            InjectError = InjectCheck.IsChecked == true,
            CrossCheck = CrossCheckBox.IsChecked == true,
            Profile = (StressProfile)ProfileBox.SelectedIndex
        };
    }

    /// <summary>
    /// 把界面输入的小时数换成秒。非法或越界时回退，并同时写状态栏与日志——
    /// 静默变成 0 会让测试立刻结束，用户还以为跑过了。
    /// 状态栏是单行、会被启动/停止流程立刻覆盖，所以必须也写日志。
    /// </summary>
    private int HoursToSeconds(string text)
    {
        int Clamp(double used, string why)
        {
            string msg = $"{why}，已按 {used} 小时计";
            StatusText.Text = msg;
            AppendLog($"[时长] {msg}");
            return (int)(used * 3600);
        }

        if (!double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out double hours) || hours <= 0)
            return Clamp(DefaultHours, $"时长输入「{text.Trim()}」无效，回退到 {DefaultHours} 小时");
        if (hours < MinHours)
            return Clamp(MinHours, $"时长下限 {MinHours} 小时（{MinHours * 60:F0} 分钟）");
        if (hours > MaxHours)
            return Clamp(MaxHours, $"时长上限 {MaxHours} 小时");
        return (int)Math.Round(hours * 3600);
    }

    /// <summary>秒 → 界面用的小时文本，保留 1 位小数（与输入框一致）。</summary>
    private static string SecondsToHoursText(int seconds) => $"{seconds / 3600.0:F1}";
}
