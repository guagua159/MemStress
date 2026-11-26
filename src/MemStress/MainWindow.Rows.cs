using MemStress.Core;

namespace MemStress;

/// <summary>
/// 错误明细表的数据行。只做「把 MemoryError 摊平成界面能直接绑定的列」，
/// 不含任何逻辑——判定与格式化都在 Core 里，这里改了会与报告/CSV 不一致。
/// </summary>
public sealed class ErrorRow
{
    private readonly MemoryError _e;

    public ErrorRow(MemoryError e) => _e = e;

    /// <summary>原始错误对象。导出报告时需要完整字段，不必从界面文本反解析。</summary>
    public MemoryError Error => _e;

    public string TimeText => _e.Timestamp.ToString("HH:mm:ss.fff");
    public int Pass => _e.Pass;
    public string Pattern => _e.Pattern;
    public int BlockIndex => _e.BlockIndex;
    public long OffsetHex => _e.OffsetInBlock;
    public string AddressHex => $"0x{_e.AbsoluteAddress:X16}";
    public string ExpectedHex => $"0x{_e.Expected:X16}";
    public string ActualHex => $"0x{_e.Actual:X16}";
}
