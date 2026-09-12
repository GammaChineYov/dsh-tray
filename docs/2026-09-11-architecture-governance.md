---
created_at: 2026-09-11
update_at: 2026-09-11
summary: "dsh 托盘（dsh-chat-popup）架构治理评估：本地模型两轮共 4+3 路并行评审 + 静态取证 + 最小复现三重证据，P0/P1/P2 问题清单、目标模块边界与增量迁移路线。问题项使用稳定 ID（P0-1 / P1-3 / P2-9），可增删不重编号"
status: open
priority: P0
---

# dsh 托盘架构治理评估

> 结论先行：**这个托盘该治理了，但顺序不能反** —— 先把"承重结构缺陷"（线程调度锚点、`svc.log` 跨线程零同步、无界增长）修掉，再做模块拆分。反过来做，会把现有缺陷原样固化进新结构里。

**问题项一律用稳定 ID**（`P0-1` / `P1-3` / `P2-9`…），不再用 `3.1 / 3.2` 这种位置序号 —— 位置序号一插入新条目就要全文重编号，是文档层面的同一种债。

---

## 0. 结论摘要

| 维度 | 现状 |
|---|---|
| 规模 | 14 个 `.cs`、**4485 行**、无测试工程 |
| 最胖单文件 | `Program.cs` **1778 行 / 143.9 KB，内含 7 个类型** |
| 最胖单类 | `TrayApp` **1469 行、129 个字段、152 个方法**（占全项目 55% 的方法） |
| 最胖构造函数 | `TrayApp()` **197 行**（L192-388）、`Main()` 148 行（L1629-1776） |
| 第二大文件 | `ModelPerf.cs` **1035 行、17 个类型**（数据模型+持久化+HTTP 基准+WinForms 面板+自检同处一室） |
| 公共 API 面 | `ModelPerf` public 方法 **35/64（55%）** |
| 并发原语 | `Program.cs`：Task.Run×8、lock×7、volatile×8、Interlocked×6、Process.Kill×4、Dispose×7 |

**必须先解决的四个问题**（详见 §3）：

1. **P0-1｜跨线程 marshal 锚点未就绪**（**两处，同一机制**）—— `logForm`（锚点之一）从不 `Show`，句柄为空时 `InvokeRequired` **恒为 `false`**，后台线程会**直接在非 UI 线程上执行 UI 操作**，并顺手把句柄**永久绑到线程池线程**；此后所有 UI 更新静默失效。`LogForm.AppendTo()`（`Program.cs:67-80`）锚在 `box`/`dshBox` 上，是**第二个同型缺陷**，而且会被"启动 DSH"这条路径**先命中**。**已用最小工程复现**（§3 · P0-1）。
2. **P0-2｜`renderSessionItem` 四个连续 `return`** —— JS 里只有第一条生效，瘦客户端的会话列表项渲染成残缺空壳（`ThinChatForm.cs:730-733`）。
3. **P1-1｜`Service.log` 三写一读、零同步** —— 同一 `StringBuilder` 被 **stdout 读线程 + stderr 读线程 + 线程池（`Log()`）** 并发写、被 UI 线程并发读。`StringBuilder` 明确不是线程安全的，线程池侧的异常会直接终止进程（会留 `tray-ex.log` FATAL 行 —— **目前该文件不存在**，说明是潜在雷、尚未引爆，见 §3 · P1-1）。
4. **P1-2/P1-3｜资源无界增长** —— `Service.log`（StringBuilder）与日志窗口两个 `RichTextBox` 都只增不减，长驻托盘内存随时间单调上升。

---

## 1. 取证方法与可复现性

本次结论来自**三路互相独立的证据**，任何一条都不单独作为定论：

| 证据路 | 手段 | 产物 |
|---|---|---|
| ① 静态取证 | 自写 C# 解析脚本（含注释/字符串净化、花括号配平），统计类型/方法/字段/并发原语/跨文件耦合 | `docs/temp/gov/inventory.py`、`analysis2.py` → `inventory.json`、`trayapp.json` |
| ② 本地模型评审 | 8081（Qwen3.6-35B-A3B，262K ctx / 4 slot）并行评审，各喂不同文件切片，**行号内联**便于回查。**两轮**：第 1 轮 r1-r4（默认思维链，3 路被烧空）→ 第 2 轮 r1-r3（`enable_thinking=false`） | `docs/temp/gov/out_r*.md`、`out2_r*.md` |
| ③ 最小复现 | 独立 WinForms 工程复现 `InvokeRequired` 语义 | `docs/temp/gov/ivtest/` |
| ④ 运行时反证 | 检查全局异常兜底留下的 `tray-ex.log`（`Program.cs:1633-1635` 写入 exe 同目录） | **该文件不存在** → 见表末说明 |

**纪律：模型提出的每一条结论都回查源码**。核实不成立的条目**照样记录在案**（§4），避免后人拿着错单子去改代码 —— 这是本次评审里最容易漏掉、也最值钱的一步。两轮下来严格命中率都稳定在 **~20%**（§4）。

复跑方式：

```bash
cd C:/Users/Landrom/dsh-chat-popup/docs/temp/gov
python inventory.py      # ① 结构取证
python analysis2.py      # ① 职责/耦合取证
python runner.py r1 r2 r3 # ② 重跑本地模型评审（需 8081 在线；默认已关思维链）
cd ivtest && dotnet run -c Release   # ③ 复现 InvokeRequired 语义
```

**第 ④ 路的读数**：全局兜底 `Application.ThreadException` 与 `AppDomain.UnhandledException` 都会追加写 `tray-ex.log`（exe 同目录，即 `publish/`）。实测 **`publish/`、`publish-next/`、`publish-new/` 三处均无该文件**。含义：

- P0-1 **本来就是静默失效**（不抛异常、只丢更新）→ 无日志痕迹**符合预期**，不能反过来当成"没这个问题"的证据；
- P1-1（`svc.log` 竞态）若真炸过，会在 `tray-ex.log` 留 `FATAL` 行 → **没有痕迹 = 尚未引爆**，所以 P1-1 定性为**潜在缺陷**，不是现网故障。这条"负证据"值得留档，避免以后有人把潜在竞态说成"已经崩过好几次"。

---

## 2. 现状体检（客观数据）

> **口径说明**：行数按 `len(content.split('\n'))`（即把文件末尾换行也计作一行），与下文引用的行号一致，比 `wc -l` 大 1；字节数为**磁盘实际大小**。
> （`inventory.json` 里的 `bytes` 字段是 CRLF→LF 归一化后的长度，对 `Program.cs` 这类 CRLF 文件会少 N 字节 —— 那是脚本口径的产物，不要当成真实体积。）

### 2.1 文件规模与职责

| 文件 | 行 | 字节 | 内含类型 | 方法数 | public |
|---|---:|---:|---|---:|---:|
| `Program.cs` | 1778 | 143917 | LogForm, TrayApp, SvcMenu, RecentSession, TaskbarWatcher, NativeMethods, Program2 | 152 | 25 |
| `ModelPerf.cs` | 1035 | 52563 | *(17 个：PerfStore, PerfSampler, LlamaLogParser, BenchRunner, PerfPanel, PerfProbe …)* | 64 | 35 |
| `ThinChatForm.cs` | 743 | 30788 | ThinChatForm, Page | 15 | 2 |
| `PluginCenter.cs` | 220 | 12501 | PluginEntryInfo, DevPluginInfo, PluginInventory, PluginCenter | 13 | 5 |
| `PluginManagerForm.cs` | 136 | 6520 | PluginManagerForm, State | 6 | 2 |
| `LaunchArgs.cs` | 125 | 6539 | LaunchResult, GpuSelection, LaunchArgs | 7 | 6 |
| `Config.cs` | 73 | 4372 | ServiceConfig, AppConfig, Config | 2 | 2 |
| `HwInfo.cs` | 72 | 3586 | HwInfo | 4 | 2 |
| `SysInfo.cs` | 67 | 4218 | SysInfo, FILETIME, MEMORYSTATUSEX | 3 | 2 |
| `DshRpc.cs` | 64 | 3379 | DshRpc | 2 | 1 |
| `GpuInfo.cs` | 64 | 2902 | Gpu, GpuInfo | 3 | 3 |
| `DshAuth.cs` | 55 | 2551 | DshAuth | 3 | 1 |
| `AutoStart.cs` | 34 | 1090 | AutoStart | 3 | 2 |
| `Service.cs` | 19 | 1497 | Service | 0 | 0 |

**判读**：`Program.cs` + `ModelPerf.cs` 两个文件占 **63%** 的代码量，且**各自内部都混装了 UI、IO、领域、自检**。这不是"文件太大"，而是**没有模块边界** —— 文件大小只是症状。

### 2.2 最长方法（超长方法 = 隐藏的上帝类）

| 行数 | 位置 | 方法 |
|---:|---|---|
| **197** | `Program.cs:192-388` | `TrayApp()` —— 构造函数里同时做：配置载入、控件构建、菜单组装、服务初始化、定时器启动 |
| **148** | `Program.cs:1629-1776` | `Main()` —— 9 个自检开关 + 基准参数的参数分发 |
| 124 | `ModelPerf.cs:890-1013` | `PerfProbe.Run()` |
| 66 | `Program.cs:1369-1434` | `Tick()` —— 每秒一次：服务状态巡检 + GPU 发现 + CPU 温度 + DSH 状态 + 日志搬运 |
| 55 | `Program.cs:982-1036` | `EnsureLlamaProvider()` |
| 51 | `ThinChatForm.cs:294-344` | `renderWorkspaceSessions()` |

### 2.3 跨文件耦合（引用次数）

```
Program.cs   → LaunchArgs×12  PluginCenter×14  Config×6  DshRpc×3  GpuSelection×3
               AppConfig×3    AutoStart×3      PerfPanel×3  PerfRuntime×2  PerfProbe×2
               GpuInfo×2      PluginManagerForm×3
ModelPerf.cs → Service×5  LaunchResult×2          ← 分层倒置，见 P1-6
ThinChatForm → DshRpc×2      DshRpc → DshAuth×2
```

`Program.cs` 引用 **12 个不同的类型**，是典型的"什么都得知道"。

### 2.4 `TrayApp` 实际承载的职责域（为拆分方案提供依据）

第 2 轮 r1 给出的职责域划分**经我抽查行号全部对得上**，可直接作为拆分依据（已并入 §5/§6）：

| # | 职责域 | 行号 | 代表成员 |
|---|---|---|---|
| A | 托盘菜单构建 | L127-388 | `menu`/`dshMenu`/`gpuMenu`/`ctxMenu`/`splitMenu`/`kvMenu`/`svcMenus` … |
| B | 模型服务生命周期 | L616-693 | `Start`/`Stop`/`RestartSvc`/`StopAll`/`RestartAll`/`ReloadSvcConfig`/`Adopt` |
| C | DSH 服务生命周期 | L695-770 | `DshStart`/`DshStop`/`DshRestart`/`DshKill`/`DshUp` |
| D | 模型状态巡检 | L1369-1434 | `Tick()` 的 `svcTick` 节拍、`HealthUp`/`ProbeLlamaProps`/`ProbeBuildInfo` |
| E | 系统信息采集 | L1402-1413 | `GpuInfo.Discover`/`HwInfo.CpuTemp`/`SysInfo.CpuPercent`/`SysInfo.Mem` |
| F | 日志窗口管理 | L775-942 | `MakeLogForm`/`ShowLogWin`/`DshTailLoad`/`TailLog`/`ClearDshLogFiles` |
| G | 会话打开与配置同步 | L1040-1133 | `OpenSession`/`OpenSessionBuiltin`/`SyncSessionConfig`/`OpenPop`/`OpenThin` |
| H | 最近会话管理 | L1135-1342 | `ScheduleRecentRefresh`/`ReadRecent`/`PollSessionStates`/`RebuildRecentMenu` |
| I | 插件管理 | L1073-1106 | `OpenPluginManager`/`ApplyPluginSettings` |
| J | 孤儿锁清理 | L811-833 | `HealProfileOrphanLock`/`ProfileLockPath` |
| K | WorkBuddy 环境清洗 | L835-880 | `ScrubWorkBuddyEnv`/`StripWorkBuddyShim` |
| L | 配置持久化 | L390-425 | `LoadCfg`/`SaveCfg`/`QueueSavePos`/`SaveAllPos` |
| M | 自检探针 | L1451-1594 | `LogWinProbe`/`DumpMenuText`/`PluginScanProbe`/`RpcProbe`/`SvcMenuProbe` |
| N | 退出/重启控制 | L1436-1449 | `ExitApp`/`ExitFromSignal`/`RestartTray` |
| O | UI 辅助 | L497-615 | `PortUp`/`HealthUp`/`Dot`/`RefreshDshUi`/`RefreshSvcMenus`/`StatusTip` |

**15 个职责域挤在一个类里** —— 但注意：这些"域"**不能直接一比一变成 15 个模块**（见 §5，先按依赖方向归并成 3 层）。

---

## 3. 问题清单（逐条已核实）

严重度定义：**P0** = 会静默瘫痪功能或明确功能失效；**P1** = 可达的坏体验、长跑劣化或潜在崩溃；**P2** = 设计债，现在不改不会炸。

### P0-1 【P0】跨线程 marshal 锚点"未就绪"—— 两处同型缺陷，会静默瘫痪整条 UI 更新链

**现象 · 锚点一（`Ui()`）**

```csharp
// Program.cs:771
// 后台执行（菜单点启停/重启不阻塞 UI 线程）；UI 更新一律经 Ui() 回 UI 线程
void Bg(Action a){ try{ Task.Run(a); }catch{} }                                    // ← L772
void Ui(Action a){ try{ if(logForm!=null&&logForm.InvokeRequired){ logForm.BeginInvoke(a); } else a(); }
                   catch{ try{a();}catch{} } }                                      // ← L773
```

注释写着"UI 更新一律经 `Ui()` 回 UI 线程"，但**这个保证不成立**。

**现象 · 锚点二（`LogForm.AppendTo()`，此前被漏掉）**

```csharp
// Program.cs:67-80
void AppendTo(RichTextBox b, string s){
  ...
  try{
    if(b.IsDisposed) return;
    if(b.InvokeRequired){ b.BeginInvoke(...); }        // ← L77：锚在 box / dshBox 自身
    else { b.AppendText(s); if(AutoFollow) ScrollToEnd(b); }
  }catch{}
}
```

`AppendTo` 的锚点选得比 `Ui()` **更讲究**（锚在自己要写的那个控件上，不是锚在别人身上）。但这**换汤不换药**：句柄没建时 `b.InvokeRequired` 同样恒为 `false`，同样直接落 `AppendText`。**这是与 P0-1 完全同型的缺陷，只是换了个锚点。**

**机制（已最小复现）**

WinForms 的 `Control.InvokeRequired` 在**句柄尚未创建**时恒返回 `false`。而 `logForm` 只在构造函数里 `new`（`Program.cs:207`），**不 Show 就不建句柄**。于是后台线程第一次触碰它时：

1. `logForm != null` 成立 → 求值 `logForm.InvokeRequired` → 句柄未创建 → **`false`**
2. 走 `else a();` 分支 → **在调用方（线程池线程）上直接执行 UI 操作**
3. 这次写入**顺手把控件句柄创建在了线程池线程上**
4. 从此 `InvokeRequired` 变成 `true`（相对 UI 线程而言），而 `BeginInvoke` 投递到的却是一个**没有消息泵的线程池线程** → **此后所有日志与 UI 刷新静默失效**

**复现证据**（`docs/temp/gov/ivtest/`，独立 WinForms 工程）：

```
步骤1 初始·未 Show        IsHandleCreated=False  InvokeRequired=False
后台线程·调用前           IsHandleCreated=False  InvokeRequired=False   ← 不会走 marshal 分支
后台线程·调用后           IsHandleCreated=True                          ← 句柄被后台线程创建
步骤2 主线程（之后）      InvokeRequired=True                           ← 句柄已绑到后台线程
步骤4 最终 Text（空）                                                  ← 写入读不回来
```

**触发路径（真实可达，而且很短）**

```
Program.cs:220   dshStartItem = new ToolStripMenuItem("启动 DSH", null, (s,e) => Bg(() => DshStart()));
Program.cs:740   DshStart() 内：logForm.Append(">>> 启动 DSH：...")
                 → AppendTo(box, ...) → box.InvokeRequired == false → box.AppendText(...) 在线程池线程
```

