namespace MemStress;

/// <summary>
/// 命令行参数说明。与 <see cref="SelfTest.Run"/> 的参数解析必须保持一致——
/// 改了一边忘了另一边，用户会照着一份错的说明书调参数。
/// </summary>
public static partial class SelfTest
{
    public static void PrintUsage()
    {
        Console.WriteLine("""
MemStress —— Windows 内存压力测试（GUI 为主，以下为维护用命令行）

用法：
  MemStress.exe                              打开图形界面
  MemStress.exe --help                       显示本说明

自检（CI / 回归用，退出码 0 通过、2 失败）：
  --selftest            跑一轮完整压测自检
  --inject-error        配合 --selftest：故意写坏一个字节，验证报错链路
  --toolselftest        验证外部工具编排（不依赖真实工具）
  --wheaselftest        验证 WHEA 事件解析与判定链路
  --toolscan            列出各工具是否找到，以及搜索了哪些目录
  --yctailtest          验证 y-cruncher 日志解析与失败判定（含措辞覆盖）
  --p0test              验证 CSV 转义与日志写失败降级

自检可调参数：
  --seconds N           按时间跑的秒数（默认 8）
  --percent P           内存占用比例 0.01–0.95（默认 0.85）
  --chunk N             块大小 MiB（默认 256）
  --passes N            轮数，>0 时忽略 --seconds
  --profile NAME        quick | standard | deep | thorough
  --pattern NAME        只跑一个模式（MovingInversion / AddressAsData / RandomFill /
                        BlockCopy / WalkingOnes / Checkerboard / ModuloX）
  --inject-fault NAME   注入的故障类型（ByteFlip / StuckAt / BitFlip / BusLane /
                        AddressAlias / Coupling / RowStripe / Burst / Intermittent）
  --no-large-pages      强制用 4 KB 页，便于对比大页影响
  --report PATH         报告输出路径（默认会话目录 report.md）

说明：
  · 时长在界面上以小时输入（可填小数），命令行一律用秒。
  · 报告只写 report.md，不生成 html、不自动打开浏览器。
  · 交叉验证（y-cruncher）需在界面勾选，或在该页手动运行；命令行不触发它。
""");
    }
}
