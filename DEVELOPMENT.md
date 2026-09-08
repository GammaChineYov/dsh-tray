# DSH托盘 开发契约与策略（DEVELOPMENT CONTRACT）

> 本文件是修改本仓库（Windows 托盘应用）时必须遵守的**契约**。
> 违反下面任何一条都曾造成线上回归（右键卡顿、双托盘、假温度、卡死），改代码前先读。

---

## 1. UI 线程瘦身（最高优先级）

托盘右键菜单的即时响应是**本产品生命线**。任何改动不得让 UI 线程做慢事。

- 禁止在 `Tick()`（1s Timer 回调，跑在 UI 线程）里做任何**可能阻塞**的调用：
  同步网络连接、netstat、WMI 查询、Process.Start 等待、文件大 IO、Thread.Sleep。
- 周期探测的正确姿势：Task.Run 后台执行 → 结果写入 volatile 缓存 →
  `Tick()` 只**读缓存**做状态机与轻量 UI 更新。参考 DSH 端口探测
  （`dshPortUp` + `_probeBusy` Interlocked 防重入，每秒最多一次）。
- 网络探测一律带超时：`TcpClient.ConnectAsync(...).Wait(400)`，禁止裸 `Connect()`。
- 菜单里任何慢动作（启动/重启/停止 DSH 等）回调体包一层 `Bg(()=>…)`（后台执行）；
  需要碰 UI 的收尾用 `Ui(()=>…)`（经 logForm.BeginInvoke 回 UI 线程）。
  **禁止**从后台线程直接改菜单项/弹 MessageBox。
- NotifyIcon.Text（tooltip）有限频（≥3s + 内容变化 + 菜单未开才写）——不要动这个逻辑。

**回归检查**：改完先想"菜单点一下，UI 线程上会发生什么"。任何不确定 → 后台化。

## 2. 单实例与自启（防双托盘）

- 进程级单实例互斥锁已内建：Local\DSH托盘_SingleInstance，后到实例静默退出。
- **新增诊断模式（如 --dump-menu / --selftest-logwin）必须跳过互斥检查**，否则跑不起来。
- 开机自启：启动文件夹只允许**一个**指向 DSHTray.exe 的快捷方式。
  改名/改入口后务必删除旧名残留（曾因 QwenLlamaTray.lnk 残留导致开机双托盘）。
- 托盘菜单「开机自启动」= Startup 文件夹 .lnk（AutoStart.cs），不要引入第二套机制。

## 3. 菜单结构（改动后用 --dump-menu 验证）

当前层级（2026-09 基线）：

```
DSH (状态圆点: ●绿运行/●黄启动中/●红未启动)   ← 顶层第一项
 ├─ 状态：…（禁用信息行）
 ├─ 启动 DSH / 重启 DSH / 停止 DSH
 ├─ 查看 DSH 日志（专用跟踪窗口）
 ├─ 打开 DSH 程序目录 / 打开 .dsh 目录
── 分隔 ──
打开 DSH 会话（每模型一条，按运行态显隐）/ 打开官方会话 chat
── 分隔 ──
启动 <模型>（每模型一条） | 停止全部 | 重启全部
── 分隔 ──
推理参数组：<当前组>（单选，标题带当前值）
GPU: <选择>（复选） | 上下文: <N>K（单选，标题带当前值） | KV 缓存：<当前>（单选） | 缓存内存：<当前>（单选；--cache-ram MiB；0=禁用/-1=无限制；cfg 键 cacheRam=） | 切分模式：<当前>（单选） | MTP: <档位>（单选；无/MTP/MTP2/MTP3/MTP4 → --spec-type draft-mtp --spec-draft-n-max N；重启对应服务后生效；cfg 键 mtpLevel=）
模型监听 0.0.0.0（局域网可访问）（复选；勾选=--host 0.0.0.0 默认，取消=--host 127.0.0.1 仅本机；下次启动生效；cfg 键 bind=）
── 分隔 ──
查看日志（服务日志窗口） | 打开配置文件
开机自启动 | 退出
```

- 规则：**目录/环境类动作放 DSH 二级菜单**（打开 DSH 程序目录、打开 .dsh 目录、查看 DSH 日志）；
  顶层只放高频操作与全局配置。
