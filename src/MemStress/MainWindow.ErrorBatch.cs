using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using MemStress.Core;

namespace MemStress;

public partial class MainWindow
{
    /// <summary>worker 线程产生的、还没上屏的错误。Enqueue 无锁，只有定时器线程出队。</summary>
    private readonly ConcurrentQueue<MemoryError> _pendingErrors = new();

    /// <summary>
    /// 把积压的错误一次性搬上界面。500ms 一次、至多搬 2000 条——
    /// 上限是防错误风暴时单帧插入过重；搬不完的留在队列里下一拍再来，
    /// 完整数据始终在日志文件里，界面只是个滚动窗口。
    /// </summary>
    private void DrainPendingErrors()
    {
        if (_pendingErrors.IsEmpty) return;

        int n = 0;
        bool hitCap = false;
        while (n < 2000 && _pendingErrors.TryDequeue(out var err))
        {
            _errors.Add(new ErrorRow(err));
            n++;
        }
        hitCap = n >= 2000 && !_pendingErrors.IsEmpty;

        // 超过 5000 条裁掉最旧的（原行为保留）
        while (_errors.Count > 5000) _errors.RemoveAt(0);

        ErrorValue.Text = _engine!.Errors.ToString();
        ErrorValue.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        ErrorHint.Text = hitCap
            ? $"错误极多，界面正追赶（积压 {_pendingErrors.Count} 条）；完整记录在日志里"
            : $"共 {_errors.Count} 条（界面保留最近 5000 条，完整记录在日志里）";
    }
}
