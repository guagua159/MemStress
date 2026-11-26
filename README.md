# MemStress — Windows 内存压力测试工具

对标 [TestMem5](https://testmem5.com/)（社区最常用的快速筛查工具）与 [rictirse/RunMemtestStartup](https://github.com/rictirse/RunMemtestStartup)（.NET 启动器），
用 .NET 10 + WPF 实现，自带图形界面与结构化报错日志。当前版本 **1.9**（见 [CHANGELOG.md](CHANGELOG.md)）。

自研六种故障模型压测引擎（MovingInversion / AddressAsData / RandomFill / BlockCopy /
WalkingOnes / Checkerboard / ModuloX），可选 y-cruncher 交叉验证，WHEA 硬件错误监控，
报告区分「通过 / 失败 / 未测」三态。零 NuGet 依赖。

**尚未做**：多实例分核、SMBIOS 原始表读取、外部工具自动下载。

> **要改代码或接手维护？** 先看 [MAINTENANCE.md](MAINTENANCE.md)——代码地图、「我要改 X 该动哪个文件」速查、改完必跑的验证命令、踩过的坑。
> **接手待办与路线？** 见 [docs/HANDOFF.md](docs/HANDOFF.md) 与 [docs/handoff-plan.md](docs/handoff-plan.md)。
> **改完要记一笔？** 按 [docs/CHANGELOG-GUIDE.md](docs/CHANGELOG-GUIDE.md) 的四段式写进 [CHANGELOG.md](CHANGELOG.md)。

## 许可证

[MIT](LICENSE)。第三方工具（如 y-cruncher）由用户自行获取，不随本仓库分发。

## 快速开始

```bash
cd E:\MemStress
dotnet build src/MemStress/MemStress.csproj
```

构建产物直接落在仓库根，双击即可运行：

```
E:\MemStress\MemStress.exe
```

exe 与 `tools\`、`logs\` 同级，整个 `E:\MemStress` 文件夹就是自洽的可分发单元——复制到任何机器上都能直接用，`tools\` 投放点跟着走。

无界面自检（CI / 验收用）：

```bash
# 基本跑通：跑 8 秒，占 20% 可用内存
MemStress.exe --selftest --seconds 8 --percent 0.2

# 验收报错链路：故意造一条错误，检查 CSV / JSONL / 报告里都能看到
MemStress.exe --selftest --seconds 8 --percent 0.2 --inject-error

# 验收外部工具编排：不需要装任何真实工具，用系统自带程序验证「拉起→捕获→归档→落盘」
MemStress.exe --toolselftest

# 验收 WHEA 解析链路：用合成事件，真实机器上未必有 WHEA 事件可测
MemStress.exe --wheaselftest
```

自检退出码：`0` 通过，`2` 失败。

📖 图文说明书：[docs/MemStress-使用说明书.docx](docs/MemStress-使用说明书.docx)（12 章，9 页，含参数表、判读规则、FAQ）

## 界面

| 页签 | 内容 |
|---|---|
| 压测 | 档位、内存占用、线程数、块大小、轮数/时长、大页开关；实时指标（时长/吞吐/累计读写/轮次/错误）；错误表格；导出报告 |
| 运行日志 | 运行过程逐行日志，可清空、可打开日志目录 |
| 外部工具 | y-cruncher / TestMem5 的发现、指定路径、运行与输出归档 |
| 方法与局限 | 判读规则、已知局限、参考来源 |

![运行中的界面](docs/screenshot-gui-running.png)

## 测试模式

对照 TestMem5 的四个子测试设计：`RefreshStable`（写非常量 pattern 后回读校验，唯一做完整性判定）、`SimpleTest`（通用读写打满内存 I/O）、`MirrorMove` / `MirrorMove128`（把测试块里外翻转、按奇偶条纹多趟搬运，破坏控制器对访问模式的自适应）。

| 模式 | 压什么 | 对应 TM5 |
|---|---|---|
| MovingInversion | 写 A → 写 ~A → 读回，反复搬运整块数据，压数据总线与写缓冲 | SimpleTest |
| AddressAsData | 把块内偏移写进内存再校验，专抓地址译码错误 | 无直接对应 |
| RandomFill | 种子生成随机字节流写入后整块比对，覆盖面最广、最慢 | RefreshStable |
| BlockCopy | 相邻块互为源和目的整块拷贝再校验，压内存控制器与缓存一致性 | MirrorMove |
| WalkingOnes | 单比特位在字内依次置位，压位线故障与相邻位串扰（每块只测前 32 MiB） | 无直接对应 |
| Checkerboard | 交替 0x55/0xAA 填充，抓相邻存储单元耦合故障 | SimpleTest |

档位：快速筛查（2 种）/ 标准（4 种，对应 TM5 `extreme@anta777` 的定位）/ 深度（6 种，对应 `absolutnew` 的定位）/
彻底（7 种，额外含 Modulo-X，慢约 4 倍，专查被缓存与写缓冲掩盖的错误）。

TM5 的强度完全由 cfg 决定，两者是不同定位而非强弱两档：`extreme@anta777` 为 Time 500%、Cycles 3、窗口 1408MB、粒度 16MB、保留 128MB，Test0 占 100% 权重；`absolutnew` 为 Time 1250%、Cycles 3、窗口 1536MB、粒度 64MB、保留 512MB，序列以 Test15 反复收尾。官方 FAQ 的流程是先 3 轮 Extreme 再 3 轮 Absolut。

## 报错日志

每次测试在 `logs\<时间戳>-<模式>\` 下生成：

| 文件 | 内容 |
|---|---|
| `session.json` | 机器信息、内存条（插槽/容量/频率/型号）、参数、版本 |
| `errors.csv` | 每条错误一行：时间、线程、轮次、模式、块、块内偏移、绝对地址、期望值、实际值、位宽 |
| `errors.jsonl` | 同上，JSON Lines，便于机器解析；另含 `kind` 为 whea / external-tool 的记录 |
| `report.md` / `report.html` | 导出报告：结论先行 + 机器信息 + 错误明细 + WHEA + 判读依据与局限 |

**每行写完立即 Flush**——压测会把机器压到蓝屏，缓冲区里的错误等于没记。CSV 带 UTF-8 BOM，Excel 打开中文不乱码。

## WHEA 监控

测试期间每 15 秒查询系统事件日志的 `Microsoft-Windows-WHEA-Logger`。有新增事件时：界面红条告警、写入 JSONL、进报告，并**即使内存零错误也判不通过**。

事件语义：**17** 是已纠正的硬件错误（最常见 `Component: PCI Express Root Port`）；**47** 是已纠正的内存/机器检查（`Component: Memory`，真正的 DIMM 定位藏在 CPER 结构里）；**18 / 46** 才是不可纠正版本。纠正不免费——本底速率正常、速率爬升即余量在流失。

事件日志读不到时报告写「未监控」，不会写成「无事件」。

## 内存分配

- 默认取可用物理内存的 85%，按 `块大小` 切成多块，**整轮不释放**（不换页才测得准）。
- 优先大页（2 MB，需要「锁定页面」特权）。任意一块大页分配失败就整体回退 4 KB 页，界面与报告都标注实际用了哪种。
- 非管理员运行时通常拿不到大页，界面顶部会提示。

开启大页（可选，需要管理员）：

```
secpol.msc → 本地策略 → 用户权限分配 → 锁定页面 → 添加当前用户 → 重新登录
```

## 外部工具

`外部工具` 页只做「发现 → 运行 → 归档输出」，**不下载任何二进制**。这是刻意保守的选择，不是授权上的硬性禁止：

| 工具 | 定位 | 授权/分发结论 | 官方地址 |
|---|---|---|---|
| y-cruncher | 非缓存负载，补足纯内存校验测不到的 IMC / 缓存层次 | 授权页明写可 free of charge 再分发，但限定非商业且条款可变更 → 第一版不打包 | https://www.numberworld.org/y-cruncher/ |
| TestMem5 | 社区基准，加载 anta777 配置快速筛查 | 仓库 license 为 null，未声明授权 → 不可分发 | https://testmem5.com/ |

**投放点优先**：程序在自己所在目录下建好 `tools\<id>\`（当前只有一个外部工具：`tools\ycruncher`），
首次启动自动创建，并附一份 `把第三方工具放在这里.txt`。下载的工具直接解压进去即可，
压缩包自带版本号子目录（如 `y-cruncher v0.8.6\`）也能识别。界面每个工具都有「打开工具目录」按钮，直接跳到该文件夹。

解析优先级：**手动指定的路径 → `tools\<id>\` → 机器上其他常见位置**（`C:\Tools`、`C:\Program Files`、`C:\TestMem5`、`C:\y-cruncher`）。
已装在别处的工具不用搬家，这是可选便利，不是必须。手动指定的路径记进 `tools\paths.json`，重启后仍有效。

注意 `tools\` 锚定在**程序自身目录**而非仓库根，这样整个文件夹复制到别的机器上，投放点跟着走。

**文件名不一定和工具名一致**，只认一个名字会把正确放置的文件误判成「未安装」。
当前只有一个外部工具，候选文件名：

| 工具 | 识别的文件名 |
|---|---|
| y-cruncher | `y-cruncher.exe` |

（1.7 起已移除 TestMem5 支持：它是 GUI 程序、无静默参数、必须管理员运行，
自动化成本高于收益。自研引擎覆盖同样的故障模型。）

**两类工具的启动方式不同**，程序会自动区分：

- **控制台程序**（y-cruncher）：流式捕获 stdout/stderr，结束后归档本次的 `logfile:` 日志。
- **窗口程序**（TestMem5）：不能用 stdout 重定向启动（进程会起不来，实测确认）。改为正常开窗、等用户关窗，
  结果从它自己的 `Log.txt` 归档。这类测试按小时计，超时给到 6 小时。

### 每个工具自己的启停按钮

每张工具卡片右侧的按钮是二合一的：没跑时显示绿色的「▶ 运行」，跑起来就变成红色的「■ 停止」，
再点一下停的就是这个工具自己。各工具互不干扰——y-cruncher 在跑的时候，TestMem5 的按钮仍是「运行」。

页面顶部那个「停止」只管自研压测，不会连带停掉外部工具。这样点哪个、停了什么，不会混。

停止是按工具 id 精确定位的：程序在启动前记录该工具进程与已有的同类进程 PID，
停止时只清理本次新起的那些。所以你在程序外自己开的 y-cruncher 不会被误杀。

### y-cruncher 一键压测

工具卡片上有一个档位下拉框，选好直接点「运行」，参数由程序按物理内存自动算，不用自己拼命令行：

| 档位 | 算法 | 每项时长 | 总时限 | 内存 |
|---|---|---|---|---|
| 快速筛查 | N63 + VT3 | 60 s | 15 分钟 | 物理内存 70% |
| 标准验证 | FFTv4 + N63 + VT3 | 120 s | 1 小时 | 物理内存 85% |
| 深度驻留 | FFTv4 + N63 + VT3 | 300 s | 4 小时 | 物理内存 90% |

算法选择依据：N63 与 VT3 是社区公认对内存 / IMC 最敏感的两个算法，FFTv4 补足浮点与缓存层次路径。
`-M` 按 `GlobalMemoryStatusEx` 的物理总量向下取整到 GiB，最小 4 GiB，上限 90%。

**三个只有 y-cruncher 才有的坑，程序都已处理：**

1. **杀父进程杀不掉负载**。`y-cruncher.exe` 只是启动器，真正吃内存的是
   `Binaries\<型号> ~ <代号>.exe`（本机为 `24-ZN5 ~ Komari.exe`）。实测杀掉父进程后子进程仍存活并占着 21 GB。
   程序在启动前记录已有负载进程的 PID，停止时只杀「新出现」的那些，再杀父进程，最后复查残留并如实写进日志。
2. **同名日志会追加**。`logfile:` 指向已存在的文件时 y-cruncher 是追加而非覆盖，
   会把上一次的 `Iteration:` / `Test Finished` 混进本次结果。程序每次生成
   `ycruncher-stress-yyyyMMdd-HHmmss.log`，只归档这一个文件，不碰目录里的历史日志。
3. **状态只能从日志读**。运行中的当前算法、迭代轮次、已跑时长，来自对本次日志的增量解析
   （`Iteration: N` 与 `Running <算法>:`），不读目录里其他日志。

**TestMem5 必须用管理员权限运行**。它靠 AWE / 锁定内存页分配大块内存，非管理员启动会立刻报
`An error -51472 occurred in the program` 并停止（实测确认）。程序会在未提权时提前拦截并说明原因，
工具卡片上也标了「需管理员权限运行」，不用等它报错才发现。

不确定程序到底找了哪些地方？直接问它：

```bash
MemStress.exe --toolscan
```

运行输出原样落到当前日志会话目录（`<工具名>.stdout.log` / `.stderr.log`），退出码写进 `errors.jsonl`。

**y-cruncher 的两个坑已处理**：stress 模式不认 `-o`，且输出走 `WriteConsoleW`，直接重定向只能拿到空文件（[issue #25](https://github.com/Mysticial/y-cruncher/issues/25) 至今 open）。本工具运行时会自动加上官方途径 `logfile:"ycruncher-stress.log"` 启动选项，结束后把该日志一并归档进会话目录。

## 验收状态

| 判据 | 状态 |
|---|---|
| `dotnet build` 零错误 | ✅ 0 警告 0 错误 |
| 界面能起、开始后指标跳动 | ✅ 管理员模式实测 16.39 GiB/s，223.2 GiB，6 轮 |
| 停止后内存回落 | ✅ 可用内存 21955 → 13314 → 22090 MB |
| 停止后导出报告数据正确 | ✅ 202.0 GiB / 7 轮（曾因引擎已释放写出 0，已修复） |
| `--inject-error` 后 CSV/JSONL/报告各有一条 | ✅ `[selftest] PASS` |
| `--toolselftest` 外部工具编排 | ✅ 退出码 7 正确捕获，stdout/stderr 落盘，日志归档，JSONL 记录 |
| `--wheaselftest` WHEA 解析与判定 | ✅ 2 条事件正确解析，零错误仍判不通过 |
| `--toolscan` 外部工具发现 | ✅ 自动找到 `C:\Tools\y-cruncher\y-cruncher.exe` |
| 真实 y-cruncher 运行 | ✅ v0.8.7 输出实时回传，`logfile:` 生效，会话目录留 stdout/stderr |
| y-cruncher 三档预设 | ✅ 快速/标准/深度均可跑通，`-M` 实测占用与计算值偏差 <10% |
| y-cruncher 停止 | ✅ 点卡片上的「■ 停止」后父子进程全部退出，无残留 |
| 每工具独立启停 | ✅ 外部工具与自研压测互不影响，各自有独立按钮 |
| 不误杀外部实例 | ✅ 程序外自行启动的 y-cruncher 在停止卡片任务后仍存活 |
| 关闭窗口带走子进程 | ✅ 子进程不再变孤儿（曾会残留并继续吃内存） |
| 断网可完整跑完并导出报告 | ✅ 仅用 BCL 与系统自带的 powershell/explorer，无 NuGet 依赖 |
| `docs/research.md` 结论带来源 | ✅ 6 节 1970 字，21 个链接，未查到的 3 项已明确列出 |
| 管理员模式下 SMBIOS 原始表 | ✅ 可读，2453 字节，SMBIOS 3.8 |

## 已知局限

- 走 1 只测每块前 32 MiB（64 遍读写代价高），其余模式全量；采样范围写在报告里，不假装全量。
- 大页不可用时回退 4 KB，覆盖率下降，已在界面和报告标注。
- 只能测用户态可分配内存，内核占用与保留区测不到。
- 温度相关的不稳定需要配合 GPU 负载或长时间运行才复现。
- 不改系统设置、不装驱动、不需要重启。

## 目录

```
E:\MemStress
├─ MemStress.exe          构建产物直接落在这里，双击即运行
├─ MemStress.dll / .deps.json / .runtimeconfig.json
├─ src\MemStress\
│  ├─ Core\           引擎、分配器、日志、WHEA、报告、外部工具、机器信息
│  ├─ MainWindow.xaml(.cs)
│  ├─ SelfTest.cs     无界面自检
│  └─ AppPaths.cs
├─ scripts\gui-smoke.ps1   UI 自动化冒烟（启动测试 + 截图）
├─ scripts\gui-act.ps1     UI 自动化（切页签 + 点按钮 + 截图）
├─ scripts\gui-selftest.ps1 GUI 全流程冒烟自检（需交互桌面，不进 CI）
├─ scripts\matrix-test.sh  63 格故障注入矩阵（CI 跑这个）
├─ scripts\fake-whea.ps1   合成 WHEA 事件，供 --wheaselftest 验证解析链路
├─ scripts\make-manual.py  生成说明书 DOCX（成品不入库）
├─ .github\workflows\ci.yml 构建 + 七项自检 + 矩阵 + 版本号校验
├─ docs\research.md        调研记录（6 节，21 个来源）
├─ docs\HANDOFF.md         交接文档（待办清单 + 一周优化路线 + 踩坑汇总）
├─ docs\handoff-plan.md    HANDOFF 十项的可执行方案
├─ docs\audit-2026-11.md   P0-P3 审计与三轮审查结论
├─ docs\MemStress-使用说明书.docx  图文说明书（由脚本生成，不入库）
├─ logs\                   每次测试一个会话目录（不入库）
└─ tools\                  外部工具投放点（不入库；第三方二进制不随仓库分发）
```
