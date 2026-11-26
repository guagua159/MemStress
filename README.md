# MemStress — Windows 内存压力测试工具

用 .NET 10 + WPF 实现的内存压力测试工具，自带图形界面与结构化报错日志。
设计参照 [TestMem5](https://testmem5.com/) 的四类子测试与 [MemTest86 官方算法说明](https://www.memtest86.com/tech_memtest-algoritm.html)。

**当前版本 1.9**，零 NuGet 依赖，MIT 许可。

## 它做什么

自研压测引擎用 7 种故障模型反复读写你指定比例的内存，把写进去的数据读回来逐字节比对。
任何一位不一致都记进结构化日志（CSV / JSONL / Markdown 报告），并附带块号、块内偏移、绝对地址、期望值与实际值。

可选的 y-cruncher 交叉验证补足纯内存校验测不到的 IMC 与缓存层次；WHEA 监控把硬件层已纠正错误也纳入判定。

**报告严格区分「通过 / 失败 / 未测」三态**：读不到 WHEA 事件日志写「未监控」而不是「无事件」，
交叉验证没跑成写「无法判定」而不是「通过」。制造虚假信心比不做更糟。

## 快速开始

需要 .NET SDK 10.0.404（`global.json` 已锁定，`rollForward: latestFeature`）。

```bash
dotnet build src/MemStress/MemStress.csproj
```

构建产物落在仓库根目录，双击 `MemStress.exe` 即可运行。exe 与 `tools\`、`logs\` 同级，
整个仓库文件夹是自洽的可分发单元——复制到别的机器上也能直接用。

命令行自检（CI 与验收用，退出码 `0` 通过、`2` 失败）：

```bash
# 基本跑通：8 秒，占 20% 可用内存
MemStress.exe --selftest --seconds 8 --percent 0.2

# 报错链路：故意写坏一个字节，验证 CSV / JSONL / 报告三处都能看到
MemStress.exe --selftest --seconds 8 --percent 0.2 --inject-error

# 外部工具编排：不依赖真实工具，用系统自带程序验证「拉起→捕获→归档→落盘」
MemStress.exe --toolselftest

# WHEA 解析与判定链路：用合成事件，真实机器未必有 WHEA 事件可测
MemStress.exe --wheaselftest

# 工具发现：列出找到了哪些工具、搜索了哪些目录
MemStress.exe --toolscan

# y-cruncher 日志解析与失败判定
MemStress.exe --yctailtest

# CSV 转义与日志写失败降级
MemStress.exe --p0test
```

`--selftest` 的内存占用默认是 **0.2**（约 6 GiB，16 GB 机器上安全）；
不传 `--percent` 时不会去抢 85% 内存。

## 界面

| 页签 | 内容 |
|---|---|
| 压测 | 档位、内存占用、线程数、块大小、轮数/时长、大页开关、交叉验证开关；实时指标（时长/吞吐/累计读写/轮次/错误）；错误表格 |
| 报告 | 查看报告、打开日志目录 |
| 运行日志 | 运行过程逐行日志，可清空、可打开日志目录 |
| y-cruncher | 工具发现、指定路径、三档预设运行、输出归档与三态判定 |
| 方法与局限 | 判读规则、已知局限、参考来源 |

顶部栏在非管理员运行时显示「以管理员重启」按钮——大页需要「锁定页面」特权，提权后才拿得到。

## 测试模式

7 种模式，每种对应一类真实内存故障模型。所有模式都在本进程内做完整回读校验。

| 模式 | 压什么 |
|---|---|
| MovingInversion | 写 A → 写 ~A → 读回，反复搬运整块数据，压数据总线与写缓冲 |
| AddressAsData | 把块内偏移写进内存再校验，专抓地址译码错误 |
| RandomFill | 种子生成随机字节流写入后整块比对，覆盖面最广、最慢 |
| BlockCopy | 相邻块互为源和目的整块拷贝再校验，压内存控制器与缓存一致性 |
| WalkingOnes | 单比特在字节内依次置位，**链式**边走边校验，压位线故障与相邻位串扰 |
| Checkerboard | 交替 0x55/0xAA 填充，抓相邻存储单元耦合故障 |
| ModuloX | 按模数打散访问顺序，专查被缓存与写缓冲掩盖的错误（移动反转类「写后立即读」的固有盲区） |

### 档位

| 档位 | 模式数 | 组成 |
|---|---|---|
| 快速筛查 | 2 | MovingInversion + AddressAsData |
| 标准 | 4 | 加 RandomFill + BlockCopy（默认） |
| 深度 | 6 | 加 WalkingOnes + Checkerboard |
| 彻底 | 7 | 加 ModuloX，慢约 4 倍 |

档位对应 TM5 的定位而非强度：`extreme@anta777` 与 `absolutnew` 的差异完全由 cfg 决定，
两者是不同用途而非强弱两档。

### WalkingOnes 的设计取舍

早期版本用 64 个位值 × 每块前 32 MiB，工作量 2 GiB 却只覆盖 0.03 GiB——
64 位 walk 的高 32 位在 byte 级毫无区别，等于把同一件事重复 64 遍。

现在改为**链式**：位位置 b 的遍历校验 b-1 的图案、同时写入 b 的图案，
共 1 次初始填充 + 8 次遍历 + 1 次收尾校验 = 10 次整块遍历。
检测力不降反升：写 i 时把 i-1 带坏，下一次遍历会在 i-1 处当场发现；
而「写满再查」会让坏值被后续写入覆盖，故障消失。这与 MemTest86 Test 4 的思路一致。

## 内存分配

- 默认取可用物理内存的 85%，按块大小切成多块，**整轮不释放**（不换页才测得准）。
- 优先大页（2 MB，需「锁定页面」特权）。任意一块分配失败就**整体回退** 4 KB 页，
  界面与报告都标注实际用了哪种。
- 非管理员通常拿不到大页，界面顶部会提示。

开启大页（可选，需管理员）：

```
secpol.msc → 本地策略 → 用户权限分配 → 锁定页面 → 添加当前用户 → 重新登录
```

## 报错日志

每次测试在 `logs\<时间戳>-<模式>\` 下生成：

| 文件 | 内容 |
|---|---|
| `session.json` | 机器信息（CPU / 主板 / BIOS / 内存条插槽容量频率型号）、NUMA、大页粒度、参数、版本 |
| `errors.csv` | 每条错误一行：时间、线程、轮次、模式、块、块内偏移、绝对地址、期望值、实际值、位宽 |
| `errors.jsonl` | 同上，JSON Lines；另含 `kind` 为 whea / external-tool 的记录 |
| `report.md` | 导出报告：结论先行 + 机器信息 + 错误明细 + WHEA + 判读依据与局限 |

**每行写完立即 Flush**——压测会把机器压到蓝屏，缓冲区里的错误等于没记。
CSV 带 UTF-8 BOM，Excel 打开中文不乱码。

## WHEA 监控

测试期间每 15 秒查询系统事件日志的 `Microsoft-Windows-WHEA-Logger`。
有新增事件时：界面红条告警、写入 JSONL、进报告，并**即使内存零错误也判不通过**。

事件语义：**17** 是已纠正的硬件错误（最常见 `Component: PCI Express Root Port`）；
**47** 是已纠正的内存/机器检查（`Component: Memory`，真正的 DIMM 定位藏在 CPER 结构里）；
**18 / 46** 才是不可纠正版本。纠正不免费——本底速率正常、速率爬升即余量在流失。

事件日志读不到时报告写「未监控」，不会写成「无事件」。
挂起/唤醒会补偿查询（`PowerModeChanged`），但**睡眠唤醒路径尚未实测**（需要能睡眠的机器）。

## 外部工具

y-cruncher 页只做「发现 → 运行 → 归档输出」，**不下载任何二进制**。
工具由用户自行获取并放进 `tools\ycruncher\`。

| 工具 | 定位 | 官方地址 |
|---|---|---|
| y-cruncher | 非缓存负载，补足纯内存校验测不到的 IMC / 缓存层次 | https://www.numberworld.org/y-cruncher/ |

解析优先级：**手动指定的路径 → `tools\<id>\` → 机器上其他常见位置**（`C:\Tools`、`C:\Program Files`、`C:\y-cruncher`）。
已装在别处的工具不用搬家。手动指定的路径记进 `tools\paths.json`，重启后仍有效。
压缩包自带版本号子目录（如 `y-cruncher v0.8.6\`）也能识别。

`tools\` 锚定在**程序自身目录**而非仓库根，这样整个文件夹复制到别的机器上，投放点跟着走。

### y-cruncher 一键压测

档位下拉框选好直接点「运行」，参数按物理内存自动算：

| 档位 | 算法 | 每项时长 | 总时限 | 内存 |
|---|---|---|---|---|
| 快速筛查 | N63 + VT3 | 60 s | 15 分钟 | 物理内存 70% |
| 标准验证 | FFTv4 + N63 + VT3 | 120 s | 1 小时 | 物理内存 85% |
| 深度驻留 | FFTv4 + N63 + VT3 | 300 s | 4 小时 | 物理内存 90% |

N63 与 VT3 是社区公认对内存 / IMC 最敏感的两个算法，FFTv4 补足浮点与缓存层次路径。
`-M` 按物理总量向下取整到 GiB，最小 4 GiB，上限 90%。

**三个 y-cruncher 特有的坑，程序都已处理：**

1. **杀父进程杀不掉负载**。`y-cruncher.exe` 只是启动器，真正吃内存的是
   `Binaries\<型号> ~ <代号>.exe`。实测杀父进程后子进程仍存活并占着 21 GB。
   程序启动前记录已有负载进程 PID，停止时只杀「新出现」的那些，再杀父进程，最后复查残留并写进日志。
2. **同名日志会追加**。`logfile:` 指向已存在的文件时是追加而非覆盖，会把上一次的
   `Iteration:` / `Test Finished` 混进本次结果。程序每次生成
   `ycruncher-stress-yyyyMMdd-HHmmss.log`，只归档这一个文件。
3. **状态只能从日志读**。运行中的当前算法、迭代轮次、已跑时长来自对本次日志的增量解析
   （`Iteration: N` 与 `Running <算法>:`）。stress 模式不认 `-o`，输出走 `WriteConsoleW`，
   直接重定向只能拿到空文件（[issue #25](https://github.com/Mysticial/y-cruncher/issues/25) 至今 open）。

### 停止是按 id 精确定位的

程序在启动前记录该工具进程与已有同类进程的 PID，停止时只清理本次新起的那些。
**你在程序外自己开的 y-cruncher 不会被误杀。**

页面顶部那个「停止」只管自研压测，不会连带停掉外部工具。
交叉验证运行期间它会重新启用，用于中止 y-cruncher。

## 已知局限

- 大页不可用时回退 4 KB，覆盖率下降，界面与报告都会标注。
- 只能测用户态可分配的物理内存；内核占用、保留区测不到。
- 温度相关的不稳定需要配合 GPU 负载或长时间运行才复现——压测时内存是凉的。
- WHEA 只有合成路径被验证过，真实事件在本机从未出现过。
- 睡眠唤醒的 WHEA 补偿代码已实现但**未实测**。
- 不改系统设置、不装驱动、不需要重启。

## 目录

```
MemStress\               （仓库根，克隆后目录名可自定）
├─ MemStress.exe          构建产物直接落在这里，双击即运行
├─ MemStress.dll / .deps.json / .runtimeconfig.json
├─ src\MemStress\
│  ├─ Core\               引擎、分配器、日志、WHEA、报告、外部工具、机器信息
│  ├─ MainWindow.*.cs     界面各职责分文件（选项、报告、交叉验证、提权、日志…）
│  ├─ SelfTest*.cs        无界面自检
│  └─ AppPaths.cs
├─ scripts\fake-whea.ps1  合成 WHEA 事件，供 --wheaselftest 验证解析链路
├─ scripts\matrix-test.sh 63 格故障注入矩阵（7 模式 × 9 故障，CI 跑这个）
├─ .github\workflows\ci.yml 构建 + 六项自检 + 矩阵 + 版本号校验
├─ logs\                  每次测试一个会话目录（不入库）
└─ tools\                 外部工具投放点（不入库；第三方二进制不随仓库分发）
```

## 构建与验证

```bash
# 要求 0 错误 0 警告
dotnet build src/MemStress/MemStress.csproj -v q --nologo

# 六项自检全部应 exit=0
MemStress.exe --selftest --seconds 5 --percent 0.04 --chunk 64
MemStress.exe --selftest --seconds 5 --percent 0.04 --chunk 64 --inject-error
MemStress.exe --toolselftest
MemStress.exe --wheaselftest
MemStress.exe --yctailtest
MemStress.exe --p0test

# 63 格故障注入矩阵（约 6 分钟）
bash scripts/matrix-test.sh
```

CI 在 GitHub Actions 上跑上述全部步骤（GUI 冒烟除外——托管 runner 没有交互桌面，
UIA 自动化无法工作，这是显式取舍）。

改完代码请按 [CHANGELOG.md](CHANGELOG.md) 的四段式记录：症状 / 根因 / 改法 / 验证。

## 许可与免责

[MIT](LICENSE)。第三方工具（如 y-cruncher）由用户自行获取，不随本仓库分发。

压测会让内存与 CPU 长时间满载，可能导致系统不稳定或数据丢失。
**请自行评估风险，不要在承载重要工作的机器上直接跑满占用。**