**关键补充：什么情况下 Tick 会"救"它、什么情况下会漏**

`Tick()`（UI 线程）里有两条会写 `logForm` 的路径，它们在**日志窗口从未被打开**时决定了缺陷是否暴露：

| Tick 内的写点 | 触发条件 | 是否兜住句柄 |
|---|---|---|
| `logForm.Append("[name] 已就绪…")` | 有模型从"启动中"变为 `/health` 就绪 | ✅ 在 UI 线程创建句柄 → **安全** |
| `logForm.Append("DSH 已就绪（端口 3080 监听中）")` | `dshState` 变为 2 | ✅ 同上 |
| `foreach(var svc…) logForm.Append(...)` | 任一 `svc.log` 有新增输出 | ✅ 同上（这是最常见的兜底） |

所以**只要 Tick 先写过一次，后面就安全**（这也解释了"为什么一直没炸"）。反过来说，**只要这些写点全部不触发、而后台线程先写，链条就断**：

> **DSH 未运行** 且 **没有任何模型在跑** 的托盘上，开机后第一次操作就是点菜单「启动 DSH」——
> `dshPortUp` 被探为 0 → Tick 一条都不写 → `Bg(()=>DshStart())` → `logForm.Append` 在**线程池线程**上完成第一次触碰 → **句柄永久绑到线程池线程** → 之后日志窗口打开也是空的、菜单圆点也不再更新。

这不是理论推演：§7 的 S0 用户视角验收第 1 条就是这个场景。

**为什么这段代码"看起来是对的"**（作者其实已经摸到边了）

`Program.cs:1726-1727` 有一条注释：

```csharp
// 必须包 Task.Run：TrayApp 构造建 LogForm 后主线程装上 WindowsFormsSynchronizationContext，
// 直接 GetResult() 等 await 会把续体排进不泵消息的主线程 → 死锁（自检路径专用规避）
```

作者**已经知道**"主线程在 `TrayApp` 构造后就有了 `WindowsFormsSynchronizationContext`"，并为此在自检路径做了规避。但 `Ui()` 没有用这个上下文，而是用了 `logForm` 当锚点 —— **知识在，没用在自己的调度器上**。（`SynchronizationContext.Post` 会**入消息队列**，即使消息泵还没开始转也不会丢，比 `BeginInvoke` 一个不存在的句柄严格更强。）

**次生问题（同一函数内）**

- `logForm` 被**释放**时：`logForm!=null` 为真 → `logForm.InvokeRequired` 抛 `ObjectDisposedException` → 被外层 `catch` 吞掉 → 内层 `try{a();}catch{}` **又一次在后台线程执行**。（注：`LogForm` 关闭是 `Hide()` 不是 Dispose，见 `Program.cs:59`，所以这条当前不触发，但一旦有人改成真关闭就会踩。）
- **异常被静默吞掉**：`Bg()`（L772）的 `try` 只包住 `Task.Run` 的**调度动作**，任务内部异常成为**未观察任务异常**（没有 `TaskScheduler.UnobservedTaskException` 兜底）；`Ui()` 的嵌套 catch 同理。**后台动作失败时没有任何痕迹** —— 而这与"托盘要能自证状态"的设计目标直接冲突（对照：`Program.cs:1633-1635` 对 UI 线程异常是有兜底落盘的，说明缺的是纪律而不是设施）。

**下游影响面（把"只在 UI 线程"的隐含假设打穿）**

`Ui()` 承载的是**整个菜单重建**：

| 位置 | 内容 | 线程来源 |
|---|---|---|
| `L1141` | `Ui(() => { recentList=list; …; RebuildRecentMenu(); })` | 外层是 `Task.Run`（L1137） |
| `L617/618/642/656` | `Ui(() => RefreshSvcMenus())` | 菜单点击 → `Bg` |
| `L718/735/742/762` | `Ui(() => RefreshDshUi())` | `DshStart/DshStop` → `Bg` |
| `L1447` | `ExitFromSignal` → `Ui(() => ExitApp(...))` | 命名事件等待线程 |

其中 **`RebuildRecentMenu()` 是最危险的一个**，因为它自己在 `L1316` 写着：

```csharp
if(menu.Visible) return; // 核心守卫：显示中绝不动结构（RemoveAt/Insert 会静默破坏弹出），交给下次 Closed/采集回调
```

作者**明知** `RemoveAt`/`Insert` 在菜单显示中会"静默破坏弹出"，因此加了 `Visible` 守卫。但这个守卫**只在"调用确实发生在 UI 线程"时才成立** —— 一旦 P0-1 把 `RebuildRecentMenu` 甩到线程池线程，`menu.Visible` 本身就是跨线程读、而结构变更会与正在显示的 UI 线程并发 → **守卫形同虚设**。

同理，`_dotCache`（`L1224` 读 / `L1232` 写）、`_archCache`（`L1259/1262`）、`_sessCache`（`L1274/1276/1277`）都是**未加锁的 `Dictionary`**，其"只在 UI 线程访问"的前提**完全依赖 `Ui()` 正确 marshal**（见 P2-11）。

**建议**

1. 引入一个**在 UI 线程创建的隐藏 `Control`** 作为 marshal 锚点，**或**（更轻、且与 L1726 的既有认知一致）在 `TrayApp` 构造后捕获 `SynchronizationContext.Current`，`Ui()` 改用 `Post`；**永远不用业务窗口/业务控件当锚点**；
2. **两处一起改**：`Ui()`（L773）+ `LogForm.AppendTo()`（L77）。只改一处等于没改 —— 另一处照样能把句柄绑到线程池线程；
3. `Ui()`/`Bg()` 内**记录**异常到 `tray-ex.log`（或新开 `tray-bg.log`），不要静默吞；
4. `Ui()` 增加"锚点未就绪"的**显式分支**（未就绪时入队，锚点就绪后回放），而不是退化成"在当前线程执行"。

---

### P0-2 【P0】`renderSessionItem` 四个连续 `return`，会话列表渲染成空壳

**证据** —— `ThinChatForm.cs:723-734`：

```javascript
function renderSessionItem(s) {
  ...
  return '<div class="sess' + (isActive ? ' active' : '') + '" data-sid="' + s.sessionId + '">';  // L730
  return '<span class="dot ' + isRunning + '"></span>' + isSub + '<div class="t">' + title + '</div>'; // L731 死代码
  return '<div class="m">' + time + '</div>';                                                    // L732 死代码
  return '</div>';                                                                               // L733 死代码
}
```

JS 里 `return` 之后不再执行，后三行是死代码。实际返回的只有一个**未闭合的 `<div class="sess">`** —— 状态点、标题、时间、闭合标签全部丢失。浏览器会自动补闭合，于是**每一行会话渲染成一个空框**。

**影响**：托盘内置瘦客户端的「工作区会话」列表不可用。

**修复**：改为字符串拼接后单次 `return`（四段拼成一段）。

---

### P1-1 【P1，可升 P0】`Service.log` 三写一读、零同步 —— 潜在进程崩溃点

**证据**（写方 3 个、读方 1 个、全项目零 `lock`/零 `Interlocked`）

```csharp
Service.cs:6      public StringBuilder log = new StringBuilder();   // 无任何同步设施

// 写方①  L624：stdout 异步读线程
svc.proc.OutputDataReceived += (o,e) => { if(e.Data!=null){ svc.log.AppendLine(e.Data); … } };
// 写方②  L624：stderr 异步读线程
svc.proc.ErrorDataReceived  += (o,e) => { if(e.Data!=null) svc.log.AppendLine("[err] "+e.Data); };
// 写方③  L955：可从线程池调用（Tick 的 Task.Run → Adopt/Stop → Log）
void Log(Service svc, string s){ svc.log.Append(s); }

// 读方   L1433：UI 线程（WinForms Timer 的 Tick）
foreach(var svc in services){ long L=svc.log.Length; … svc.log.ToString((int)svc.lastLen, (int)(L-svc.lastLen)); … }
```

`BeginOutputReadLine()` + `BeginErrorReadLine()` 各自把回调排进线程池 —— **stdout 与 stderr 两个回调通常是两个不同线程**，于是出现"同一 `StringBuilder` 被两个线程池线程并发 `AppendLine`"的画面。`StringBuilder` 明确**不是线程安全的**：并发 `AppendLine` 可能撞坏内部 chunk 指针，抛 `IndexOutOfRangeException`/`ArgumentOutOfRangeException`。

**后果分两档，必须分开说**：

| 抛异常的线程 | 兜底 | 后果 |
|---|---|---|
| **线程池线程**（`AppendLine` 内部） | `AppDomain.UnhandledException`（`L1635`）**只通知、不阻止终止** | **托盘进程直接退出**，`tray-ex.log` 留一行 `FATAL` |
| **UI 线程**（`Tick()` 内） | `Application.ThreadException`（`L1634`） | 不崩，但**本次 Tick 整体中止** → 这一秒的巡检/日志搬运全部跳过 |

**当前状态：潜在，未引爆。** 实测 `publish/`、`publish-next/`、`publish-new/` 三处**都没有 `tray-ex.log`**（§1 第 ④ 路）⇒ 至今没真炸过。原因是单次 `AppendLine` 极短、撞车概率低。但**概率低不等于不会**，而且这台机器的模型服务是 24h 常驻 + 每轮对话都往 stdout 打统计行，累计样本量足够大。

**建议**（按性价比排序）：

1. 把 `svc.log` 换成 **`ConcurrentQueue<string>` + 有界容量**（顺带解决 P1-2 的无界增长，一次改两件事）；
2. 退而求其次：给 `AppendLine`/`Append`/`ToString` 三处统一加一把 `readonly object logGate` —— 注意**读方也必须进锁**，否则等于没加；
3. 无论选哪种，`Tick()` 的搬运逻辑要能容忍"游标失效"（截断后 `lastLen` 必须同步回退 —— 这个坑本项目在日志窗口上已经踩过一次）。

---

### P1-2 【P1】`Service.log` 只增不减 —— 长驻托盘的内存单调增长

**证据**：见 P1-1 的四个位置。全项目 `grep` 无任何 `log.Clear()` / `log.Remove(...)` / `Length` 上限 —— 只有 `lastLen` 这个**读游标**。`llama-server` 的 stdout 会持续输出（每轮对话的 `eval time` 统计行等），跑几天就是几十 MB 常驻内存。

**顺带**：`L1433` 每秒在 UI 线程做一次 `StringBuilder.ToString(start,len)` 切片拷贝，日志一多就是每秒几 MB 的字符串分配（**这是"卡顿"的一个独立来源，与 P1-3 的 RichTextBox 重绘叠加**）。

**建议**：同 P1-1 建议 1（有界 `ConcurrentQueue` 一并解决）。若单独修，务必**裁剪时同步回退 `lastLen`**。

---

### P1-3 【P1】日志窗口两个 `RichTextBox` 无上限

**证据**：`Program.cs:23-24` 声明 `box` / `dshBox`；唯一清空手段是 `ClearView()`（`L112`，工具栏「清屏」）+ `ClearDshView()`（`L113`）—— **只能手动清，没有自动裁剪**（`MaxLength` 用的是控件默认值，等同无界）。

`L1433` 把**每个模型服务的全部 stdout** 灌进 `box`，`AppendDsh` 把 DSH 的 out/err 灌进 `dshBox`。长跑后文本量无界，`AppendText` 与重绘开销随之上升；再叠加 `AutoFollow` 每次追加都 `ScrollToEnd()`（`L66`）→ `SelectionStart`/`ScrollToCaret`，开销随文本长度增长。**表现为托盘日志窗口越用越卡。**

**建议**：设行数/字节上限（如 2 万行），超出时从头部裁剪并保持滚动位置语义。

---

### P1-4 【P1】插件「应用」在 UI 线程同步跑 `dump-config`，最长卡死 60 秒

**证据链**

```
Program.cs:1081   pluginMgr = new PluginManagerForm(st, ApplyPluginSettings, …)   // 「应用」回调
Program.cs:1096   string ApplyPluginSettings(bool sm, HashSet<string> de, HashSet<string> dd){
Program.cs:1099     string msg1 = PluginCenter.ApplyDevDisabled(cfg, disabledDevPlugins);
Program.cs:1100     var inv = PluginCenter.Scan(cfg, disabledDevPlugins, true);      // ← 同步，无 Bg 包裹
...
PluginCenter.cs:71   var p = Process.Start(psi);                                     // node --dump-config
PluginCenter.cs:73   string outp = p.StandardOutput.ReadToEnd();                     // ← 同步阻塞读
PluginCenter.cs:75   if(!p.WaitForExit(60000)){ try{ p.Kill(); }catch{} … }          // ← 最长 60s
```

`PluginManagerForm` 的「应用」按钮在 **UI 线程**回调 `ApplyPluginSettings`，里面同步等 `node --dump-config` 结束，最长 **60 秒**。期间托盘菜单、日志窗口全部无响应。

**注意**：托盘自身走的是 `Bg(...)` 包裹的路径（`L1075`、`L1090`），**只有插件窗口这条路径漏了** —— 所以这是"同一操作两条路径、其中一条忘了异步"的不一致，不是普遍性错误。

**建议**：`ApplyPluginSettings` 内部把重活 `Bg(...)` 化，或把「应用」按钮改成异步 + 忙碌态。

---

### P1-5 【P1】`async void` 事件处理器把异常抛进消息循环

**证据**：`ThinChatForm.cs:58` `async void OnWebMessage(...)`，其中 `L78` `var res = await rpc.Call(method, argsRaw);` **不在任何 try 内**（`try/catch` 只包了 `L60-66` 的 JSON 解析和 `L86` 的 `PostWebMessageAsJson`）。

`async void` 的异常无处可接 → 直接抛给 `SynchronizationContext` → 被 `Application.ThreadException`（`Program.cs:1634`）记进 `tray-ex.log` 后**静默中止本次消息处理**，JS 侧永远收不到 `id` 对应的回复（前端会一直等）。

`ShowHome`（`L40`）、`OpenSession`（`L32`）同样是 `async void`。

**建议**：事件处理器内部整体包 `try/catch` 并回一条错误响应给 JS；内部方法改 `async Task`。

---

### P1-6 【P1】分层倒置：性能数据层直接依赖运行时服务类型

**证据**

```csharp
ModelPerf.cs:561  public string OnStart(Service svc, LaunchResult build, string exePath, out bool isNew)
ModelPerf.cs:582  public void OnReady(Service svc, long loadMs, int servedCtx, string? llamaBuild)
ModelPerf.cs:598  public void OnFail(Service svc, string result)
ModelPerf.cs:608  public void OnLlamaLine(Service svc, string? line)
```

而 `Service` 不是配置 DTO，是**带活对象的运行时状态袋**（`Service.cs:5-16`）：

```csharp
public string Name; public int Port; public string Model; …
public System.Diagnostics.Process proc;          // ← 进程句柄
public StringBuilder log = new StringBuilder();  // ← 可增长缓冲
public volatile bool Starting; public volatile int runCtx; …
```

**为什么是问题**：采集/落盘逻辑只需要"这是哪个服务的哪份配置"，却被塞进一个**携带进程句柄和可变缓冲**的对象。后果：

- `ModelPerf` 无法在无 WinForms / 无进程的环境下单测（`PerfProbe` 只能自己 `new Service{…}` 造一个，见 `ModelPerf.cs:959`）；
- 「同一配置」的判定被迫依赖整个运行时对象，而不是一个小值对象。

叠加 `ModelPerf.cs` 把 **UI（`PerfPanel`）、持久化（`PerfStore`）、解析（`LlamaLogParser`）、HTTP 基准（`BenchRunner`）、自检（`PerfProbe`）** 全放在一个文件里 —— 数据层与呈现层、测试代码与生产代码都没有边界。

**建议**：引入 `readonly record struct ServiceKey(string Name, int Port)` 作为采集层入参；`PerfSampler` 只依赖它。（**注意**：`cfgId` 的算法必须一格不改，见 §7 S2 验收。）

---

### P1-7 【P1】`PerfPanel.Refresh_` 在 UI 线程读整月分片 + N 次 JSON 解析 —— 且注释与实现不符

**现象**