- 「推理参数组/切分模式/上下文/KV缓存/缓存内存/GPU」标题实时显示当前选中值（RefreshChecks() 统一刷新）。
- 模型监听复选项默认勾选（0.0.0.0），兼容早期硬编码行为；取消勾选才收紧为 127.0.0.1，避免默认回归。
- 不要加含义模糊的项（曾因「停止所选服务」语义不明被移除）。
- 验证：publish\DSHTray.exe --dump-menu → menu-dump.txt 核对结构与勾选/禁用态。
- **切分模式滑块旁的显存估算**：张量并行时显示「GPU0≈X.XGB  GPU1≈Y.YGB（<模型> <ctx>K 估算）」——参考模型(运行中优先)GGUF 大小 + KV(ctx 近似) + 每卡计算缓冲(≈1.1GB) 按 `-ts` 比例分摊；仅供预览，实际以 llama-server 加载报告为准（`VramSplitPreview()`）。
- **打开 DSH 会话自动校准模型配置**：点「打开 DSH 会话（<模型>）」时，先 `EnsureLlamaProvider` 在 settings.yaml 的 `llm-pi-ai.providers.<provider>` 下校准 **baseURL 端口 = 托盘服务 port**、**首个模型 id = 托盘服务 Model**（只改对应 provider 块，其它 provider/顶层键不动；provider 块缺失则整块补插），再临时改 `agent-default-model` 指向该模型（**30s 后还原**）。用于「端口变了/模型名变了」后 DSH 会话仍指向正确模型。
  - **新建会话的关键**：dsh 0.1.3+ 会在浏览器 localStorage 持久「上次会话」(`dsh.sessions.current` / `dsh.workspace.view.v5`)，打开 base URL 会恢复上次会话而非新建；托盘在弹窗 WebView2 用 `AddScriptToExecuteOnDocumentCreatedAsync` 在每次文档创建时清掉这两个键（不动 cookie），使「打开 DSH 会话」= 新建会话并应用 agent-default-model（本地模型）。

## 4. 配置与隐私（公共仓库安全）

- 本仓库 GitHub **PUBLIC**。机器真实路径（node.exe、harness checkout、.dsh、模型路径等）
  只允许出现在 **gitignored** 的 dsh-tray-config.json（exe 同目录）里。
- 代码默认值（Config.Default()）一律用占位符（C:\models\<xx>.gguf、<you> 等），
  禁止把真实路径写进 *.cs / README.md / 本文档。
- gitignore 已覆盖：bin/ obj/ publish/ publish-new/ *.cfg dsh-tray-config.json webview-userdata/。
- 新增大件/凭据/本地状态文件 → 先加进 .gitignore。

## 5. 构建 / 发布 / 重启

```powershell
# 托盘在跑会锁 publish\DSHTray.dll → 先停再发布
Stop-Process -Name DSHTray -Force
dotnet publish -c Release -o publish
Start-Process -FilePath .\publish\DSHTray.exe -WorkingDirectory .\publish
```
- 只改源码不重发 = 线上不生效；发布目录被锁 = 先停托盘。
- 发布前如有 llama 服务在跑，先经托盘「停止全部」再重启托盘（避免孤儿管道）。
- 本机验证出口：--dump-menu（结构）、--selftest-logwin（DSH 日志窗口）、单实例双启=1 进程。

## 6. 数据源诚实策略（温度/数值）

- **不显示假数据**。主板 ACPI 恒温（如 \_TZ.TZ00 恒 30.1°C、与负载无关）视为假值 → 隐藏。
- 真实 CPU 温度来源策略：
  - HWiNFO64 共享内存（Global\HWiNFO_SENS_SM2，v8 布局）——真实但**免费版单次运行 12h 后失效**，
    若采用该路线须配套每 ~11h 自动重启任务。
  - LibreHardwareMonitor —— 开源无限制，但读 CPU 需加载内核驱动 → 宿主需管理员/提权运行。
  - 无真实源时：温度字段留空（tooltip 只显示 CPU% 与内存%），不要回退编数值。
- GPU 温度来自 nvidia-smi（真实）。

## 7. 提交与推送纪律

- 提交信息：中文、一句讲清**改了什么 + 为什么**（防回归上下文）。
- 推送前自检：git status 干净（无本机路径/临时文件/日志进库）；构建 0 error。
- 推送网络：本机环境多变（公网/代理），失败先看报错再换通道，不要把代理 env 永久写进脚本。
- 大改动分主题提交（如：菜单功能 / 修复卡顿 / 文档），方便回滚定位。

## 8. 其它踩坑速记

- LogForm 关闭 = 隐藏不销毁（FormClosing → Cancel+Hide），Append 需防 IsDisposed。
- 自引用 lambda（本地变量引用自身）会 CS0841/CS0165 → 改用字段。
- 中文/路径编码：脚本与配置 UTF-8 无 BOM；含引号命令交给脚本文件执行，避免壳转义。
- 诊断模式新 flag 记得同时支持「进程已在跑」场景（不占锁/不弹窗）。

## 9. 菜单动态内容模式（加任何动态子菜单前必读，防竞态/阻塞复发）