```csharp
ModelPerf.cs:712  public void Refresh_(){            // ← UI 线程（WinForms 控件的事件回调）
ModelPerf.cs:715    _runCache=null;                  // ← 每次刷新都强制重扫样本
ModelPerf.cs:733    status.Text=" 配置 "+all.Count+" 条 · 样本 "+store.TotalBytes()/1024+" KB · "+store.Dir;
                    // TotalBytes() = new DirectoryInfo(Dir).GetFiles("samples-*").Sum(...)  ← 每次全目录枚举
ModelPerf.cs:737  Dictionary<string,(double pp,double tg)>? _runCache;
ModelPerf.cs:738  void BuildRunCache(){              // ← 由 RunTg() 在 Refresh_ 的循环里惰性触发
ModelPerf.cs:741    foreach(var line in Store?.TailSamples(300) ?? new List<string>()){
ModelPerf.cs:743      using var doc=JsonDocument.Parse(line);      // ← 最多 300 次解析，全在 UI 线程
```

**真正的硬伤在 `TailSamples` 的注释与实现不一致**（`ModelPerf.cs:356-366`）：

```csharp
// 最近样本（供界面详情显示；只读当月+上月两个分片，避免越用越慢）
public List<string> TailSamples(int maxLines){
  var files = Directory.GetFiles(Dir,"samples-*.ndjson").Where(f=>!f.EndsWith(".gz")).OrderByDescending(f=>f).Take(2).OrderBy(f=>f).ToList();
  foreach(var f in files) res.AddRange(File.ReadAllLines(f));      // ← L362：整片全读进内存
  if(res.Count>maxLines) res=res.Skip(res.Count-maxLines).ToList(); // ← L363：读完才截断到 300
}
```

注释说"只扫最近 300 行，**避免越用越慢**"，但实现是 **`File.ReadAllLines` 把当月经月两个分片全量读进来，再 `Skip().Take(300)` 丢掉** —— 代价**随分片大小线性增长**，"避免越用越慢"这句注释**恰好说反了**。而且是"永久保留"策略（P2-9），分片只会越来越大。

再叠加 `Refresh_` 里对 `_runCache=null` 的无条件重置 → **每次刷新都重读**（且调用方 `ShowPerf()`（`Program.cs:63`）在每次切到性能页都调用一次）。

> 这一类"注释承诺有界、实现是无界"的偏差，正是本项目最该警惕的形态：**读者会信注释，下一次改动就会照着注释的前提做决策。**

**建议**：

1. `TailSamples` 改成**从文件尾部按块倒读**（或 `File.ReadLines().TakeLast(n)` 的流式实现）—— 让实现真的匹配注释；
2. `PerfPanel.Refresh_` 的 IO + 解析移出 UI 线程（`Task.Run` + 回 UI 线程刷 `ListView`）；
3. `_runCache` 增加失效条件（样本文件 mtime/size 变化）而不是每次无条件置空。

---

### P2 清单（设计债，现在不改不会炸）

| # | 问题 | 证据 | 说明 |
|---|---|---|---|
| P2-1 | **两套配置机制并存** | JSON：`Config.cs`；行式：`Program.cs:393-405` 的 `key=value` 前缀 if-else 链（13+ 分支） | 两种格式、两套默认值语义、两处校验，容易不一致 |
| P2-2 | `Config.Load()` 会**写回默认配置** | `Config.cs:53-70`（文件缺失或 `Services` 为空时 `File.WriteAllText(Path_, …)`） | 已有约定：重读配置一律用只读解析。**这个约定必须写进代码注释与评审清单**，否则新人一调就抹配置 |
| P2-3 | `Service` 是公共字段袋 | `Service.cs:5-16`（12 个 public 字段 + 4 个 volatile） | 无封装、无不变式保护 |
| P2-4 | WebView2 **无导航守卫** | `grep NavigationStarting\|NewWindowRequested\|WebResourceRequested` 在 `ThinChatForm.cs` **零命中** | 页面若被导航到外部源，外部页面可通过 `postMessage` 直达 `DshRpc.Call`（**已带认证 cookie**）。当前页面由本地 `NavigateToString` 注入且 markdown 渲染**先转义**（`L235-237`，已核实无 XSS），所以**不是现网漏洞**，但缺一道纵深防御 |
| P2-5 | `Main` 内 9 个自检开关 + 148 行参数分发 | `Program.cs:1637-1645`；`L1762-1763` 注释自述"血的教训：曾漏写 exitProbe 分支，一路落到 Application.Run" | 自检已事实上成为"运维界面"，宜抽 `Cli` 类 + 注册表。**该注释本身就是"参数分发靠手写 if-else 已经出过事故"的自证** |
| P2-6 | `AutoStart` 静默失败 | `AutoStart.cs:31` 空 `catch { }` | 设自启动失败用户无感知；与"要能自证状态"目标冲突 |
| P2-7 | 401 后不重签 cookie | `DshRpc.cs:28` 缓存判定、`L48` 401 直接返回 | `EnsureAuth` 不会因 401 失效缓存 → 极端情况（签发后系统时钟大幅前跳）会**持续 401 直到重启** |
| P2-8 | `Process` 对象 Kill 后未 `Dispose` | `Program.cs:646-657`：`try{ svc.proc.Kill(); … } svc.proc=null;` | 事实成立，但**影响被模型夸大了**：`System.Diagnostics.Process` 继承自 `Component`，**有终结器**，句柄会在 GC 时释放 → 不是"句柄耗尽"，只是把释放时机交给 GC。建议顺手补 `Dispose()`（低优先） |
| P2-9 | `RotateLocked` 崩溃窗口 → `.gz` 与原片并存 | `ModelPerf.cs:342-345`：`using(File.OpenRead)→File.Create(gz)→GZipStream.CopyTo` **完成后**才 `f.Delete()` | 顺序**是对的**（先压后删，与注释"只压缩、不删除"语义一致）。残留风险：若在 `f.Delete()` 之前崩溃，会留下同名 `.gz` + 原 `.ndjson` 两份；而 `TotalBytes()` 用 `GetFiles("samples-*")` **会把两份都算进去** → 体积闸门提前触发。建议 `TotalBytes` 排除 `.gz` 或按 `.gz` 折算 |
| P2-10 | `PerfProbe` 用**真实形态的绝对路径**当测试夹具 | `ModelPerf.cs:902-905`：`@"E:\llm-deploy\llamacpp\llama-b10797-cuda12.4\llama-server.exe"` 等 | **现象成立，但模型的"影响判断"是错的**：这些字符串只喂给 `PerfFingerprint.Compute(args, exe)` 算 sha1，**不访问磁盘**（真实 IO 都在 `Path.GetTempPath()` 下的临时目录，`L893`/`finally` 删除）。真正的问题是**误导性**：读者会以为自检依赖本机 `E:\llm-deploy`。建议改成 `@"X:\llama\<ver>\llama-server.exe"` 之类的**明显假路径**，并把"目录名必须不同"的原因写成注释 |
| P2-11 | 三个未加锁的 `Dictionary` 缓存 | `_dotCache`（`L1224/1232`）、`_archCache`（`L1259/1262`）、`_sessCache`（`L1274/1276/1277`） | 目前靠"只在 UI 线程访问"的**约定**保证安全 → 与 P0-1 强耦合（见 P0-1 下游影响面）。P0-1 修好后可保持现状；若坚持不修 P0-1，则必须给它们加锁 |
| P2-12 | `Tick()` 两处 `Task.Run` **没有忙闸门** | `L1381`（svc 探活，节拍 `svcTick%2`，实际间隔见 `L1377-1379` 的 `anyStarting\|\|svcTick%10`）、`L1407`（`gpuTick%5` 跑 `nvidia-smi`） | 对照 `L1423` 的 `_probeBusy` 闸门（做得对）。**"无限流导致任务堆积"是夸大**：稳态下 svc 探活的间隔是 20s、GPU 是 5s，且 `ThreadPool` 本身有界。真实风险仅限于"`anyStarting` 为真时每 2s 一发、而上一发探活尚未结束"的重叠。建议给这两处也补 `Interlocked` 闸门 |

---

## 4. 本地模型提出的结论：核实结果

这一节是本次评审**最有价值的部分之一**。两轮共 7 路本地模型提出了数十条结论，**经回查源码后严格命中率稳定在 ~20%**。逐条存档，避免后人照错单子改代码。

### 4.1 第二轮（r1/r2/r3，`enable_thinking=false`，全部产出真实答案）

| 模型结论（出处） | 核实 | 依据 |
|---|---|---|
| `svc.log` 的 `StringBuilder` 无锁保护 | ✅ **成立** | `Service.cs:6` + `Program.cs:624/955/1433`，全项目零同步设施 → **P1-1** |
| `PerfPanel` 在 UI 线程解析 JSON | ✅ **成立** | `ModelPerf.cs:712/738/741/743`；且我进一步查出 `TailSamples` 注释与实现矛盾 → **P1-7** |
| 分层倒置：`PerfSampler` 直收 `Service` | ✅ **成立**（与 §3 独立复现同一结论） | `ModelPerf.cs:561/582/598/608` → **P1-6** |
| `PerfPanel` 与数据/存储/自检同处一文件 | ✅ **成立**（设计意见） | `ModelPerf.cs:252/479/654/889` |
| `TrayApp` 职责域清单（15 个域 + 行号范围） | ✅ **成立**（抽查行号全对） | 见 §2.4，已并入 §5/§6 拆分依据 |
| `Ui()` 锚点不可靠 | ⚠️ **结论对、机制错** | 模型说"`menu` 与 `logForm` 的 `InvokeRequired` 可能指向不同线程"——**错**：同一 UI 线程创建的所有控件同属一个线程。真因是**句柄未创建时恒返回 false**（→ **P0-1**），而这正是模型**没**说出来的那一步 |
| `volatile dshState` 是误导性修饰 | ⚠️ **派生结论成立** | `Program.cs:144` 确实是 `volatile`；但"`volatile` 保证不了 UI 线程"这句话只有在 P0-1 成立时才有意义 → 归入 P0-1 影响面，不单列为缺陷 |
| `RebuildRecentMenu` 被两处调用（`Ui()` + `menu.Closed`）存在竞态 | ⚠️ **结论对、机制错** | 模型自己先论证"两者都在 UI 线程 → 安全"，紧接着又推出竞态，**自相矛盾**。真实风险**全部来自 P0-1**：`L1316` 的 `if(menu.Visible) return` 守卫在跨线程调用时形同虚设 |
| `PerfProbe` 硬编码 `E:\llm-deploy\…` 路径 | ⚠️ **现象成立、影响判断错** | 路径确实存在（`ModelPerf.cs:902-905`）；但只用于算 `cfgId`，**不访问磁盘**。"自检无法在 CI 运行"是错的 → **P2-10** |
| `Process` Kill 后未 `Dispose` 导致句柄耗尽 | ⚠️ **事实成立、影响夸大** | 见 **P2-8**（`Component` 有终结器，交给 GC 而非耗尽） |
| `Tick()` 的 `Task.Run` 无节制、线程池堆积 | ⚠️ **夸大** | `L1423` 已有 `_probeBusy` 闸门；另两处节拍低（20s / 5s）且 ThreadPool 有界 → **P2-12** |
| `SaveDbLocked`/`File.Replace` 竞态、`.tmp` 残留 | ⚠️ **推测，无可复现缺陷** | `ModelPerf.cs:280-298` 有 `lock(_gate)` + `.tmp`+`File.Replace`；模型未给出可复现路径 |
| `importLegacy` 幂等性（`SaveDbLocked` 失败会重复导入） | ⚠️ **推测** | `ModelPerf.cs:372/407/408`；属边界推演。**反向事实**：`ImportLegacy` 的既有实现刻意"只在文件确实存在时才置 `legacyImported`"，说明作者已考虑过幂等 |
| `PendingCfg` 竞态（`OnReady` 早于 `OnLlamaLine` 导致丢数据） | ⚠️ **推测** | `ModelPerf.cs:572-574/583/611-612`；逻辑上要求"进程启动极快"这一未观测前提 |
| `Service.Starting`/`PortBusy` 未声明 `volatile` | ❌ **不成立（事实错误）** | `Service.cs:8/12` **就是** `volatile`（`runCtx`/`runVision` 在 `L10/L11` 也是）。模型**漏看了修饰符**——这是最典型的"看起来很有道理但其实没细看" |
| `dshPortUp` 混用 `volatile` 与 `Interlocked` | ❌ **不成立** | `Program.cs:146`：`volatile int dshPortUp=-1; int _probeBusy=0;` —— 用 `Interlocked` 的是 **`_probeBusy`**，模型把两个变量混为一谈 |
| `DisposeForDump()` 应先停 `clock` 再 `Dispose` `logForm` | ❌ **不成立** | `Program.cs:1491` **已经是**这个顺序：先 `icon.Dispose()`、再 `clock.Stop()`、最后 `logForm.Dispose()` |
| `popup`/`officialForm` 只 `Close()` 不 `Dispose()` → WebView2 COM 泄漏 | ❌ **不成立** | 两者都是 `Show()`（非模态，`L1127`/`L1357`）→ 非模态表单 `Close()` **即 `Dispose()`**；且 `L1113`/`L1345` 有 `IsDisposed` 复用检查，`popup` 只在 `ExitApp`（`L1443`）关一次 → **根本没有反复创建/销毁**，谈不上泄漏 |
| `RotateLocked` 的 `File.Delete` 与 `File.Create` 之间有时间窗口 | ❌ **不成立** | `ModelPerf.cs:342-345`：`Delete` 在 `using` 块**之后**，压缩已完整落盘才删。真实残留风险是另一回事 → **P2-9** |
| `DshKill`/`Adopt` 里 `GetProcessById` 的 try-catch 吞异常导致误判 | ❌ **不成立** | `Program.cs:744-758` / `L1361-1368`：内层 `try{…}catch{}` 的语义就是"进程不在了就跳过"，`killed=false` 是**正确结果**而非误判 |
| `Med()` 用 `OrderBy` 导致中位数因"排序不稳定"而不同 | ❌ **不成立（概念错误）** | 中位数只取决于**值的有序位置**，与"相等元素的相对次序"无关；`OrderBy` 的稳定性在这里完全不影响结果 |
| `AppendAllText` 不是原子操作 → 半行写入 | ❌ **不成立（就本项目而言）** | `ModelPerf.cs:317-324` 有 `lock(_gate)` 覆盖进程内全部写入；跨进程/崩溃截断是文件系统的固有性质，不是本设计缺陷 |

### 4.2 第一轮（r1/r2/r3 被思维链烧空 + r4 完整）

第一轮只有 **r4** 产出了最终答案（`out_r4.md`），r1/r2/r3 在 `max_tokens=9000` 下把预算全烧在思维链上、最终答案只有 9 字符。r4 的 15 条结论核实结果（统计：成立 3、部分成立/设计意见 6、不成立 5、夸大 1）：

| 模型结论 | 核实 | 依据 |
|---|---|---|
| `renderSessionItem` 四个连续 `return`，列表渲染为空 | ✅ **成立** | `ThinChatForm.cs:730-733` 逐行确认 → **P0-2** |
| `async void` 吞异常（`OpenSession`/`ShowHome`/`OnWebMessage`） | ✅ **成立** | `ThinChatForm.cs:32/40/58`，且 `L78` 的 `await` 未包 try → **P1-5** |
| `ParseDump` 同步阻塞可致 UI 冻结 | ✅ **成立（触发路径需修正）** | 阻塞确在 `PluginCenter.cs:73/75`；但托盘入口在 `Bg` 里，**真正走 UI 线程的是插件窗口「应用」**（`Program.cs:1096-1100`）→ **P1-4** |
| `ReadSecret` 行级 YAML 解析脆弱 | ⚠️ **部分成立** | 当前 dsh 写的 inline 格式可用；若写成块标量（`secret: \|`）会取到 `"\|"` 导致认证失败 |
| 内嵌 HTML/JS 无法测试、无法版本化 | ⚠️ **成立（设计意见）** | `Page.Html` 25423 字符固化在 `ThinChatForm.cs:92-742` |
| WebView2 消息协议无版本/白名单 | ⚠️ **成立（设计意见）** | `ThinChatForm.cs:77-78` 注释直言"不校验" → **P2-4** |
| `ApplyDevDisabled` 无并发保护会丢状态 | ❌ **不成立（现网）** | 全项目仅 3 个调用点，均在 UI 线程；属未来隐患非现网缺陷 |
| 静态 `HttpClient` 会 socket 耗尽 | ❌ **不成立** | .NET 8 下**静态 HttpClient 是官方推荐**；socket 耗尽源自"每请求 `new HttpClient`"，本项目没有 |
| bundle 路径未处理 `@scope` 包名 | ❌ **不成立** | 模型把 `L120`（路径，`/`→`\`，正确）与 `L121`（id 兜底，去 `@`，也是刻意）**混为一谈** |
| `_readLock` 使用不一致（L1234/1238 加锁、L1242/1247 不加） | ❌ **不成立** | 逐行确认：**四处全在锁内**（`L1234/1238/1242/1247`） |
| `sessionId` 拼接 JSON 可注入 XSS | ❌ **不成立** | 走的是 `PostWebMessageAsJson`（**JSON 通道**，非 HTML 上下文）；且 `JsonEncodedText.Encode` 转义正确。模型拿自己引用的代码推出了相反结论 |
| `issuedAt` 用 `TickCount64` 导致 cookie 过期判断错误 | ⚠️ **结论夸大** | `DshRpc.cs:28/33` 确实用 `TickCount64`，但**进程内单调时钟对"缓存窗口"反而更正确**（不受系统时钟调整影响）。真问题在 **401 后不重签**（P2-7） |
| `State` 类公开暴露内部状态 / `RefreshAll` 是 hack | ⚠️ **设计意见** | `PluginManagerForm.cs:14-22/68`，非缺陷 |
| `AutoStart` 用 `dynamic` COM 脆弱 | ⚠️ **成立（次要）** | `AutoStart.cs:18-21`；真正的问题是静默失败（P2-6） |
| 迁移顺序建议（先抽 Core 再拆 UI） | ⚠️ **方向对、顺序需调整** | 见 §6：应先修承重缺陷，再拆模块 |

### 4.3 关于"用本地模型做架构评审"的可复用结论

**两轮独立复现，严格命中率都 ~20%**（第 1 轮 3/15、第 2 轮 5/23）。这不是偶然，是可以写进工作流的稳定特征：

| 它能干什么 | 它不能干什么 |
|---|---|
| 广度扫描、指出"这里值得看一眼"（本轮最有价值的 3 条线索都由它先提出：P1-1 / P1-7 / 职责域清单） | 给出可直接落地的结论——**每条都必须回查源码** |
| 读懂小切片的结构与命名问题 | 判断影响严重度（几乎全部偏大一档） |
| 复述已有事实 | 记住**修饰符级**细节（漏看 `volatile`、混淆两个变量的加锁方式） |
| | 保持跨段落自洽（同一节里先论证安全、再推出竞态） |

**推论**：把它当**线索发生器**用，并且**永远用行号回查**；把它当**结论来源**用，就会把"没看 `volatile`"这种错误改上线。

---

## 5. 目标架构（模块边界）

```
                    ┌─────────────────────────────────────────┐
                    │   QwenTray.Host（组装 + 入口）           │
                    │   Main / Cli（--selftest-* / --bench）   │
                    └───────────────┬─────────────────────────┘
                                    │ 只做组装，不含逻辑
        ┌───────────────────────────┼───────────────────────────┐
        ▼                           ▼                           ▼
┌───────────────┐          ┌────────────────┐          ┌──────────────────┐
│ QwenTray.Ui   │          │ QwenTray.Core  │          │ QwenTray.Integr. │
│ (WinForms)    │  ──────► │ (无 UI 依赖)    │ ◄──────  │ (外部世界)        │
│               │          │                │          │                  │
│ TrayShell     │          │ Config         │          │ GpuInfo / HwInfo │
│ UiDispatcher  │          │ ServiceSpec    │          │ SysInfo          │
│ MenuBuilders  │          │ PerfStore      │          │ AutoStart        │
│ LogWindow     │          │ LlamaLogParser │          │ ProcessRunner    │
│ PerfPanel     │          │ PerfSampler    │          │ PluginScanner    │
│ PluginForm    │          │ BenchRunner    │          │ DshAuth / DshRpc │
│ ThinChat      │          │ ServiceRuntime │          │                  │
└───────────────┘          └────────────────┘          └──────────────────┘
        ▲                                                        │
        └──────────────── 依赖方向单向，Core 谁都不依赖 ───────────┘
```

**关键边界决策**

| 决策 | 从 | 到 | 理由 |
|---|---|---|---|
| 拆 `Service` | 单一可变字段袋 | `ServiceSpec`（配置，不可变）+ `ServiceRuntime`（状态 + 进程句柄） | 让采集/落盘层只依赖 `ServiceSpec`，可单测（解 P1-6） |
| 抽 `UiDispatcher` | `Ui()` 用 `logForm` 当锚点；`AppendTo()` 用 `box` 当锚点 | 单一 `SynchronizationContext`（构造后捕获） + 未就绪入队 | **一处修、全局受益**，且必须覆盖两个锚点（解 P0-1） |
| 抽 `LogSink` | `svc.log` 是裸 `StringBuilder` | 有界 `ConcurrentQueue<string>` + 游标语义 | 一次解决 P1-1（线程安全）+ P1-2（无界） |
| 抽 `Cli` | `Main` 里 9 个 bool + 148 行分发（且已出过事故，`L1762-1763`） | `Cli` 类 + 命令注册表 | 自检是事实上的运维界面，值得独立 |
| 拆 `ModelPerf.cs` | 17 类型同文件 | 按上表分到 Core / Ui | 数据层不再与 `PerfPanel`、`PerfProbe` 同居 |
| 统一配置 | JSON + `.cfg` 两套 | 单一 `AppConfig`（`.cfg` 降级为一次性迁移输入） | 消除双默认值语义（P2-1） |

**注意：§2.4 的 15 个职责域不等于 15 个模块。** 归并按**依赖方向**走，不按"话题"走 —— 例如"模型状态巡检"与"系统信息采集"都只是往 `Core` 取数据 + 往 `Ui` 推结果，不该各成一个可独立引用的模块。

---

## 6. 增量迁移路线（每步可独立发布、不破坏现有功能）

> 铁律：**每一步都必须单独可编译、可上线、可回滚**。不做"大爆炸式重构"。

| 步 | 内容 | 风险 | 验证方式 |
|---|---|---|---|
| **S0** ✅ | **只修承重缺陷，不动结构**（已完成，见 §9）：①`UiDispatcher`（最小化版：捕获 `SynchronizationContext`，**同时替换 `Ui()` 与 `LogForm.AppendTo()` 两个锚点**）②`svc.log` 换有界 `ConcurrentQueue` 或统一加锁 ③两个 `RichTextBox` 加上限 ④`Bg()`/`Ui()` 异常落盘 ⑤修 `renderSessionItem` ⑥`ApplyPluginSettings` 改后台 ⑦`TailSamples` 改成真·尾部读取 | 低 | 既有 `--selftest-*` 全 PASS；**新增** `--selftest-uithread`、`--selftest-logsink`（见 §7） |
| **S1** ✅ | **已完成（2026-09-11，见 §10）** —— 同工程内按文件拆类型（零逻辑改动）：`Program.cs`(7 类型) + `ModelPerf.cs`(17 类型) → **20 个文件**；`TrayApp` 用 `partial` 按职责分 5 块 | 极低 | 编译 0 error / 32 警告（= 基线）；9 项自检全 PASS；**多重集守恒断言**通过（缺失 0 / 多出 4 = 5 块各补的闭合 `}`） |
| **S2** ✅ | **已完成（2026-09-12，见 §11）** —— 切出 `ServiceSpec`（10 个配置字段 + `From`）；`Service` 改为**组合**（10 个转发属性保旧访问点零改动）⇒ `LaunchArgs.Build` 与 `PerfRuntime` 四方法的签名里**不再出现 `Service`**。`ServiceRuntime` **有意未拆**（无 S3 收益，理由见 §11.3） | 中 | `dotnet build` **0 错误 / 30 警告**；9 项自检全 PASS；`--selftest-perf` 39 → **40/40**（新增 `FP-REAL-8081` 锚）；**真实 `--bench 8081` 打回 `cfg=966d1620a3`**（= 改造前基线；台账未新增条目） |
| **S3** ✅ | **已完成（2026-09-12，见 §12）** —— 抽 `QwenTray.Core` 工程：15 个无 UI 依赖文件 `git mv` 进去（git 全部识别为 rename ⇒ 历史保留），命名空间**保持 `QwenTray`** ⇒ 调用点零改动 | 中 | Core 用 `UseWindowsForms=false` 卡成**编译期围栏**（里面出现 `System.Windows.Forms` 即 CS0234）。围栏落地当天就清掉了 S1 遗留的 14 行僵尸 using |
| **S4** ✅ | **已完成（2026-09-12，见 §13）** —— 抽 `CliOptions`（Core）+ 建 `QwenTray.Tests`（93 个单测）；为此把 `Config` 的回填抽成纯函数 `MergeFrom` / `Merge` | 低 | `dotnet test` **93/93 全绿**；已纳入范围的类 ≥95%（`Cli` / `LlamaLogParser` 100%）。⚠️ Core **总体仅 27.8%** —— 「Core 先卡 60%」这步不成立，原因与建议口径见 §13.3 |
| **S5** ✅ **收口** | **四段全部交完（2026-09-12，见 §14 / §15）**：① `LogSink` 移入 `QwenTray.Core`（+9 单测）；② `ServiceManager` 的**可测面**升格 —— `SvcStatus`（`State`/`Dot`/`Enable`）+ `SvcProbe`（`PortUp`/`HealthUp`/`KillByPort`）进 Core（+25 穷举单测）；③ 菜单**可判定文本**（`SvcLines`：4 个档位标签 / `Mid` / `Dur` / `CfgLines` / `StatusTip`）进 Core（+47）；④ 「重读配置」的**判定内核**（`SvcConfigDiff`：`Find` / `Apply`）进 Core（+13）。⚠️ **原定的「独立类型版」（`MenuBuilders` / 进程编排面）经复核判定不做**（解锁 0 自动验证、风险最高），理由见 §15.2 | 高 | `dotnet test` **193/193**；11 项自检与同机基线差分（8 项逐字节一致，3 项差异逐条定性全为时刻/环境类）；负向测试如期 3 红后逐字节还原。**原定独立类型版仍须** §7 回归清单逐项手工过 |

**为什么不先拆模块**：P0-1、P1-1、P1-2 是**跨模块的行为问题**，拆模块不会消除它们，反而会把"用业务控件当 marshal 锚点"这个错误模式带进新的 `Ui` 模块，成为"新架构里的旧毛病"；`svc.log` 的裸 `StringBuilder` 也会被原样搬进 `LogSink` 并**继承**它的线程不安全。

**一个具体的排序理由（本轮新增）**：P0-1 的影响面里包含 `_dotCache`/`_archCache`/`_sessCache` 三个未加锁字典（P2-11）与 `RebuildRecentMenu` 的 `Visible` 守卫。如果先拆模块，这三个缓存会被分到不同模块、各自的"我以为是单线程"假设**再也无法统一审查** —— 所以**先统一调度器，再拆**。

---

## 7. 验收标准（双视角）

按本项目约定，每条都给**开发者视角**与**用户视角**两个标准。

### S0 验收

| # | 开发者视角 | 用户视角 |
|---|---|---|
| 1 | 新增 `--selftest-uithread`：从线程池线程调用 `Ui()` **与** `logForm.Append`，断言二者最终都在 UI 线程执行、且 `logForm`/`box`/`dshBox` 的句柄**不是**由线程池线程创建 → 输出 PASS | 托盘启动后**先不开日志窗口**，直接从菜单点「启动 DSH」，**再**打开日志窗口 —— 应能看到启动全过程日志（当前此场景会看到空白） |
| 2 | 新增 `--selftest-logsink`：多线程并发喂 10 万行，断言无异常、行数守恒、且 `Length` 有上限、游标被同步回退（游标越界测试） | 连续跑满 24h 后，托盘内存占用不单调上升（任务管理器对比首末） |
| 3 | `Ui()`/`Bg()` 异常断言：注入抛异常的回调 → `tray-ex.log`（或 `tray-bg.log`）出现记录 | 托盘动作失败时，日志窗口有可读的错误行（而不是静默无事发生） |
| 4 | `renderSessionItem` 单测（Node 侧或正则断言）返回串包含 `dot`/`t`/`m`/`</div>` 四段 | 瘦客户端「工作区会话」列表显示标题与时间，不再是一排空框 |
| 5 | 插件「应用」耗时断言：`dump-config` 跑满 60s 期间 UI 线程不阻塞（Stopwatch + `Application.DoEvents` 心跳计数） | 点插件窗口「应用」时，托盘菜单仍可正常打开 |
| 6 | `TailSamples` 有界性断言：造一个 100 万行的分片，断言读取耗时/内存与"300 行"同级（而非与 100 万行同级） | 样本文件攒了几个月后，性能页签刷新仍然秒开 |
| 7 | 全部既有自检：`--selftest-logwin` / `--selftest-svcmenu` / `--selftest-lock` / `--selftest-exit` / `--selftest-perf` 全 PASS | 日志窗口三页签、菜单状态圆点、模型启停/重启（不中断 dsh 会话）行为与改造前一致 |

### S2 验收（数据可比性是硬指标）

| # | 开发者视角 | 用户视角 |
|---|---|---|
| 1 | 改造前后同一配置的 `cfgId` 完全一致（`sha1(排序参数 + llama 目录名)[:10]`），台账不新增行 | 性能页签里历史配置的性能数据仍然对得上，没有出现"同配置两条记录" |

### S5 验收（拆两段：可自动验证的一半已交，另一半是手工回归）

| # | 开发者视角 | 用户视角 |
|---|---|---|
| 1 | `dotnet test` **193/193**（S4 的 93 + `LogSink` 9 + `SvcStatus` 25 + `SvcLines` 47 + `SvcConfigDiff` 13） | —（纯逻辑，无 UI 面） |
| 2 | 11 项自检 + `--dump-menu` 与**同机基线**逐字节差分：8 项一致；3 项差异逐条定性（`menu-dump` 相对时间跨 24h 阈值 / `logwin` 时间戳 + env 枚举顺序，排序后集合 50=50 / `perf` 仅 `ts`） | 菜单结构与渲染内容与改造前一致 |
| 3 | **负向测试**：把 `SvcStatus.State` 的 starting/running 优先级写反 ⇒ 如期 3 红（恰为优先级用例）；还原后 sha256 与改造前一致 ⇒ 断言不空转 | — |
| 4 | 编译 **0 错误 / 30 警告**（= S4 基线，无新增） | — |
| ⏳ | —— | **下表**是**原定「独立类型版」（`MenuBuilders` / 进程编排面）若日后要做**的验收门槛 —— 该版经复核**判定不做**（§15.2），清单保留备查 |

**托盘行为回归清单（原定「独立类型版」的门槛 —— 自动化只能盖结构，交互态与真实启停必须人眼）**

| # | 用户视角动作 | 通过判据 |
|---|---|---|
| 1 | 右键托盘，看一级菜单 | 逐项与改造前一致（模型项 / DSH / 推理参数组 / GPU / 上下文 / KV / 缓存内存 / 切分模式 / MTP / 监听 / 日志 / 退出） |
| 2 | 逐个展开二级菜单 | 元素齐全、勾选态与当前配置一致（`--selftest-svcmenu` 只覆盖结构与状态映射，**交互态它测不到**） |
| 3 | 「启动模型」 | 圆点 黄(启动中) → 绿(运行中)；状态行出现 pid / 内存 / 端口 |
| 4 | 「停止模型」/「重启模型」 | 停止释放显存；重启**只重启 llama-server，DSH 会话历史不中断** |
| 5 | 改任一参数后看「运行时配置」6 行 | 实时跟随，数值与所选一致 |
| 6 | 取消「模型监听 0.0.0.0」 | 重启后仅本机可访问（外部设备连不上） |
| 7 | 「停止全部」/「重启全部」 | 三个模型逐个执行，无残留进程 |
| 8 | 拖动「GPU1 占比」滑块 | 两行显存估算跟随更新，子菜单宽度不抖 |
| 9 | 「查看日志」三页签 +「模型性能日志」 | 与改造前一致（含 DSH 输出页签实时跟踪） |
| 10 | 「重启托盘」/「退出」/「退出（同时停止模型服务）」 | 语义不破坏：默认退出**只退托盘不停模型** |

---

## 8. 附录

### 8.1 本地模型（8081）作为审查员的实测数据

| 项 | 实测 |
|---|---|
| 模型 | `Qwen3.6-35B-A3B-Claude-4.7-Opus-Reasoning-Distilled-APEX-MTP-I-Compact`，build `b1-832fd6f` |
| 上下文 | `n_ctx=262144`，`total_slots=4`；**每 slot 可容纳 ≥ 52,600 token 的提示词**（实测被接受，未截断） |
| 速度（第 2 轮） | 3 路并行，prompt 45.8K / 23.5K tok，输出 6000 / 4684 tok，**283 秒**（最慢一路）；4 路并行 55K token 时约 278 秒/路 |
| 并发约束 | 每路占 1 个 slot，`total_slots=4` ⇒ **最多 4 路并行**，第 5 路会排队 |
| ⚠️ 陷阱 1 | **默认开启思维链**。第 1 轮的 r1/r2/r3 在 `max_tokens=9000` 下**把预算全烧在思考上，最终答案为空（9 字符）**；`out_r*.md` 里 40KB 全是思维链，答案段只剩一个 `## 评审结果` 标题 |
| ⚠️ 陷阱 2 | 长提示词 + 思维链会进入**退化重复循环**（同一段分析反复重复直到耗尽 token） |
| ✅ 解法 | `{"chat_template_kwargs": {"enable_thinking": false}}` —— 实测生效（小请求 0.5s 直出答案，无 `reasoning_content`） |
| ⚠️ 陷阱 3 | **即便关掉思维链，仍会撞 `max_tokens`**：第 2 轮 r1/r2 的 `completion_tokens` **恰好 6000 = 上限**，输出在文中途被截断（r1 断在职责域表之后、r2 断在第 14 条）。**长评审任务要把 `max_tokens` 开到 12000+** |
| ⚠️ 陷阱 4 | **幻觉率不低**：编造不存在的代码行、给出与自己所引代码矛盾的结论、行号张冠李戴、**漏看 `volatile` 等修饰符** |
| 结论 | 适合当**线索发生器**（广度扫描、找可疑点），**不可当结论来源**；每条必须回查源码。两轮严格命中率均 ~20%（§4.3） |