本仓库因"给子菜单加动态内容"多次踩到两类回归：**右键菜单静默不弹**、**右键延迟**。
根因都是把「改菜单结构 / 磁盘 IO」挂进了 ContextMenuStrip 生命周期（Opening/Closed/显示中）。
WinForms 下拉显示是模态且状态敏感——显示期间对 DropDownItems 做 RemoveAt/Insert，会**静默放弃显示/卡内部消息循环**，无异常、进程看似活着。

**唯一允许的动态子菜单模式（如「最近会话」）**：
1. **数据采集永远后台**（Task.Run 或 Tick 低频），产出写入 volatile/不可变快照字段（如 `recentList`）；采集函数**禁止**在 UI 线程调用（dumpMode 诊断除外）。
2. **结构重建只有唯一入口**（如 `RebuildRecentMenu()`），且必须同时满足：快照已就绪、`menu.Visible==false`（显示中直接 return，绝不硬改）；重建**只读内存快照**，禁止任何磁盘/网络/解析。
3. **触发只允许三个点**：采集完成回调（经 Ui 回 UI 线程）、`menu.Closed`（此时菜单必不可见，仅做内存快照重建，毫秒级）、低频 Tick。**严禁**在 `menu.Opening` / 菜单显示期间触发采集或重建。
4. 改动态子菜单时先看是否符合 1-3；不符合就重构到符合，不要图省事在事件里塞动作。

**右键弹出与 DPI**：
- 右键菜单走 `icon.ContextMenuStrip=menu`（NotifyIcon 内置弹出，位置由系统给）。
- 进程必须 `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)`（Main 首行）：高分屏（2560@150%）下若 DPI-unaware，`menu.Show(Cursor.Position)` 坐标被二次缩放 → 菜单落到 (0,0)/越界不可见（曾误判"右键不弹"）。

## 10. 性能与复杂度契约（改动前先算账，2026-09-07 新增）

右键菜单即时响应是本产品生命线。**每次改动前先评估「每个用户操作的复杂度与 IO」**，并对照下列红线：

- **右键/菜单链路红线**：`ContextMenuStrip` 打开、Opening/Closed/任意菜单事件回调里，只允许 O(菜单项数) 的内存操作；**禁止任何磁盘/网络/JSON 解析/大循环/同步等待**。本仓库已因「每次关菜单都全量扫 E:\dsh\sessions（393 目录）+ 读 1.2MB 投影」导致右键延迟数秒。
- **数据采集红线**：磁盘/网络/解析一律后台线程 + volatile 不可变快照，UI 线程只消费快照。采集频率需算总成本 = 频次 × 每次 IO/CPU；对跨目录/大文件不要高频全扫。
- **重复解析必须缓存**：同一磁盘文件（如 workspace.json / session_projcache.json，1.2MB）在同一 mtime 下只解析一次，缓存复用。
- **外部数据源依赖**：会话/内容类数据若宿主（dsh）不实时落盘（本机实测 projcache 停更、session 日志仅空壳、消息全在 host 内存），磁盘自治方案**无法反映实时会话**——不要假装能，明确标注限制或改走宿主接口。
- **自查清单（改动后必做）**：启动后立即右键、连续右键 5 次、打开→关闭→再开菜单，都应毫秒级响应、无不弹出/无延迟。
- **通知区冷启动右键（必要对策，勿删）**：重启托盘后，explorer 可能长时间不建立图标右键路由 → 右键无反应/延迟。2026-09-07 A/B 证实必须保留「启动 ~2.5s 后 `icon.Visible=false→true` 重注册一次」（触发 NIM_DELETE/ADD 强制绑定），否则重启后右键要等很久。若你认为可移除，请先做同款 A/B（去掉→重启→立即右键）确认不再复发。
- **右键不弹 = 幽灵菜单（2026-09-08 已加防御）**：探针显示"右键不弹"时 `MouseUp` 读到 `menu.Visible` 已为 True——存在 `Visible=true` 但用户看不见的菜单残留（曾被弹到不可见位置且未关），NotifyIcon 因 `Visible` 不再重弹。已加防御：`MouseUp` 里若 `menu.Visible` 且 Bounds 不在任何工作屏 → `menu.Close()` 清掉（命中才写 GHOSTCLR 日志）。若仍偶发不弹，先看 GHOSTCLR 是否命中。
- **重启后首次右键被 explorer 消费（已知现象，非回归）**：2026-09-08 实测——重启托盘后，**第一次右键消息被 explorer 用于建立图标路由而消费掉（日志无 RUP/无 GHOSTCLR），第二次右键起正常**。这是 Windows 通知区对新注册图标的行为；重注册已尽量缩短窗口。验收/排查右键问题时请右键 ≥2 次，勿把首次丢弃误判为回归。