### 8.2 本次取证产物

| 路径 | 内容 |
|---|---|
| `docs/temp/gov/probe.py` | 端口/模型探测（定位 8081 及其 `/props`） |
| `docs/temp/gov/inventory.py` / `inventory.json` | 文件/类型/方法/字段/并发原语统计（注意 `bytes` 是 CRLF 归一化后的值） |
| `docs/temp/gov/analysis2.py` / `trayapp.json` | TrayApp 字段分类、方法命名分组、净化后并发统计、跨文件耦合矩阵 |
| `docs/temp/gov/llm.py` / `runner.py` | 本地模型客户端（含 `enable_thinking=false`）与 4 路并行调度器 |
| `docs/temp/gov/out_r1..r4.md` | 第 1 轮原始输出（含思维链；r1/r2/r3 答案被烧空） |
| `docs/temp/gov/out2_r1..r3.md` | 第 2 轮原始输出（关思维链，r1/r2 仍被 `max_tokens` 截断） |
| `docs/temp/gov/s1_scan.py` / `s1_split.py` | S1 的**侦察器**（类型边界 + 嵌套/可见性识别）与**切分器**（逐行切片 + 行尾保真 + 多重集守恒断言，不守恒拒绝写盘）。S2/S5 可复用 |
| `~/.workbuddy/_backup/20260912_s2_spec/` | **S2 的回滚点**：9 个待改源文件 + bench 前的台账快照（`ledger/`），逐一 sha256 校验过。⚠️ S0–S2 **全部尚未提交**（git HEAD 停在治理之前）⇒ 回滚**不能**用 `git checkout`，那会连 S0 的 `LogSink` 一起丢掉 |
| `docs/temp/gov/s2_verify_cfgid.py` / `s2_patch.py` / `s2_xcheck.py` | S2 的**离线验算**（用 Python 复现 `cfgId` 算法 —— 先证明口径与台账一致，再动 C# 代码）、**补丁器**（13 处替换 + 逐处断言命中数，不符就不写盘）、**交叉核对器**（从生成的 C# 字面量反解 args 复算 `cfgId`，独立通道复核）。另 `s2_docs_sync.py` / `s2_mem_sync.py` 是文档与记忆的同步器 |
| `G:\Tools\_evidence\ivtest\` | `InvokeRequired` 语义最小复现工程（可 `dotnet run` 复跑）。**必须放在项目树之外** —— SDK 默认只排除**项目根**的 `bin`/`obj`，放在 `docs/` 下的子工程会把它的 `obj/*.cs` 卷进主项目编译，报 CS0579 特性重复（详见 §9.5） |

> 这批产物是**结论的可复现证据**，不是临时垃圾 —— 保留在 `docs/temp/` 下（符合本项目"临时文档进 `docs/temp/`"的约定）。若要清理，请连同本节的引用一起处理。

### 8.3 与既有经验的关系

本评估**不替代**已有沉淀，而是它们的上位视图：

- `dsh-tray-dev` skill：单点机制与作业流程（发布、退出语义、菜单结构、性能日志）；
- 本文：**结构性问题与治理路线**，回答"为什么这个托盘开始变得难改"。

**建议回流到 skill 的三条**（可在其他 .NET WinForms 项目复用）：

1. **"锚点未就绪"是 marshal 封装的头号陷阱**：任何 `if(ctrl.InvokeRequired) BeginInvoke else 直接干` 的写法，在控件句柄未创建时都会**静默降级为"在当前线程执行"**，并**把句柄永久绑到该线程**。正确锚点是构造后捕获的 `SynchronizationContext`（`Post` 会入消息队列，比 `BeginInvoke` 一个不存在的句柄严格更强）。
2. **`Process` 的异步读回调与 UI 线程共享的可变缓冲必须自带同步** —— `OutputDataReceived` / `ErrorDataReceived` 是**两个**线程池回调，不是"同一个 IO 线程顺序触发"。
3. **注释里写的"有界/避免越用越慢"必须有一条断言兜着** —— 否则实现漂移后，注释会变成下一轮改动的错误前提。

### 8.4 本轮的一次自纠（留档，防止重犯）

核实时我第一次用 `grep -n "E:\\\\\|llama-b1"` 去找 `PerfProbe` 的硬编码路径，得到**零命中**，于是差点把 r3 的这条结论判成"模型编造"。换一条独立命令（`sed -n` 直读 + `grep` 换个模式）后，**路径确实存在**（`ModelPerf.cs:902-905`）—— 是我的**转义写坏**，不是模型幻觉。

**教训**：核实幻觉时，**grep 模式本身也是待验证对象**。凡是"零命中"这种强结论，必须换**至少一条不同机制的命令**（`sed` 直读 / 换模式 / Read 工具）交叉确认，否则会把真相判成幻觉 —— 而本项目的环境里 `MSYS_NO_PATHCONV=1` + 反斜杠路径本就容易让转义出错，这条尤其容易复发。

---

## 9. S0 实施记录（2026-09-11 当晚完成）

S0 的 7 项承重缺陷全部修完，**不动结构**。改动 8 个文件（5 改 + 3 新增），`+181 / -29` 行。

### 9.1 逐项落点

| 项 | 问题 ID | 落点 | 做法 |
|---|---|---|---|
| ① UI 调度锚点 | **P0-1** | 新增 `UiDispatcher.cs`；`Program.cs` 的 `Ui()`；`LogForm.AppendTo()` | 锚点改为**构造时在 UI 线程显式建好句柄的隐藏 `Control`**。`Ui()` 与 `AppendTo` **两处一起换** —— 只换一处等于没换（`AppendTo` 反而先被命中）。句柄异常缺失时**放弃更新并上报**，而不是退回"在后台线程裸碰控件" |
| ② `svc.log` 有界 + 同步 | **P1-1 / P1-2** | 新增 `LogSink.cs`；`Service.cs`；`Program.cs:1433` | 裸 `StringBuilder` → 有界 `Queue` + 锁。`Length` 保持**单调递增**当游标、`Read(游标)` 自动夹取到窗口起点 ⇒ **调用点语义完全不变**，安全降级（丢历史、不丢新日志） |
| ③ RichTextBox 上限 | **P1-3** | `Program.cs` `LogForm.Trim()` | 超 `MaxChars`（默认 100 万字符）时裁掉"超出量 + 20%"，避免此后每次追加都裁一次。裁剪前临时解除 `ReadOnly`（否则 `EM_REPLACESEL` 被系统忽略） |
| ④ `Bg`/`Ui` 异常落盘 | P0-1 衍生 | `Program.cs` `ReportEx()` | 后台任务与 UI 回调里的异常不再 `catch{}` 静默：进 `tray-ex.log`（与全局 handler 同一文件）+ 日志窗口可见；带防重入闩 |
| ⑤ `renderSessionItem` | **P0-2** | `ThinChatForm.cs` | 四条连续 `return` → 单次字符串拼接 |
| ⑥ 插件「应用」不阻塞 UI | **P1-4** | `PluginManagerForm.cs`；`Program.cs` `ApplyPluginSettings` | `onApply` 整体丢后台；按钮忙闲 + 结果回贴弹窗日志。其中唯一真正碰 UI 的 `RefreshChecks()` 由 TrayApp 侧 `Ui()` marshal 回 UI 线程（否则会跨线程改菜单项） |
| ⑦ `TailSamples` 真尾部读 | **P1-7** | `ModelPerf.cs` | `ReadAllLines` 整片读 → 按 64KB 块**反向扫描 `\n`**。代价与 `maxLines` 成正比、与文件大小无关（UTF-8 续字节恒 ≥ 0x80，按字节切行对中文安全） |

### 9.2 新增两个自检

- **`--selftest-uithread`**（实例探针 —— 必须有一个"已创建但未 Show"的 `LogForm`，那正是 P0-1 的触发场景）：9 项断言。判别力核心是**排队语义** —— "消息泵未跑时，后台线程发起的 UI 更新**必须还没生效**"。旧实现下这条必 FAIL（它会当场写入，并把句柄绑到线程池线程）。
- **`--selftest-logsink`**（纯数据层，不建 TrayApp、不占单实例锁）：10 项断言，含 8 线程 × 20000 行并发写、`Length` 守恒、窗口有界、**旧游标夹取**、单行超上限仍保留、`Clear` 不重置游标。

### 9.3 实测结果（`bin/Release/net8.0-windows/DSHTray.exe`）

| 自检 | 结果 |
|---|---|
| `--selftest-uithread` | **PASS 9/9** —— `logForm.Visible=False`、`HandleCreated=False`（正是 P0-1 场景）；`uiDisp ready=True anchorThread=1` |
| `--selftest-logsink` | **PASS 10/10** —— 并发 16 万行无异常；`Length` 2471120 守恒；`Retained` 65536 = 上限；旧游标被夹取到窗口起点 |
| `--selftest-perf` | **ALL PASS 39/39** |
| `--selftest-svcmenu` / `-exit` / `-lock` / `-plugins` / `-logwin` | 全部 PASS（`OLD-TOPLEVEL-ENTRIES = 0`；env scrub 4 项全 PASS） |
| `--selftest-rpc` | 连 3080 失败 —— **环境状态**（DSH 当时未启动），非回归 |

### 9.4 过程中抓到并修掉的一个"自己的 bug"（留档）

`ReadTailLines` 第一版对**以换行结尾的文件**会多吐一个空行：反向扫描时，文件末尾那个 `\n` 会先凑出一个空串。`--selftest-perf` **立刻抓到 4 项 FAIL**（`SAMPLE-WRITTEN` / `AGG-MEDIAN-TG` / `RT-FEED-FLUSH` / `BENCH-SAMPLE`），且失败值恰好各**多 1 行**（修前 `lines=4`、`lines=5` → 修后 `lines=3`、`lines=4`），与推断完全吻合。

修正判据：`cur` 与 `rev` **同时为空**时才跳过（那是"文件末尾换行"产生的空串）；`rev` 已非空则说明文件里**确实有**一个空行，必须保留。修后 39/39 全绿。

> 这条正好反向印证了 §8.3 的第 3 条：**注释里写的"有界/避免越用越慢"必须有一条断言兜着**。没有断言，这个 off-by-one 会静默活到线上，表现为"性能页签偶尔少一行样本"——极难归因。

### 9.5 顺带修掉的一个环境坑（编译层面）

上一轮为验证 `InvokeRequired` 语义，我在**项目树内**建了 `docs/temp/gov/ivtest/`。SDK 默认只排除**项目根**的 `bin`/`obj`，**不排除子目录** —— 于是 `ivtest/obj/*.cs` 被主项目当源码编译，`dotnet build` 报 **CS0579 特性重复**（9 个错误）。已把该工程移到 `G:\Tools\_evidence\ivtest\`，编译恢复 0 错误（32 个警告 = 改造前基线，我新增的 3 个 nullable 警告已消除）。

**可复用推论**：任何在项目树内新建的 `.csproj` 子工程都会污染主项目编译；最小复现/证据类工程必须放在项目树**之外**。

### 9.6 尚未做（S1 起）

S0 只修承重缺陷，**不做模块拆分**。S1（按文件拆类型，零逻辑改动）、S2（`ServiceSpec`/`ServiceRuntime`，硬指标是 `cfgId` 不变）、S3（抽 `QwenTray.Core`）、S4（`Cli` + 测试工程）、S5（拆 `TrayApp`）见 §6。

**上线**：`dotnet publish -c Release -o publish-next` 已产出 → 双击桌面 `apply-dsh-tray.cmd`（会优雅退出旧托盘 → robocopy → 启新托盘；不碰正在跑的模型服务）。

---

## 10. S1 实施记录（2026-09-11 当晚，紧随 S0）

**目标**：同工程内按文件拆类型，**零逻辑改动**。`Program.cs`（1868 行 / 7 个类型）与 `ModelPerf.cs`（1077 行 / 17 个类型）拆成按职责命名的文件；`TrayApp`（1516 行 / 157 个成员）用 **`partial` 按职责分 5 块**（粒度由用户选定）。

**结果**：2 个文件 → **20 个文件**（共 3300 行）。原文件改名留档为 `Program.cs.bak-s1` / `ModelPerf.cs.bak-s1` —— `.cs.bak-*` 后缀不被 SDK 编译，且被 `.gitignore` 的 `*.bak-*` 覆盖，**既能一键回滚又不污染编译与仓库**。

### 10.1 拆分清单

| 新文件 | 行 | 来源（原文件行号） |
|---|---:|---|
| `LogForm.cs` | 153 | `Program.cs` L16-150（含头部说明注释） |
| `SvcMenu.cs` | 31 | L188-200（原 `TrayApp` **私有嵌套类 → 顶层 `internal`**） |
| `TrayApp.cs` | 380 | partial 1/5：字段 + 构造函数 + 配置读写（L152-187 + L201-525） |
| `TrayApp.Services.cs` | 218 | partial 2/5：探活 + 每模型二级菜单 + 启停（L526-723） |
| `TrayApp.Dsh.cs` | 287 | partial 3/5：DSH 进程 + 环境清洗 + 日志跟踪（L724-990） |
| `TrayApp.Sessions.cs` | 461 | partial 4/5：UI 线程探针 + dsh 配置 YAML + 会话/弹窗/插件 + 最近会话缓存（L991-1431） |
| `TrayApp.Lifecycle.cs` | 255 | partial 5/5：`Adopt` + `Tick` + 退出 + 全部自检探针（L1432-1667） |
| `RecentSession.cs` | 19 | L1669 |
| `TaskbarWatcher.cs` | 25 | L1671-1677 |
| `NativeMethods.cs` | 21 | L1678-1680 |
| `Program2.cs` | 204 | L1682-1867（`Main` + 9 个诊断开关分派） |
| `PerfFingerprint.cs` | 133 | `ModelPerf.cs` L21-132（模块总说明 + 配置指纹） |
| `LlamaLogParser.cs` | 49 | L133-160 |
| `PerfModel.cs` | 111 | L161-250（8 个数据模型） |
| `PerfStore.cs` | 228 | L251-457 |
| `PerfSampler.cs` | 83 | L458-519（含嵌套 `Bucket`） |
| `BenchRunner.cs` | 87 | L520-585 |
| `PerfRuntime.cs` | 130 | L586-694 |
| `PerfPanel.cs` | 255 | L695-928 |
| `PerfProbe.cs` | 170 | L929-1076 |

### 10.2 为什么 `TrayApp` 可以用 `partial` 而不必"拆类型"

`partial` 是**编译器零感知**的机制：多个声明在编译期合并为**同一个类型**，成员可见性、私有访问、`this` 语义全部不变。它唯一真实的风险是**字段初始化顺序**（跨文件的初始化器顺序在 C# 里是"未指定"的）—— 所以动手前先取证：`TrayApp` 的 67 行字段区**没有任何跨字段依赖**（全是字面量 / `new()` / `GpuSelection.FromCfg("all")` 这类不读其它字段的初始化），因此拆分**不改变任何语义**。

这样做的收益：`TrayApp` 从 1516 行的单块变成 255-461 行的 5 块，**S5 时不需要再搬一次**。

### 10.3 唯一的可见性变化（已单独取证）

`SvcMenu` 原本是 `TrayApp` 的**私有嵌套类**（`class SvcMenu`，无修饰符）。嵌套类**可以访问外部类的私有成员**，所以"能否拆出去"必须验证而不是假设：

- 实读 L190-200：字段全是它自己的 `ToolStripMenuItem` / `List<ToolStripMenuItem>` / `string`，**不引用 `TrayApp` 的任何私有成员** ⇒ 提升为顶层 `internal` 语义等价（使用点 `BuildSvcMenu` / `RefreshSvcMenu` 全在 `TrayApp` 内，同命名空间直接可见）。
- 这是本次拆分**唯一**的代码改写（`  class SvcMenu {` → `internal class SvcMenu {`）。

### 10.4 三个"零逻辑改动"的证明手段（可复用）

1. **逐行切片 + 行尾保真**。`Program.cs` 是**纯 CRLF**、`ModelPerf.cs` 是**纯 LF**（实测：1867/1867 vs 0/1076）。读用 `newline=''`、切用 `split('\n')`（`\r` 留在行尾）、写用 `join('\n')`，**逐字节搬运**；连新增的模板行（using/namespace/来源注释/闭合 `}`）也按源文件行尾补 `\r`。→ 拆完两类文件各自行尾风格不变。
2. **多重集守恒断言（脚本内置，不通过就拒绝写盘）**。把"原文件有效内容行"（剔除 `using` / `namespace` / 类声明行 / 新增注释行）与新文件合并后的多重集比对。实测：**缺失 0**，多出 **4** —— 恰好等于 4 个 partial 块各自补的类型闭合 `}`（第 5 块沿用原文 L1667 的闭合行）。这条断言把"手滑漏切/切重"变成**脚本级硬失败**，而不是等着编译器或人去发现。
3. **方法级守恒**。原 `TrayApp` 区段（L152-1667）的 160 条成员声明 vs 5 个 partial 文件合计 159 条 —— 差的 1 条是 `SvcMenu` 的 `cfgLines` 字段（**随嵌套类移出，预期内**）。

### 10.5 实测结果

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 错误 / 32 警告**（= S0 后基线，未新增） |
| `--selftest-uithread` | **9/9 PASS** |
| `--selftest-logsink` | **10/10 PASS** |
| `--selftest-perf` | **39/39 PASS** |
| `--selftest-exit` / `-lock` / `-svcmenu` / `-plugins` / `-logwin` / `-menushow` | 全 PASS（`svcmenu`：`OLD-TOPLEVEL-ENTRIES=0`、`svcMenus=3`、每服务固定 20 子项；`menushow`：29 项 / 10 条最近会话） |

→ **零行为改动得到实证**：这是一次纯结构位移，不是"顺手改点东西"。

### 10.6 过程中踩到的三个切分边界（留档，供 S2/S5 复用）

1. **`using` 块边界要靠 assert 而不是眼看**：`Program.cs` 的 `using` 只到 **L13**（L14 是空行、L15 才是 `namespace`）。首版脚本按"L1-14"切，`Pusing[-1].startswith('using ')` 直接断掉 —— 正是这个 assert 拦下的。
2. **CRLF 会让"行内容相等"的断言假失败**：`P[14] == 'namespace QwenTray;'` 在 CRLF 文件里实际是 `'namespace QwenTray;\r'`。凡涉及行内容比较，一律 `.strip()` 或显式带上 `eol`。
3. **每个 `partial` 块必须有各自的类型闭合 `}`**：原文件只有 1 个 `TrayApp` 的 `}`（L1667），切成 5 块后需要 5 个。首版漏了这一条，多重集校验报了"缺失 1 个 `}`"——**再次证明守恒断言比人眼可靠**。

### 10.7 尚未做（S2 起）

S2（`ServiceSpec`/`ServiceRuntime`，硬指标是 `cfgId` 不变）→ S3（抽 `QwenTray.Core`，编译期卡住"不引用 `System.Windows.Forms`"）→ S4（`Cli` + 测试工程）→ S5（拆 `TrayApp`）。见 §6。

> **S1 对后续的直接影响**：S5 的"拆 `TrayApp`"已经从"搬 1516 行"变成"把 5 个 partial 文件按块升格为独立类型"，风险显著下降。

---

## 11. S2 实施记录（2026-09-12 凌晨）

**一句话**：把「配置」从运行时状态袋里切出来，让两个纯逻辑模块（`LaunchArgs`、`PerfRuntime`）的签名里**不再出现 `Service`** —— 这是 S3 能把它们搬进 `QwenTray.Core` 的前提。

### 11.1 改了什么（6 个文件，13 处替换）

| 文件 | 改动 | 规模变化 |
|---|---|---|
| **`ServiceSpec.cs`**（新增） | 10 个配置字段 + `From(ServiceConfig)`；**不引用** `System.Diagnostics.Process` / WinForms | 3,085 B / 55 行 |
| `Service.cs` | 配置部分 → `Spec` 字段 + 10 个转发属性；运行时状态原样保留 | 1,733 → 2,993 B |
| `LaunchArgs.cs` | `Build(Service` → `Build(ServiceSpec`（1 处） | 6,539 → 6,543 B |
| `PerfRuntime.cs` | `OnStart`/`OnReady`/`OnFail`/`OnLlamaLine` 4 处签名 | 5,766 → 5,782 B |
| `TrayApp.cs` | 服务列表构造 2 行 → `new Service{ Spec=ServiceSpec.From(sc) }` | 35,857 → 35,657 B |
| `TrayApp.Services.cs` / `TrayApp.Lifecycle.cs` | 7 处调用点 `svc` → `svc.Spec` | — |
| `PerfProbe.cs` | 3 处探针调用改用 `svc.Spec` + **新增 `FP-REAL-8081` 回归锚** | 10,813 → 11,945 B |

**切口是怎么定下来的**：不是靠读代码猜，而是先把 `Service` 的 18 个成员按"来源"分类 —— 10 个从 `ServiceConfig` 来（配置）、8 个由进程生命周期写（运行时）—— 再对 `LaunchArgs` / `PerfRuntime` 做**字段级**的读写点核查，确认它们只碰前者。核完发现两个附加事实：

- **`Service.Ctx` 是死字段**：全项目 `grep '\.Ctx\b'` **零命中**（既非配置进入、也无任何读写）。依「宁留注释不删」惯例搬进 `ServiceSpec` 并注明来历，没有顺手删掉。
- **`Service` 从不被序列化**：JSON 契约是 `ServiceConfig`（`Config.cs:9-20`），`Service` 是纯内存对象 —— 所以字段改属性不会破坏配置文件兼容性。这条必须先证实，否则整个方案不成立。

### 11.2 为什么用「组合」而不是「继承」

`class Service : ServiceSpec` 是更省事的路子：零转发代码、零调用点改动（`LaunchArgs.Build(svc, ...)` 靠隐式向上转型照常编译）。放弃了它，理由只有一条但够硬：

> `Service`「has-a 配置」，不是「is-a 配置」。

继承会把"运行时对象是一种配置"这个**错误语义**固化进类型系统 —— 将来给 `ServiceSpec` 加不可变性或相等性约束时，`Service` 会被迫一起继承（而它显然不该是不可变的）。11 行转发属性换一个不会反噬的边界，划算。转发属性也**不是设计**，是迁移期兼容层，`Service.cs` 里已就此写明（并提示新代码优先写 `svc.Spec.X`，以区分"读配置"与"读运行时"）。

### 11.3 为什么**没有**同时拆 `ServiceRuntime`

§6 的表里 S2 写的是"`ServiceSpec`/`ServiceRuntime` 拆分"。实际只拆了 `ServiceSpec`，`ServiceRuntime` 没动 —— 这是有意的取舍，不是漏做：

- `ServiceRuntime` 独立化对 **S3（抽 Core）零收益** —— Core 只需要 `ServiceSpec`，运行时状态本来就该留在 UI 侧。
- S5 拆 `ServiceManager` 时**必然要重做**这一块（届时 `proc`/`log`/`Starting` 的归属要按新管理器重新划）。现在拆，只是给一个马上还要改的东西多加一层间接。
- 一个具体的反例：`Starting`/`runCtx`/`runVision`/`PortBusy` 都是 `volatile` 字段。包装成转发属性后 volatile 语义**能保住**（真正读写的仍是 `ServiceRuntime` 里的 volatile 字段），但这层"为什么还在"需要额外解释 —— 为一个 3 天后就要删的东西付这个认知成本不值得。

判断准则记下来：**拆分的收益要看它解锁了什么下游动作**。解锁 S3 的才做；只让当前代码"看起来更整齐"的，不做。

### 11.4 验收（§6 定的硬指标：`cfgId` 必须与改造前完全一致）

`cfgId = sha1(排序后的启动参数 + llama 目录名)[:10]`，是性能台账的主键 —— 它一变，历史样本就与台账对不上，"哪套配置更快"这个问题就失去可比性。所以这条指标不是形式主义。

| 验证 | 方法 | 结果 |
|---|---|---|
| **算法未变** | 新增 `FP-REAL-8081` 断言：把台账里 `id=966d1620a3` 那条的 `args` **原文**抄进自检，重算必须仍是 `966d1620a3` | **PASS**（`--selftest-perf` 39 → **40/40**） |
| **落盘路径未变（端到端）** | 对**正在跑的** 8081 执行 `--bench 8081`（prompt=512 gen=64 runs=3） | 打印 **`已记入台账 cfg=966d1620a3`** ✓ |
| **不产生脏条目** | bench 后台账条数 | **仍 17 条**（未新建）✓ |
| **样本忠实落盘** | `samples-2026-09.ndjson` 新增行 | `{"ts":"2026-09-12T01:12:50+08:00","cfg":"966d1620a3","src":"bench",...}` ✓ |
| 编译 | `dotnet build -c Release` | **0 错误 / 30 警告**（基线 32 —— 反而少 2 个：`Service` 的裸 `string Name;` 之类移进有初始化器的 `ServiceSpec` 后，CS8618 自然消失） |
| 行为回归 | 9 项自检 | uithread 9/9 · logsink 10/10 · perf 40/40 · exit/lock/svcmenu/plugins/logwin/menushow 全 PASS |
| 发布产物 | 用 `publish-next` 里的 EXE 重跑 uithread/logsink/perf | 全绿（证明发布物确实含新代码） |

**实测性能**（顺带产物，也是"8081 上是哪套配置"的一次真实记录）：`pp=1166.9 t/s · tg=87.5 t/s · TTFT=325.9 ms`。

### 11.5 一个值得复用的验收范式：给「不可见的行为」配一个**可执行锚**

`cfgId` 不显示在任何界面上，改了它也不会报错 —— 只会在几周后表现为"性能页签里的数据看着不对"。这类**静默的行为契约**最容易被重构无声破坏。

这次的处理是把契约**变成一行断言**（`FP-REAL-8081`），且锚点取自**真实台账原文**而不是我手搓的样例 —— 因为手搓的样例只能证明"算法对我的心算成立"，取自真实数据的锚才能证明"算法对**生产中已写进磁盘的那批数据**成立"。

副产品：这条断言现在永久留在 `--selftest-perf` 里。以后任何人改 `PerfFingerprint.Normalize` 的剔除规则、或动 `LaunchArgs.Build` 的参数顺序，都会在自检里当场撞线 —— 而不是等到台账出现两条本该是同一条的配置。

配套的一条小纪律：**改前先取基线快照，改后用同一条命令复现**。本次是「记下 `966d1620a3` + 备份台账 → 改造 → 重跑 bench → 比对」。

### 11.6 尚未做（S3 ✅ / S4 ✅ / S5 ✅ —— S5 落地见 §14 / §15）

| 步 | 内容 | 本次为它铺了什么 |
|---|---|---|
| **S3** ✅ | 抽 `QwenTray.Core`（`Config` / `ServiceSpec` / `Perf*` / `DshAuth` / `DshRpc` / `LaunchArgs`）—— 已完成，见 §12 | `LaunchArgs` 与 `PerfRuntime` **已不再引用 `Service`**；`ServiceSpec` 本身无 WinForms 依赖 ⇒ 边界已可在编译期卡住 |
| **S4** ✅ | `Cli` + `QwenTray.Tests` —— 已完成，见 §13 | `FP-REAL-8081` 已示范"纯逻辑可被断言驱动"；S4 把它搬成了标准单测（`dotnet test` 即可跑，不必起托盘） |
| **S5** | 拆 `TrayApp` → `ServiceManager` / `MenuBuilders` / `LogSink` | `Service` 的转发属性就是删除清单：到了 S5，把 10 个转发属性换成对 `Spec` 的直接访问即可，编译器会**逐处报错**指路（这正是转发布局比继承好的第二个理由） |

> ✅ **版本控制状态已收口（2026-09-12）**：S0/S1/S2 曾长期**全部未提交**（`git status` 里 `Program.cs` 是 `D`、新文件全是 `??`），那时 git HEAD 停留在治理开始之前 ⇒ 回滚**不能**用 `git checkout`（会连 S0 的 `LogSink` 一起丢）。现已按 3 个分层提交入库并在独立 worktree 上做过全新检出验证：
>
> | 提交 | 内容 | 验证 |
> |---|---|---|
> | `e667cb3` | S0+S1 | 在 pre-S2 状态下编译 0 错误 / 32 警告（= S1 记录基线） |
> | `92d93f8` | S2 | 改动面精确命中预期的 8 文件 + `ServiceSpec.cs`，零越界 |
> | `2100eba` | 文档 + `.gitignore` | — |
>
> 分层手法：S2 的回滚点里**恰好存着那 9 个文件的 pre-S2 版本**，于是"先铺回 pre-S2 → 提交 S0+S1 → 再铺回 post-S2 → 提交 S2"就能把两种状态都还原出来（靠 sha256 逐文件校验保证没有手滑）。收口后用 `git worktree add --detach` 在**独立目录**做了全新检出编译 + 跑满自检 —— 这一步专治"有没有文件忘了提交"，是 `git status` 看不见的风险。

---

## 12. S3 实施记录（2026-09-12）

**一句话**：把无 UI 依赖的逻辑层搬进独立工程 `QwenTray.Core`，并把"Core 不许碰 UI"从**口头约定**变成**编译期围栏**。

### 12.1 搬了什么（15 个文件，git 全部识别为 `rename` ⇒ 历史保留）

| 归入 `src/QwenTray.Core/` | 说明 |
|---|---|
| `Config.cs` | JSON 配置契约 |
| `ServiceSpec.cs` | S2 切出的配置描述 |
| `LaunchArgs.cs` | 启动参数构建（S2 起只依赖 `ServiceSpec`） |
| `PerfFingerprint.cs` / `PerfModel.cs` / `PerfSampler.cs` / `PerfStore.cs` / `PerfRuntime.cs` | 性能数据层（指纹 → 解析 → 模型 → 存储 → 采样/运行时） |
| `LlamaLogParser.cs` / `BenchRunner.cs` | stdout 解析 / 命令行基准 |
| `DshAuth.cs` / `DshRpc.cs` | dsh 认证与 RPC |
| `GpuInfo.cs` / `HwInfo.cs` / `SysInfo.cs` | 硬件/系统采集（含 WMI，故 Core 仍带 `System.Management` 包） |

**命名空间有意保持 `QwenTray` 不变** ⇒ 调用点**零改动**。本步只换程序集边界，不换名字；否则"零逻辑改动"这条就守不住了。

**有意*未*搬（写下来是为了避免下一轮重复讨论）**：

| 文件 | 不搬的理由 |
|---|---|
| `AutoStart.cs` | 真用 `Application.ExecutablePath`；且按 §5 目标架构它归 `Integr.` 而非 `Core` |
| `Service.cs` | 持有 `Process` 句柄 + `LogSink`，是运行时状态袋（§5 的 `ServiceRuntime` 与 `Core` 是两块） |
| `LogSink` / `UiDispatcher` / `PerfPanel` / `PerfProbe` | UI 或探针，属 `Ui` |

### 12.2 围栏：`UseWindowsForms=false` —— 断言不是文档，是编译器

Core 的 `.csproj` 里没有写"请不要引用 WinForms"这种注释当约定，而是把 `UseWindowsForms` 设为 `false`：此后 Core 内任何 `using System.Windows.Forms;` / `MessageBox.` / `Control` / `Application.` 都会直接 **CS0234 编译失败**。

这条围栏**当场就抓到了东西**：S1 按类型切 `ModelPerf.cs` 时，切分器把原文那 17 行 `using` 块**复制进了每个文件**，于是 `Perf*` / `BenchRunner` / `LlamaLogParser` 里躺着 **14 行僵尸 using**（`System.Drawing` + `System.Windows.Forms`），而它们**一行都没用到**。S0–S2 三轮里主工程本来就引用 WinForms，所以这 14 行一直"合法地"躺着，没有一次编译器警告、没有一次人眼发现 —— 直到围栏把它们逼出来。本提交的 `48 insertions / 14 deletions`，那 14 处删除就是它们。

**围栏自身也做了负向测试**（"测试你的测试"）：往 Core 的 `LaunchArgs.cs` 头部注入一行 `using System.Windows.Forms;` → 编译如期 `CS0234`。

> ⚠️ **负向测试的第一次是假阳性，值得留档**：我第一版把 `using` **追加到文件末尾**，报的是 `CS1529`（using 子句位置错）—— **错了，但错对了**（"编译失败"这个判据被满足）。换个错因就"通过"了，等于没测。**负向测试的判据必须落在目标错因上**，否则它只证明了"随便改点什么都会编译不过"。

### 12.3 子工程 `.csproj` 的坑：必须排**整个目录**

主工程 `QwenTray.csproj` 需要把 Core 目录排除出自己的默认 glob：

```xml
<DefaultItemExcludes>$(DefaultItemExcludes);src/QwenTray.Core/**</DefaultItemExcludes>
```

**不能只排 `src/QwenTray.Core/*.cs`**：SDK 默认只排除**项目根**的 `bin`/`obj`，子工程的 `src/QwenTray.Core/obj/*.cs`（`AssemblyInfo` 那批 `Assembly*Attribute`）会被卷进主工程的编译，报 **CS0579「特性重复」**。这个坑 §9.5 已经在证据工程上踩过一次，本条是把那次教训**前移**成一条写进 csproj 的硬规则。

### 12.4 验收：**同机同配置的差分法**

改造前后各跑一遍**完全相同的命令集**，逐字节比对。这一步的价值不在于"跑通"，而在于**排除环境性失败**：

| 验证 | 方法 | 结果 |
|---|---|---|
| 编译 | `dotnet build -c Release`（全量重建） | **0 错误 / 30 警告 = 基线**（Core 1 + 主体 29，只是重新分配，无新增） |
| 10 项自检 + `--dump-menu` | 改造前后各跑一遍，`diff` | **实质内容逐字节一致**；差异只有时间戳与"已运行 2h59m→3h1m"这类时变量。`lock`/`exit`/`logsink`/`uithread`/`plugins`/`menushow`/`menu-dump` **完全相同** |
| 数据可比性锚 | `--selftest-perf` + 真实 `--bench 8081` | `FP-REAL-8081 = PASS (966d1620a3)`；bench 打印 **`已记入台账 cfg=966d1620a3`**；台账仍 **17 条**、该配置 `runs` 2→3、无重复条目 |
| 代码是"搬走"而非"复制两份" | 程序集体积（**独立通道**） | `DSHTray.dll` 345,088 → 286,720（−58,368）；`QwenTray.Core.dll` 64,512 ⇒ 量级吻合 |
| 发布链可用 | 读回 `apply-dsh-tray.cmd`：`robocopy "%SRC%" "%PUB%" /E` | **整目录**同步 ⇒ 新增的 `QwenTray.Core.dll` 自动随发布带上，无需改脚本 |

**两个"假警报"被差分法当场排除**（这正是先取基线的用处）：

1. `--selftest-rpc` **改造前后同样**报 `RPC FAIL: 由于目标计算机积极拒绝 (127.0.0.1:3080)` —— 是 dsh web 当时没开，**环境性失败**。没有基线的话，这会被当成 S3 引入的回归去查半天。
2. `--selftest-menushow` 曾超时一次（`exit=124`），但输出文件内容与基线一致。在两棵树**各复跑 3/3** 全部 `exit=0` ⇒ 抖动，非回归。

### 12.5 两条可复用的结论

1. **架构边界要用编译器表达，不要用文档表达。** `UseWindowsForms=false` 这一行比一页"编码约定"都硬 —— 它在 S3 落地当天就清掉了 14 行躺了三轮的死代码。凡是"某模块不许依赖 X"这类约束，优先找**能编译失败**的写法（关闭引用、独立 TFM、`BannedApiAnalyzers`），退而求其次才是文档 + 人工 review。
2. **断言本身要有负向测试。** 正面用例只能证明"正常情况下它不拦"，证不了"该拦的时候它真拦"。而且负向测试的**判据要落在目标错因上**（§12.2 的 `CS1529` vs `CS0234` 假阳性）。

### 12.6 尚未做（S4 ✅ / S5 ✅ —— S5 落地见 §14 / §15）

| 步 | 内容 | 本次为它铺了什么 |
|---|---|---|
| **S4** ✅ | 抽 `Cli` + 建 `QwenTray.Tests`，给纯逻辑加单测（`LlamaLogParser` / `PerfFingerprint` / `Config` 合并语义 / `LaunchArgs.Build`）—— **已完成，见 §13** | **`QwenTray.Core` 现在就是一个可被引用、可被单测、且保证没有 UI 依赖的程序集** —— 这是加测试工程的前提条件，也是 S3 的真正目的 |
| **S5** | 拆 `TrayApp` → `ServiceManager` / `MenuBuilders` / `LogSink` | `Service` 的 10 个转发属性就是删除清单（见 §11.6） |

> **发布状态**：S3 纯内部结构改动，**对外行为零变化**（自检差分可证）。是否随发布上线都不影响功能；要上线仍需双击桌面 `apply-dsh-tray.cmd`（它会先跑 `--selftest-exit` + `--selftest-svcmenu` 验证 `publish-next` 确实是新版）。

---

## 13. S4 实施记录（2026-09-12）

**范围**（§6 定义）：抽 `Cli` + 建 `QwenTray.Tests`，先给纯逻辑加单测。落地为 3 处代码改动 + 1 个新工程，`+804 / −39` 行。

### 13.1 抽 `CliOptions`：把「血的教训」变成可穷举断言

原来 `Program2.Main` 前 70 行是 11 个 `Array.IndexOf(args,"--selftest-…")>=0` 的内联解析，解析结果决定**进程要不要走到 `Application.Run`**。这段代码的历史伤疤就写在它自己的注释里：

> 曾漏写 `exitProbe` 分支（它又跳过了单实例锁），`--selftest-exit` 一路落到 `Application.Run`，起出一个「没有锁的幽灵托盘」+ 通知区图标且永不退出，只能手工 taskkill。

S4 把这段解析抽成 `src/QwenTray.Core/Cli.cs` 的 `CliOptions`（纯数据、零副作用），并给两个判定起名字：

| 判定 | 原来 | 现在 |
|---|---|---|
| 不占单实例锁 | `!dump && !selftest && !menuProbe && … && benchPort<=0`（12 项长串） | `!cli.IsLockFree` |
| 防御栏（绝不走到 `Application.Run`） | `if(dump\|\|selftest\|\|menuProbe\|\|…\|\|uiProbe)`（10 项） | `if(cli.IsDiagnostic)` |

两处都**等价**（防御栏比原清单多覆盖 `perfProbe`，而它在更早处已 `return`，故实际不可达 —— 方向是单侧收紧）。真正的收益在测试侧：现在有一条断言枚举**每一个探针标志**，要求它必须让 `IsDiagnostic` / `IsLockFree` 为真。以后新增探针忘了登记，`dotnet test` 当场红，而不是等某天在通知区里多出一个幽灵。

`Program2.Main` 后续 130 行分派逻辑**一行未改** —— 改动全部落在前 73 行（同义绑定 + 两处判定），这是"行为中性"最容易审的形式。

> 忠实保留一处历史怪癖并用测试钉住：`--dump-menu` **只认 `args[0]`**，其余标志认任意位置。实际调用形态恒为 `DSHTray.exe --dump-menu`，两种写法等价；要"顺手修好"属行为变更，该由独立评估驱动，不该混在抽离里。

### 13.2 为可测性而做的唯一重构：`Config` 的回填抽成纯函数

`Config.Load()` 把"读文件 → 解析 → 空值回填 → 失败时写盘"糊在一个方法里，于是回填语义（哪些字段补默认值、哪些**有意不补**）根本测不到。S4 抽出：

- `Config.MergeFrom(string? json)` —— 纯函数；返回 `null` 表示这份 JSON 不可用（空串 / 非法 / 解析出 null / 没有 services）
- `Config.Merge(AppConfig c)` —— 只补 4 个字符串字段；`Dsh*` 系列**有意不回填**（留空 = 菜单仅提示配置）

`Load()` 行为一字未改（异常吞掉后回落 `Default()` 并写盘的路径照旧）。顺带收益：`MergeFrom` 就是"只读解析"的干净入口 —— 本项目有一条已知约束：重读配置必须只读解析，不能调会写盘的 `Load()`。

### 13.3 覆盖率：如实说明「没达标」

§6 给 S4 定的门槛是「Core 先卡 60%，纯解析类卡 90%」。实测：

| 口径 | 数值 | 判定 |
|---|---|---|
| **Core 总体** | 行 **27.8%**（211/758）、分支 39.1% | ❌ **未达 60%** |
| 已纳入范围的类 | `Cli` 100%、`LlamaLogParser` 100%、`PerfFingerprint` 96.4%、`LaunchArgs` 95.2~100%、`Config`（回填部分）100% | ✅ 达 90% |
| 未纳入的文件（0 覆盖） | `PerfStore` 140 行、`PerfRuntime` 73、`PerfModel` ~64、`HwInfo` 45、`BenchRunner` 45、`PerfSampler` 40、`DshRpc` 36、`SysInfo` 31、`GpuInfo` 28、`DshAuth` 26 | 不在 S4 范围 |

**结论**：27.8% 是**范围事实**，不是失败 —— S4 的定义只含 4 个类，Core 里其余 11 个文件一行没测。「Core 卡 60%」这个门槛在 S4 这步**不成立**，它实质是 S5/S6 的目标。**建议改口径为「按已纳入范围的类卡 90%」**，Core 总覆盖率单列为趋势指标。⚠️ **S5 收口后复核（§15）**：`MenuBuilders` **未**按原定抽成独立类型，但 `SvcLines` / `SvcConfigDiff` 两个新文件进了 Core ⇒ **本节的 27.8% 是 S4 时点值，不可当「当前值」引用**；要引用请跑当次 `dotnet test --collect:"XPlat Code Coverage"` 取最新。

> ⚠️ `Config.Load()` 的 9 行**有意为 0 覆盖**：它有写盘副作用（读不到就把默认配置写回 `Config.Path_`），在测试进程里调它会把文件丢进 `bin/`。要测 IO 行为得另有设计（例如先把路径参数化），本步不做 —— **这个 0 是决定，不是遗漏**。

### 13.4 三条不查档案就会踩的规则

1. **不建 `.sln`**。根目录必须保持"只有一个 `.csproj`"：发布流程用的是 `dotnet publish -c Release -o publish-next`，**无参数 `publish` 在含 `.sln` 的目录会要求显式指定工程而报错**。跑测试显式给路径：`dotnet test tests/QwenTray.Tests/QwenTray.Tests.csproj -c Release`。
2. **主工程 `DefaultItemExcludes` 要加整个 `tests/**`**。这是 §9.5 那个 CS0579 的同一个坑 —— SDK 只排**项目根**的 `bin`/`obj`，子工程 `obj/*.cs` 里的 `Assembly*Attribute` 会被卷进主工程编译。
3. **测试工程也设 `UseWindowsForms=false`**。被测对象 `QwenTray.Core` 本身就是无 UI 程序集，测试侧用同一条围栏 —— 让"Core 没有 UI 依赖"有两个方向的守卫。

### 13.5 验收：同机同配置差分 + 负向测试

| 项 | 结果 |
|---|---|
| 编译 | **0 错误 / 30 警告**（= S3 基线），无 CS0579 |
| `dotnet test` | **93/93 全绿** |
| 负向测试（测断言自身） | 临时从 `IsDiagnostic` 摘掉 `--selftest-perf` ⇒ 如期 **21 通过 / 1 失败**；还原后 sha256 一致 ⇒ 断言不是空转，且判据落在目标错因上 |
| 10 项自检 diff | **8 项与 S3 主树逐字节一致**；2 项差异仅为运行时刻（`logwin` / `perf` 的 `ts` 字段） |
| 关键锚 | `--selftest-perf` **40/40**，含 `FP-REAL-8081 = PASS (966d1620a3)`；单测里另有一条同源的 `Real8081Config_StillHashesTo_966d1620a3`（`dotnet test` 即可跑，不必起托盘） |

**差分过程中排除的两个假警报**（不先取基线就会误判成回归）：

1. worktree 是 **git 检出、没有 `publish/`** ⇒ `cp` 配置失败 ⇒ 自检用了**自动生成的默认配置模板** ⇒ `plugins` / `svcmenu` 输出与主树不同。**这是测试装置差异**，补齐真配置后逐字节一致。
2. 输出文件在终端里显示为乱码（`涓嶅彲鐢`），一度像是编码回归；实为 **UTF-8 文件被 GBK 渲染**，两棵树的文件同为 UTF-8 无 BOM。

> 方法复用：**两棵树在同一时刻各跑一遍**同一批自检再 diff —— 单边采样无法区分"代码回归"与"环境不同"，本次两次假警报都属于后者。

### 13.6 尚未做（S5 —— 见 §14 / §15）

| 步 | 内容 | 后续进展 |
|---|---|---|
| **S5** ✅ | 拆 `TrayApp` → `ServiceManager` / `MenuBuilders` / `LogSink` | **四段全部交完**：`LogSink` + `ServiceManager` 可测面（`SvcStatus`/`SvcProbe`）进 Core（`0b23103`/`d85d342`，见 §14）；菜单**可判定文本**（`SvcLines`）+ 重读配置**判定**（`SvcConfigDiff`）进 Core（`08f370d`，见 §15）⇒ `dotnet test` **193/193**。⚠️ **原定的「独立类型版」（`MenuBuilders` / 进程编排面）经复核判定不做**，理由见 §15.2 |

> **发布状态**：S4 是内部重构 + 新增测试工程，**对外行为零变化**（自检差分可证）。发布链没变，仍走桌面 `apply-dsh-tray.cmd`（`publish-next` 我不重建也不影响，S4 不改变 exe 的前置探针）。

---

## 14. S5 实施记录 —— S5-1 / S5-2（2026-09-12）

**一句话**：本节记录 S5 的**前两段**（`LogSink` / `SvcStatus`+`SvcProbe` 进 Core），也就是"能自动验证"的那半。

> ⚠️ **编号说明（重要）**：本节（含 §14.5）出现的「S5-3 / S5-4」指**原定的「独立类型版」**（把 `MenuBuilders` / 进程编排面搬成独立类型）。
> 实际交付的 S5-3/S5-4 已按 **§15 重新定义**为「把菜单里**可判定的部分**搬进 Core」—— **编号相同、内容不同，勿混**。

### 14.0 为什么拆两段做，而不是一次做完

S5 是路线里唯一标「高」风险的阶段，而它的**验收**（§7 回归清单）本来就要求**真人逐项点菜单**。
把"纯逻辑升格"与"UI 面搬家"混进一次改动，等于让**同一次人工回归**承担两种性质的风险 ——
出了红灯也分不清是"逻辑搬错"还是"菜单搬错"。所以先交可自动验证的那半：

| 段 | 内容 | 可自动验证性 | 状态 |
|---|---|---|---|
| **S5-1** | `LogSink` → `QwenTray.Core` | 单测 + 自检差分 | ✅ `0b23103` |
| **S5-2** | `SvcStatus` / `SvcProbe` → Core（= `ServiceManager` 的**可测面**） | 单测 + 自检差分 | ✅ `d85d342` |
| **原定 S5-3** | `MenuBuilders`：菜单构建与刷新搬出 `TrayApp`（= 独立**类型**版） | **只能真人过回归清单** | ❌ 复核后**判定不做** → §15.2 |
| **原定 S5-4** | `ServiceManager` 的**进程编排面**（`Start`/`Stop`/`Restart`/`ReloadSvcConfig`）搬出 | 需**真实 llama 进程** | ❌ 复核后**判定不做** → §15.2 |
| **S5-3 / S5-4**（实际交付） | 菜单**可判定文本**（`SvcLines`）+ 重读配置**判定**（`SvcConfigDiff`）→ Core | 单测 + 自检差分 | ✅ `08f370d` → §15 |

### 14.1 S5-1：`LogSink` 进 Core

`LogSink` 早在 S0 就已是**独立类型**（`public sealed class`，零 UI 依赖），S5 对它要做的只是挪进 Core
—— 所以这块的"拆分"是 **0 行代码改动**，只有一次 `git mv`。

真正的收益是**测试覆盖**：它此前只被 `--selftest-logsink` 覆盖，而**那个探针不在 `dotnet test` 链上**
⇒ "日志缓冲坏了"只有手动跑一次托盘自检才会暴露。搬进 Core 后补 **9 例**，逐条钉 `LogSink.cs` 里
**写明的**契约（不是从实现倒推的）：`Length` 单调累计、超上限丢**最早**且**至少留一行**、
`Read` 对滑出窗口的游标**夹取而不抛**、`Clear` 不重置 `Length`、4 写 1 读并发下账目守恒。

### 14.2 S5-2：`SvcStatus` / `SvcProbe` 进 Core

`TrayApp` 里有 3 个**无状态纯函数**（`SvcState`/`SvcDot`/`SvcEnable`）与 3 个**无状态进程探针**
（`PortUp`/`HealthUp`/`KillByPort`）。它们写的时候就是照"与 UI 解耦"设计的（`static`），
但因为是 `TrayApp` 的**私有**成员，只有 `--selftest-svcmenu` 能枚举到。

> **这是 S5 里性价比最高的一刀**：搬进 Core 后，菜单圆点色 / 状态文本 / 三个启停项的可用性
> 从"只能靠人跑自检"变成 **8 种组合 × 3 函数的穷举断言**，另加一条交叉约束 ——
> 三个映射必须对优先级一致（它们历史上分散在三处独立 `if` 里，正是漂移的来源）。

`Service.Spec` 的 10 个转发属性**未删**：它们服务于 `ServiceManager` 的进程编排面（S5-4），
现在删只会把改动摊进还没做的那半。

> **后续（§15）**：S5-4 的独立类型版**判定不做** ⇒ 这 10 个转发属性**暂无删除必要**；
> 真要删必须与独立类型版同批做（编译器会逐处报错 = 那份删除清单）。

### 14.3 验收（同机同配置差分；基线留档 `docs/temp/gov/s5-baseline/`）

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 错误 / 30 警告**（= S4 基线，无新增） |
| `dotnet test` | **127/127**（93 + `LogSink` 9 + `SvcStatus` 25）—— ⚠️ 这是**本节时点**的数字；S5-3/S5-4 交付后总数为 **193/193**（§15.3） |
| 11 项自检 + `--dump-menu` 逐字节差分 | **8 项 IDENTICAL**；3 项差异见下表 |
| 负向测试（把 `State` 的优先级改坏） | 如期 **3 红**，恰为优先级用例；还原后 sha256 = `672f2293…` ⇒ 断言不空转 |

**三处差异的定性（逐条，不是"看起来像"）**：

| 项 | 差异 | 定性 |
|---|---|---|
| `menu-dump.txt` | 1 行：`23小时前` → `1天前` | **时刻类**：相对时间标签在两次采样之间跨过 24h 阈值 |
| `selftest-logwin.txt` | 时间戳 `14:24:16` → `14:29:31`；env 列表**枚举顺序** | **时刻类 + 已知非确定性**：排序后集合 **50 = 50 完全相同**；剔除时间戳后**逐字节一致** |
| `selftest-perf.txt` | 仅 `ts` 字段 | **时刻类** |

### 14.4 本轮新增的两条易踩（留档）

1. **基线必须取在"干净检出"上，不能取在正在干活的主树**。主树当时有**另一会话**的未提交改动
   （`PluginCenter.cs` / `TrayApp.Dsh.cs` + 新增 `Core/NodeLocator.cs`）⇒ 拿主树当基线，
   会把他人在制品的差异算进我的差分里。worktree 从 git 对象检出，天然干净。
2. **worktree 里没有 `publish/`**（它在 `.gitignore` 里）⇒ 自检会拿**自动生成的默认配置模板**跑，
   产出一份"看着像回归"的假差异。必须在 worktree 里 `dotnet publish` 之后**把真配置只读拷进去**
   （`dsh-tray-config.json` 是手工维护、不进 git 的唯一源 —— **任何 `rm` 都不许带上它**）。

### 14.5 原定 S5-3 / S5-4（独立类型版）：经复核判定不做

| 步 | 内容 | 为什么放着 |
|---|---|---|
| **原定 S5-3** `MenuBuilders` | `TrayApp.cs` 约 150 行菜单构建 + `BuildSvcMenu`/`RefreshSvcMenu`/`SvcCfgLines`/`StatusTip`/`KeepOpen*` 搬成独立类型 | **风险最高、下游收益最低**：它解锁不了任何自动验证（Core 装不下 WinForms），验收只能靠真人回归。⚠️ 菜单控件是 `TrayApp` 的实例字段，被 `TrayApp.Dsh.cs` 的 `RefreshDshUi()` 读写 ⇒ 搬字段**必然碰那个文件**，而它当时正被另一会话修改 |
| **原定 S5-4** `ServiceManager` 编排面 | `Start`/`Stop`/`RestartSvc`/`RestartAll`/`StopAll`/`ReloadSvcConfig` 搬出 | 正确性**只能靠真实拉起 llama-server 验证**（`--selftest-*` 覆盖不到）⇒ 与 S5-3 同批做更划算 |

> **判断准则（沿用 §11.3）**：拆分的收益要看它**解锁了什么下游动作**。
> S5-1/S5-2 解锁的是"可自动验证"（进 `dotnet test` 链）；S5-3 不解锁任何自动验证，
> 只解锁"文件更短" —— 所以它排在最后，且需要一个真人同时在场的回归窗口。

> **发布状态**：S5-1/S5-2 是内部重构，**对外行为零变化**（自检差分可证）。发布链未变；
> `publish-next` 重建与否不影响功能。

---

## 15. S5-3 / S5-4 实施记录（2026-09-12 下午，S5 收口）

**一句话**：S5 里**能自动验证的部分到此全部交完**；剩下的"把菜单/编排面搬成独立**类型**"经复核
判定**不做** —— 这不是"没做完"，而是按 §11.3 的判据做完之后，剩下的那块**不该做**（§15.2 有实测代价）。

### 15.1 这一轮做了什么

沿用 S5-1/S5-2 的同一刀法：**可判定的纯逻辑进 Core，WinForms 装配留在原位**。

| 段 | 内容 | 位置 | 验证 |
|---|---|---|---|
| **S5-3** | 菜单**文本合成**：4 个档位标签（KV / 缓存内存 / MTP / 参数组）、文件名与宽度截断 `Mid`、时长 `Dur`、「运行时配置」6 行 `CfgLines`、状态悬浮 `StatusTip` | `src/QwenTray.Core/SvcLines.cs` | `SvcLinesTests` 47 例 |
| **S5-4** | 「重读配置」的**判定内核**：`Find`（Name 忽略大小写 → 退同 Port）+ `Apply`（就地写穿 + 差异描述） | `src/QwenTray.Core/SvcConfigDiff.cs` | `SvcConfigDiffTests` 13 例 |

调用侧（`TrayApp.Services.cs`）新增 `ToView(Service)`，把 `Service` + 当前托盘参数拍成 Core 的 `SvcView`
—— **这是 `Service` 与 Core 之间唯一的收口点**（Core 不能引用带 `Process` 句柄的 `Service`）。
`ReloadSvcConfig` 原先内联的三段（挑项 / 逐字段差异 / 拼接日志）收敛成两行调用。
`TrayApp.cs` 的 4 个档位标签留了**迁移期兼容层**转发（`TrayApp.KvLabel` → `SvcLines.KvLabel`），
与 `Service` 上那 10 个转发属性同一惯例：调用点零改动、JIT 内联。

> **顺手清掉一个哑参数**：`SvcCfgLines(Service svc, bool running, LaunchResult b)` 的 `b`
> **函数体从未读过**（首行自己算 `EffectiveGpus`，不碰 `b.args`）。**搬运是发现死参数的最好时机** ——
> 它躲过了 S1/S2/S3/S4 四轮体检，因为只有"把它搬到另一个工程"才会强迫你写清它的真实依赖。

### 15.2 为什么"独立类型"那块不做

S5 原定三块是 `ServiceManager` / `MenuBuilders` / `LogSink`。前两块要的是**独立类型**，实测代价：

| 待搬 | 需要动的东西 | 落点 | 解锁的自动验证 |
|---|---|---|---|
| `MenuBuilders` | `TrayApp` 的 ~50 个 `ToolStripMenuItem` 字段 + 构造函数里 ~145 行菜单构建 + `RefreshChecks`/`VramSplitUpdate`/`KeepOpen*`/`SetParam` 等 ~15 个方法，牵动 5 个 partial 文件约 50 处调用点（含 `TrayApp.Dsh.cs::RefreshDshUi`） | 只能留**主工程**（Core 装不下 WinForms，`CS0234`） | **零** |
| `ServiceManager` 进程编排面 | `Start`/`Stop`/`StopAll`/`RestartSvc`/`RestartAll`/`ReloadSvcConfig`；依赖 `Bg`/`Ui`/`Log`（`TrayApp.Dsh.cs` 里 **private**）与 `logForm` ⇒ 要么放宽可见性（**碰并发文件**），要么把 ~10 项能力做成委托参数传进去 | 主工程 | **零**（`--selftest-*` 覆盖不到，只能真实拉起 llama-server） |

按 §11.3 的判据（**拆分要看它解锁了什么下游动作**）：两块都只解锁"文件更短"。
而代价是动这个应用**最核心的对象**（菜单构建历史上出过"幽灵菜单/布局错乱"，正是 §2 记录的那类缺陷），
且必须由**真人过 §7 的回归清单**兜底。**收益 0、风险最高、还要占用一次人工回归窗口 ⇒ 不做。**

> 这一轮把 S5-3/S5-4 **改名落地**了：从"把菜单搬成类型"变成"把菜单里的**可判定部分**搬进 Core"。
> 拿到的东西更少（不缩 `TrayApp.cs`），但拿到的是**真的**（进 `dotnet test` 链）。

### 15.3 验收（同机同配置差分；基线 `docs/temp/gov/s5-baseline/`，本轮 `s5-3-after/` + 复跑 `s5-3-after2/`）

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release -t:Rebuild` | **0 错误 / 30 警告**（= S4 基线，无新增） |
| `dotnet test` | **193/193**（133 + `SvcLines` 47 + `SvcConfigDiff` 13） |
| 11 项自检 + `--dump-menu` 与基线逐字节差分 | **8 项 IDENTICAL**；3 项差异见下表 |
| 复跑可复现性（重建产物再采一遍） | 与上一轮 **9/11 逐字节一致**（差的两项 = 时间戳） |
| 负向测试 | ① 摘掉 `Apply` 的 `Batch>0` 守卫 ⇒ `Apply_ZeroBatchUbatch_IsNotApplied` 如期红；② `Mid` 宽度算错 1 ⇒ 两条宽度契约如期红。合计 **3 红 / 190 通过**，还原后与 HEAD 内容一致 |

**三处差异的定性（逐条）**：

| 项 | 差异 | 定性 |
|---|---|---|
| `menu-dump.txt` | 仅"相对时间"（`18小时前`→`19小时前` / `23小时前`→`1天前`） | **时刻类**。归一化后**逐字节一致** ⇒ **菜单可见文本零变化**（本轮最想证明的一条） |
| `selftest-logwin.txt` | 时间戳；env 清洗清单**多 1 个 token** | **时刻类 + 环境类**：多出的是 `CODEBUDDY_CURRENT_MODEL_ID`（WorkBuddy 本会话注入的环境变量）；剔掉时间戳后其余逐字节一致 |
| `selftest-perf.txt` | 仅 `ts` | **时刻类**：`cfg 62d566325a` 与 `med/min/max/n/tok` 全等 |

### 15.4 顺带发现的既有缺陷（**未修**，已用断言钉住）

多卡时「运行时配置」第 2 行渲染成 **`GPU0+1`**，而一级菜单 GPU 项走 `GpuSelection.ShortLabel()`
渲染成 **`GPU0+GPU1`** —— 同一个事实两种写法。

- 成因：`SvcLines.CfgLines` 里 `eff` 是 `List<int>`，`string.Join("+", eff)` 直接得到 `"0+1"`。
  **搬运前就是这个行为，非本轮引入**。
- 为什么不当场修：这是**用户可见文本**的变更。S5 全程的规矩是"纯搬运不改可见输出"，
  改了就不再能用"菜单可见文本零变化"这条证据说话。要修必须单独决定 + 单独走一次托盘人工回归。
- 已加 `SvcLinesTests.CfgLines_MultiGpu_LayerVsTensor` 钉住现状（注释写明"要改就两边一起改"），
  避免它某天被"顺手修好"而无人复核。

### 15.5 两条与本轮验收装置有关的观察（都与 §14.4 同族）

1. **`sha256 -c` 对刚 `git checkout` 回来的文件会假红**：本仓库 `core.autocrlf=true`，
   `Write` 工具写出的文件是 LF、`git checkout` 还原出的是 CRLF ⇒ 逐字节不同但 `git status` 为空。
   校验"是否真还原"应看 `git status` / `git diff`，或**按行尾归一后再比**
   （`sed 's/\r$//' f | sha256sum` vs `git show HEAD:f | sha256sum`）。
2. **`publish-next\` 里会出现一份自动生成的默认配置模板** `dsh-tray-config.json`（内容是
   `<35B-model>.gguf` 那套占位符）。来源是 `apply-dsh-tray.cmd` 的健康哨（`--selftest-exit` /
   `--selftest-svcmenu`）在那个目录跑过托盘，`Config.Load()` 找不到配置就写一份默认的。
   **绝不能进 `publish\`**（发布脚本用 `robocopy /XF` 排除，线上安全）；`rm -rf publish-next` 重建是根除办法。
   实测：`publish-next` 那份（14:18、1461 B）与 `publish` 真配置（13:04、1743 B、md5 `3337cb80…`）确实不同 ——
   **判据也比对 `publish\*.dll` 的 mtime + 大小，别用目录 mtime**（WebView2 userdata 写入会改它）。

> **发布状态**：S5-3/S5-4 与 S5-1/S5-2 同类 —— **对外行为零变化**（自检差分可证，菜单文本逐字节可查）。
> `publish-next` 已按本轮内容重建（`DSHTray.dll` 284,160 → **280,576**、`QwenTray.Core.dll` 70,144 → **75,264**，
> 体积变化 = 代码**搬走**而非复制两份）。发不发不影响功能，等有需要时由真人触发 `apply-dsh-tray.cmd`。
