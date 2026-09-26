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

- 进程级单实例互斥锁已内建：Local\DSH托盘_SingleInstance，后到实例**不产生第二托盘**（结果仍是单托盘）。
- **图标自愈（2026-09-22）**：后到实例不再无条件静默退出，改为两级处置 ——
  ① 先发 `Local\DSH托盘_Resurrect` 请主实例重挂通知区图标；主实例**在 UI 线程**挂好后回执
     `Local\DSH托盘_ResurrectAck`（1.5s 内收到 ⇒ 活托盘，后到实例静默退出）。
  ② 收不到回执（无监听者 = 旧构建残留 / UI 线程僵死）⇒ `KillStaleTrays()` 清同名僵尸后本实例接管，
     保证「双击必有图标」。仍抢不到锁 ⇒ 按契约放弃。
  起因：托盘进程曾**活着但图标被通知区丢弃**（无头托盘），而旧逻辑让用户再点一次也静默无反应 ⇒ 只能 taskkill。
  ⚠️ 改动此项必须同步保持：新信号一律**不占锁**、不在 UI 线程外碰 `icon`/菜单；`--selftest-exit`
  须输出 `resurrect ... listener=` 行（也是 apply 脚本判定产物为新版的锚点）。
- **新增诊断模式（如 --dump-menu / --selftest-logwin）必须跳过互斥检查**，否则跑不起来。
- 开机自启：启动文件夹只允许**一个**指向 DSHTray.exe 的快捷方式。
  改名/改入口后务必删除旧名残留（曾因 QwenLlamaTray.lnk 残留导致开机双托盘）。
- 托盘菜单「开机自启动」= Startup 文件夹 .lnk（AutoStart.cs），不要引入第二套机制。

## 3. 菜单结构（改动后用 --dump-menu 验证）

当前层级（2026-09 基线）：

```
DSH (状态圆点: ●绿运行/●黄启动中/●红未启动)   ← 顶层第一项；未启动(红)时**直接点本项 = 快捷启动 DSH**（§11.9）
 ├─ 状态：…（禁用信息行）
 ├─ 启动 DSH / 重启 DSH / 停止 DSH
 ├─ 查看 DSH 日志（专用跟踪窗口）
 ├─ 检查/清除孤儿启动锁（node_modules.lock）        ← 手动复查；启动时已自动处理（§11.6）
 ├─ 打开 DSH 程序目录 / 打开 .dsh 目录
── 分隔 ──
最近会话（子项带会话状态圆点；图例见该项 ToolTipText）
   ●绿=执行中 ●黄=阻塞·询问 ●橙=阻塞·权限 ●蓝=已完成·未读 ●灰=已完成(已读)
   ●红=异常中断(非手动终止/错误/崩溃) ●浅=空/未收尾 ●透明=状态未知(DSH 未运行或插件未装)
打开官方会话 chat
── 分隔 ──
<状态圆点> <模型名> (<端口>)   ← **叶子项，没有二级菜单**（2026-09-26 改造，§11.10）
   状态圆点：●黄=启动中 ●绿=运行中 ●橙=运行中(未托管，端口上有 llama 但托盘未接管) ●红=未运行
   **点本项 = 选中该模型**（一级菜单内互斥单选；选择写进 dsh-tray.cfg 的 selectedModel=，下次启动托盘仍是它）
   ⇒ 下面「当前模型」那一组 + 全部参数面板都跟随它；本行因此是**①类**（点完不收起，好接着调参数）
── 分隔 ──
当前模型：<名> (<端口>) · 本模型覆盖 N 项（禁用标题；N>0 = 该模型在 dsh-tray-config.json 里单有几项参数）
状态：…（禁用行；悬浮给全量：pid / 启动时刻 / 已运行时长 / 内存 / 端口 / 服务端实测）
启动模型 / 重启模型 / 停止模型   ← 作用于**上面选中的那个**模型；三项**点击后不收起菜单**（②类）
启动 DSH 会话（官方 Web UI；先校准 settings.yaml 再打开，§4 同旧「打开 DSH 会话」逻辑；
              文案随「内置渲染」开关加「（内置渲染）」后缀）
启动内置对话（ThinChatForm 瘦客户端；恒走内置渲染，不受开关影响）
运行时配置（禁用；悬浮给 6 行明细 + 生效环境 + 完整命令行。**2026-09-26 语义终稿（用户三修正后）**：面板各下拉标题显示的就是选中模型的生效值 ⇒ 本行**不复述任何参数值**（差异值清单与标题同时可见=重复，用户圈注打回）；只报选项里看不出的信息：自定义启动命令（ArgsCustom）恒显示，其余整行隐藏；差几项的计数只在「当前模型」头部（值清单在其 ToolTip 按需看）。另修「显示慢一拍」：PersistParam 同步 Spec 后补 RefreshChecks（此前 setter 的刷新跑在同步前，面板显示的是上一次点击的值））
 ├─   环境配置
 │     ├─ CUDA_VISIBLE_DEVICES = …（-ts 多卡时=选中的 GPU 列表；CPU/单卡时=未设置）
 │     └─ GGML_CUDA_ALLREDUCE = internal（多卡张量并行时；否则未设置）
 └─   llama.cpp 配置（悬浮=完整命令行）
       模型 = … · 无 mmproj / mmproj = …
       GPU0+1 · -ngl 99 · --split-mode tensor -ts 50,50
       上下文 = 192K · KV = … · 批 = B/UB · 缓存内存 = …
       MTP = … · 参数组 = … · flash-attn = …
       监听 = 0.0.0.0:8081 · reasoning = deepseek · jinja
       实测 = ctx …(服务端 /props) · 视觉 …（未运行时提示「启动后自动探测」）
内存与缓存（二级菜单：立即回收 / 就绪后自动回收一次 / 空闲自动回收（6 档间隔）/ 硬盘 KV 缓存开关 / 路径与状态行；§13）
停止全部 | 重启全部（重启全部 = 逐个「重启模型」，同样重读配置、不碰 DSH）
── 分隔 ──
推理参数组：<当前组>（单选，标题带当前值）
GPU: <选择>（复选） | 上下文: <N>K（单选，标题带当前值） | KV 缓存：<当前>（单选） | 缓存内存：<当前>（单选；--cache-ram MiB；0=禁用/-1=无限制；cfg 键 cacheRam=） | 切分模式：<当前>（单选） | MTP: <档位>（单选；无/MTP/MTP2…MTP8 共 9 档（2026-09-26 6→9 档，用户令）→ --spec-type draft-mtp --spec-draft-n-max N；重启对应模型后生效；cfg 键 mtpLevel=）
⚠️ 上面这一整排（含 MTP）改的是**当前选中模型的持久化配置**（写回 dsh-tray-config.json 的服务项），
   不再是"启动时覆盖所有模型"；托盘全局默认（dsh-tray.cfg）退居"未覆盖模型用的基线"。解析规则见 §11.10。
模型监听 0.0.0.0（局域网可访问）（复选；勾选=--host 0.0.0.0 默认，取消=--host 127.0.0.1 仅本机；下次启动生效；cfg 键 bind=）
── 分隔 ──
查看日志（服务日志窗口） | 打开配置文件
开机自启动 | 重启托盘 | 退出 | 退出（同时停止模型服务）
```

- **两个「退出」的区别（2026-09-11 语义反转）**：`退出` = `ExitApp(false)` = **默认**，只注销图标退托盘，
  llama-server 继续服务，下次启动托盘经 `Adopt()` 自动接管（§11.7）；`退出（同时停止模型服务）` = `ExitApp(true)`
  → `StopAll()`（停掉本托盘启动/接管的模型，释放显存），等价 CLI `DSHTray.exe --exit --stopall`。
  反转理由：退出托盘是常态、停模型是例外，**默认不应有破坏性**（停模型要重载 30-60s）。
  `重启托盘` 也已改为**保留模型**（`ExitApp(false)`；新实例起来后会 `Adopt()` 接管端口上已跑的模型）。

- 规则：**目录/环境类动作放 DSH 二级菜单**（打开 DSH 程序目录、打开 .dsh 目录、查看 DSH 日志）；
  顶层只放高频操作与全局配置。
- **「最近会话」子项状态圆点（2026-09 新增）**：会话实时状态（执行中/阻塞·询问/阻塞·权限/已完成/异常中断）
  **只存在于 dsh host 内存与事件流**（磁盘 projcache 投影无会话级状态、会话消息不落盘）。托盘经
  **dsh host 内插件 dshtray-status**（~/.dsh/dev-plugins/dshtray-status，`GET /dshtray-status/api`，
  进程内读 sessionQuery.observeSession + agents）轮询权威信号；plugin token 存 ~/.dsh/dshtray-status.token。
  「已完成·未读/已读」由**托盘本地 sidecar**（~/.dsh/dshtray-read.json，cfg 键 DshtrayTokenPath/RecentReadPath
  可改路径）记录“托盘弹窗点开过的会话 id”推导——与 dsh web 自身的“上次访问 seq”是两套来源，不假装同步。
  轮询在 ScheduleRecentRefresh 的后台任务里（60s 低频 + dshPortUp 守卫）；DSH 未运行/插件未装/超时 → 全部
  静默降级为透明占位（unknown），绝不显示编造状态。
- **「最近会话」只含有内容的会话（2026-09 修订）**：`sessionListMetadata.val.blank=true` 的空会话
  （创建后从未发过用户消息，无标题无 lastPromptAt）**不进列表**——否则排序用 `Max(lastPromptAt,createdAt)`
  会让新建空会话靠创建时间顶到列表前部，出现成片「(无标题 …)」。
  判据：`ReadProjRecord` 解析 blank → `ReadRecent` 过滤（`if(c.blank) continue;`）。
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
- **先退出托盘**（默认就保留模型，版本 ≥ 2026-09-11 的 `--exit` 即为此意）：
  `publish\DSHTray.exe --exit`（或托盘菜单「退出」/ 桌面 `dsh-tray-exit-keep-model.cmd`），模型服务不会被打断。
  想把模型一起停掉才用 `--exit --stopall`（桌面 `dsh-tray-exit-stop-model.cmd`）。
  ⚠️ 版本 ≥ 2026-09-10b 且 < 2026-09-11 的构建里，`--exit` 旧语义是**停模型**，那时保留模型要 `--exit --nostop`
  （该别名现在仍被接受，但已等价默认）；再旧的版本没有这个开关，只能 `Stop-Process -Force`。
- 本机验证出口：--dump-menu（结构）、--selftest-svcmenu（一级模型行 +「当前模型」组：状态映射矩阵 + 运行时配置渲染 + 一级旧入口残留 + 叶子项判定，§11.10）、--selftest-logwin（DSH 日志窗口）、--selftest-lock（孤儿写锁判定，§11.6）、--selftest-exit（退出信号监听者探测，§11.7）、--selftest-uithread（UI 调度锚点「排队语义」断言）、--selftest-logsink（有界日志缓冲）、单实例双启=1 进程。

**上线脚本 `apply-dsh-tray.cmd`（桌面）现在用优雅退出而非强杀**（2026-09-11）：先 `publish\DSHTray.exe --exit --nostop`（`--nostop` 在新旧构建里都表示「保留模型」→ 跨版本安全）等它自己退，最多等 12s，超时才 `taskkill /F` 兜底。好处：走 `ExitApp` → 图标正常注销（不留死图标槽）、模型完全不被碰。

**不想打断模型服务时的发布流程（2026-09-10 新增，已验证）**：直接覆盖发布要先把托盘弄掉，而版本 < 2026-09-10b 的托盘只有 `ExitApp → StopAll` 这一条退出路径（会停模型）、`Stop-Process -Force` 又会留下孤儿模型；若模型正在用、不想重载，走「暂存 + 切换」：
```powershell
# 1) 托盘继续跑也能构建：发到独立暂存目录（不能发进正在跑的 publish/，dll 被锁）
dotnet publish -c Release -o publish-next
# 2) 双击桌面 apply-dsh-tray.cmd
#    UAC 自提权 → 校验源构建含 --selftest-exit（防陈旧产物）→ taskkill /F 全部 DSHTray
#    → robocopy publish-next → publish → 启新托盘
```
- `taskkill /F` **不走** `ExitApp`，所以模型进程活着；新托盘启动时 `Adopt()`（`netstat -ano` 找该端口 LISTENING 的 pid，进程名含 `llama` 即 `svc.proc=pr`）**会自动接管**→ 菜单里可见、可停、可重启，无需重载。
- 接管的前提是权限：托盘若**未提权**而 llama-server 是提权的（反之亦然），`Process.GetProcessById` 抛访问拒绝被 `catch` 吞掉 → 接管失败。此时才需要桌面 `kill-llama-orphan.cmd` 收回，再从托盘启动。
- ⚠️ **绝不要让暂存产物覆盖本地运行态文件**：任何目录里跑过一次托盘都会生成默认模板 `dsh-tray-config.json`（默认值 + 本地 llama 路径，含 `<you>` 占位），拷进 publish 会**抹掉用户真实配置**。切换脚本已 `/XF dsh-tray-config.json dsh-tray.cfg *.log *.txt` + `/XD webview-userdata` 排除。
- ⚠️ **发布必须复核产物真的是新的**（2026-09-10 踩坑）：暂存目录若被上一个残留 DSHTray 进程锁着，`dotnet publish` 会失败而**旧 exe 时间戳不变**；只看 "0 个错误" 会误判。体检方式：`DSHTray.exe --selftest-exit` 必须产出 `selftest-exit.txt`（新开关），或按 UTF-16 检索 DLL 里的新字符串（`.NET` 字符串常量是 UTF-16，`grep` ASCII 查不到，别被误导）。apply 脚本已内置这道自检。
- `publish-staged/` `publish-next/` 已进 .gitignore。
- **WorkBuddy 侧无法强杀提权托盘**：PowerShell 工具对**提权**的桌面会话进程 `Stop-Process` 返回「拒绝访问」（令牌权限不足；普通权限的子进程可以杀），`cmd.exe` 又被安全策略整体拦截 → 这类"杀用户进程"的动作只能落到用户双击的 .cmd 里（apply 脚本因此带 UAC 自提权）。

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

## 11. 内置渲染 / 安全模式 / 插件管理（2026-09-09 新增）

背景：dsh 官方 Web UI 的客户端 bundle 由 loader entry 组装（本机 40+ client 模块），任一插件（尤其 super-injector 注入的 dev 插件）client.js 导入失败 → **整树白屏、无法续会话**（实测 `@dsh-external/dsh-topic-relay`）。但后端 cordis 与 `~/.dsh/sessions/**` 数据完好，3080 JSON-RPC 不受客户端 bundle 影响。三项功能因此而生：

### 11.1 托盘内置渲染（ThinChatForm.cs）
- WebView2 承载**内嵌单文件 HTML**（`Page.Html`，无 CDN 依赖），JS 经 `chrome.webview.postMessage` 桥到 C#，C# 用 `DshRpc` 打 `http://127.0.0.1:3080/api/<method>`——**绕开 CORS 也绕开官方 bundle**。
- **认证（DshAuth.cs）**：读 `~/.dsh/.credentials.yaml` 的 `client-connection/browser-session` secret，自签 cookie——名 `dsh-auth-<b64u(sha256(authority))>`，值 `v1.<b64u(JSON{version,authority,issuedAt,expiresAt})>.<b64u(HMAC-SHA256(secret, body))>`。与每次启动变化的 `?token=` 无关，30 天有效、重启保留。契约见 dsh 源码 `packages/client/connection/src/browser-auth.ts`。
- **RPC 契约（实测 0.1.0-rc 新版）**：斜杠方法名（`session/list` 不是 `session.list`）；信封 `payload:{args:{...}}`；`session/list` 参数 wire 名是 **`_request`**，其余（page/prompt/cancel/create/fork/search）是 `request`；历史用 `session/page {address:{kind:'session',sessionId}, throughSeq, maxMessages}`——throughSeq 未知时先传 `9007199254740991`，错误消息含 `past cursor N` → 用 N 重试；`session/follow` 是 stream Remote，普通 POST 拒绝（"must be opened through the stream carrier"），瘦客户端用 2.5s 轮询 page 代替。
- 事件渲染：user/message、assistant/message（reasoning 折叠、text 走极简 markdown）、tool/call+result（折叠截断）、turn/end（分隔线）；其余事件类型跳过。
- P0 边界：approval 待批会话会卡住（提示回官方 UI）；附件/plan 模式不实现。
- **WinForms 同步上下文死锁（自检踩过）**：TrayApp 构造建 LogForm 后主线程装上 WindowsFormsSynchronizationContext，之后任何 `xxxAsync().GetResult()` 都会死锁（续体排进不泵消息的主线程）。自检/同步等待一律 `Task.Run(()=>...).GetAwaiter().GetResult()`；正常功能全走 async void + 消息循环，不受影响。

### 11.2 安全模式启动（PluginCenter.cs + DshStart 注入）
- 机制 = dsh 原生 `--patch <file>` 全局 overlay（`dsh --help` 证实，应用于 profile 层之后）。托盘每次启动/重启 DSH 前重建 `dsh-tray-safemode.patch.yml`（exe 同目录），内容 `- id: X / disabled: true`。**零侵入**：不碰 cordis.patch.yml/package.json，取消勾选重启即还原。
- **参数顺序坑**：`bin.js --patch X web` 会被拒（"web takes none of parent --profile/--patch..."）→ 带 patch 必须用 `bin.js --profile web --patch X` 形式（web 是 --profile web 的别名）。
- 白名单 = `@deepseek-ai/dsh-base` + `@deepseek-ai/dsh-web-app` 两个 bundle 贡献的全部 entry + 用户 cordis.patch.yml 层里 name 以 `@deepseek-ai/` 开头的条目（如 tool-session-query）。⚠️ 不能按 entry 的包名判官方：`@tt-a1i/archify-dsh` 贡献的 entry 包名是 `@deepseek-ai/dsh-skill-filesystem`，按 bundle 归属判才正确。
- entry id 获取：优先 `bin.js --profile web --dump-config` 合成树（分节头 `# == <bundle>`，entry 级 `- id:`/`disabled:`，~8s）；dump 失败兜底读**每个 bundle 包目录自带的 cordis.patch.yml**（`profiles/web/node_modules/<bundle>/cordis.patch.yml`，link: 依赖是 junction 可直接读）——**不要凭包名推 id**（作者自定义，如 `dsh-better-sidebar→better-sidebar`、`@dsh-external/workflow→dsh-external-workflow` 无统一规则）。id 猜错无害：patch 仅告警 `entry not found`，不崩。
- 已知限制：entry 的 bundle 归属按 dump 分节头「首次出现」记，patched-by 叠加显示可能错挂（如 restart-service 错挂 dsh-ssh-tunnel）——本机 160 entry 分类全对，但理论上可能误判；安全模式禁用错了表现为该插件仍在/误禁，用 --dump-config --patch 复核。

### 11.3 插件管理弹窗（PluginManagerForm.cs）
- 四源清单：package.json bundles（官方/外部）、dump-config 合成树（权威 entry + disabled 态）、cordis.patch.yml 用户层、**super-injector 注册表 `~/.dsh/super-injector/registry.json`**（运行时注入的 dev 插件，dump 看不到！dsh-topic-relay 白屏元凶就在此列）。
- dev 插件启停 = registry 手术：禁用项移入 `registry.tray-disabled.json`（启用移回；首次手术自动备份 `registry.json.bak-tray`）。注入器重启按 registry 重注入 → 移出即禁用。
- 「应用并重启 DSH」走托盘 DshRestart（进程外干净重启），避开会话内 restart 撞 3080 端口的已知坑。
- 所有启停**重启 DSH 后生效**（cordis patch 不热重载）。

### 11.4 自检开关（与 --dump-menu 同族）
- `--selftest-plugins`：扫描清单 + 演练 patch 生成 → `selftest-plugins.txt`（官方/外部/注入分类与安全模式 count 核对）。
- `--selftest-rpc`：DshAuth+DshRpc 打真实 3080 `session/list` → `selftest-rpc.txt`（`RPC OK session/list items=N`）。
- `--selftest-lock`：检查/清除孤儿启动写锁 → `selftest-lock.txt`（before/action/after/result 四行，见 §11.6）。
- `--selftest-exit`：探测两条退出信号是否都有监听者 → `selftest-exit.txt`（`keep` / `stopall` 两行 + `listener=YES/NO`，见 §11.7）。
- `--selftest-svcmenu`：一级模型行 +「当前模型」组自检 → `selftest-svcmenu.txt`（状态映射矩阵 / 真实端口探测 / 逐个选中的组渲染 / 一级旧入口残留 `OLD-TOPLEVEL-ENTRIES` / 叶子项 `SVC-MENU-LEAF-ITEMS`，见 §11.10）。
- `--selftest-menukeep`：菜单「点了关不关」裁决器自检 → `selftest-menukeep.txt`（纯函数三态 `ShouldCancel`/`ShouldCancelFocusChange`/`ShouldCloseOnClick` + 真实菜单树逐项 `IsState` 分类（含负向：③类项必须为 false）+ 挂载计数 + `Hook` 幂等（连调两次订阅数不增长）+ 一级模型行=叶子项契约（A12）+ 隔离菜单 `PerformClick` 行为对照 + ⑤ 一级项接线（接线数 / 判据与「启动模型」项 `Enabled` 同源 / 判据穷举含 3 条负向 / DSH 一级项仍③类 / 模型一级项①类 / 隔离菜单行为：`canStart=true` 触发恰好 1 次、闸门拦第二次、`canStart=false` 不触发，见 §11.9）。⚠️ 本探针跑在 CLI 进程里，**证不了真鼠标路径**（`PerformClick` 的 `Closing` 顺序与真左键相反）⇒ 真鼠标结论只能在常驻真托盘进程里取得；判据与实测时序见纪律 skill `~/.workbuddy/skills/tray-menu-keepopen` §二）。
- `--selftest-uithread`：UI 线程调度自检 → `selftest-uithread.txt`（**实例**探针 —— 需要一个"已创建但未 Show"的 `LogForm`，那正是 P0-1 的触发场景；判别力核心 = "消息泵未跑时，后台线程发起的 UI 更新**必须还没生效**"；另含 RichTextBox 上限裁剪。改造记录见 `docs/2026-09-11-architecture-governance.md` §9）。
- `--selftest-logsink`：日志缓冲自检 → `selftest-logsink.txt`（纯数据层，不建 TrayApp、不占单实例锁：8 线程 × 2 万行并发写 / `Length` 守恒 / 窗口有界 / 旧游标夹取 / 单行超上限仍保留 / `Clear` 不重置游标）。
  首行是 ASCII 锚点 `SVC MENU PROBE (...)`、并输出 `OLD-TOPLEVEL-ENTRIES = 0` —— 供 `apply-dsh-tray.cmd` 判定「源构建确实是新版」。
- 所有诊断模式都不占单实例锁、不影响运行中托盘；从 bin 目录跑需先拷 `publish/dsh-tray-config.json` 到 exe 同目录（否则静态兜底通道）。
- ⚠️ **诊断分支必须 return，永远不要落到 `Application.Run`**：漏写分支 + 该模式又跳过单实例锁 = 起出「没有锁的幽灵托盘」（通知区图标 + 永不退出 + 与真托盘抢菜单）。`Main` 末尾已加防御栏（命中任一诊断开关即 `ExitCode=3` 返回）。
- ⚠️ **`TrayApp` 构造的 dumpMode 决定会不会建 `TaskbarWatcher`/冷启动重注册**（隐藏窗口 + 伴随线程，进程不会因 `Main` 返回而退出）。现统一传 `! _isPrimary`（诊断一律 true）——原写法漏了 `menuProbe`。

### 11.5 子代理会话支持（2026-09-09 修复）
- **报错**：点开子代理会话 → `session/agent-busy: subagent Sessions require their durable parent address`。根因：`session/page` 的 `SessionAddress` 有两种形态，`origin:'subagent'` 的会话**禁止**用 `{kind:'session'}` 直连，必须 `{kind:'subagent', parentSessionId, childSessionId, mode:'one-shot'|'continuable'}`（校验在 session-controller/src/history.ts validateAddress）。
- **数据来源**：`session/list` items 自带 `origin`/`parentSessionId`/`projections.values.subagent.mode`（实测 468 个会话里 149 个是子代理）。注意子代理的 sessionId **没有 `session-` 前缀**，parentSessionId 有。
- **prompt/cancel 不可用于子代理**：裸 sessionId 调 session/prompt 被拒 `session/agent-busy: owned by subagent routing`（子代理由父会话经 subagent delivery 驱动）→ 瘦客户端对子代理会话**只读**：输入框禁用 + 徽标「子代理」+ 隐藏中断按钮。
- **fetchPage 三级错误恢复**：① `past cursor N` → 用 N 重试；② `mode does not match` → continuable/one-shot 互换重试一次（modeFixed 防刷新覆盖）；③ `durable parent address` 且本地无记录（列表未加载就直开）→ loadSessions 后用父地址重试。
- JS 侧维护 `subInfo[sid]` 映射（从每次 list 响应登记，含 blank 过滤项），`addrFor(sid)` 统一出口地址。

### 11.6 孤儿启动写锁自愈（2026-09-10）

**故障**：`DshStart` 起 dsh 后，进程秒退，`dsh-web-err.log`：
```
Error: atomic-write: timed out waiting for the writer lock at C:\Users\<u>\.dsh\profiles\node_modules.lock
    at withFileLock (packages/util/atomic-write/lib/index.js)
    at healProfilesModuleFallback (packages/boot/app-boot/lib/index.js)
    at composeProfile (apps/cli/lib/profile-boot-*.js)
```

**根因**：
- dsh 的 `composeProfile` 每次启动都校验共享 module fallback（`$DSH_HOME/profiles/node_modules`）与当前安装依赖闭包是否一致；不一致就重写符号链接，并用 `profiles/node_modules.lock` 做跨进程互斥。
- `packages/util/atomic-write` 的 `withFileLock` **按设计不回收陈旧锁**（源码注释原文：*the contender never removes an existing lock... orphan recovery is an operator action*），只等 2 秒。
- 任何一次启动在持锁期间被杀（**托盘「停止 / 重启 DSH」用的就是 `Process.Kill()`**，关窗口同理），锁就永久留下 → 之后每次启动都 2s 超时崩。诊断：`cat` 该 lock（内容=持有者 pid）→ 查 pid 是否还存在。

**处置（已内置，无需人工）**：
- `HealProfileOrphanLock(bool log)`（Program.cs）：读 lock 里的 pid → `Process.GetProcessById` 探活 → **死 pid（含空/非数字内容）才 `File.Delete`**；活 pid **保留不动**（有实例正在重写软链，抢删会撞车）。`DshStart()` 在 `Process.Start` 之前调用一次（配置校验之后），日志写 `[lock] ...`。
- DSH 菜单新增「检查/清除孤儿启动锁（node_modules.lock）」，手动一键复查。
- 自检开关 `--selftest-lock` → `selftest-lock.txt`：
  ```
  lock: C:\Users\...\.dsh\profiles\node_modules.lock
  before: present | absent
  action: absent | orphan-removed pid=X | held pid=Y | error: ...
  after:  present | absent
  result: OK (no stale lock left) | KEPT (lock owner alive)
  ```
- **排障提醒**：报这个错**不要重装依赖/重装插件**。依赖层通常是好的——用 `node apps\cli\lib\bin.js --profile web --dump-config` 验证（EXIT=0 即合成链路健康；`--version` 不合成 profile，测不出该故障）。

**同名同族的 dsh 侧兜底**（不在本仓库，防托盘没跑的场景）：
- `G:\Tools\deepseek-harness\fix-dsh-lock.ps1`（逻辑同源，可用 PowerShell `-File` 单测）+ `fix-dsh-lock.cmd` 薄封装。
- `dsh-web-server.cmd` 启动前 `call fix-dsh-lock.cmd /quiet`；`C:\Users\Landrom\bin\dsh.cmd` 加 `if exist <lock> call ...`；桌面 `fix-dsh-lock.cmd` 一键恢复。
- ⚠️ 三处逻辑（C# / PS1 / .cmd）语义必须一致：**活 pid 保留、死 pid 删除**。改动任一处时同步另两处。

### 11.7 退出语义 / `--exit`（默认保留模型）· `--exit --stopall`（2026-09-11 反转）

**默认不可有破坏性**：`--exit` / 菜单「退出」= 只退托盘、**保留模型**；要停模型必须显式 `--stopall`。

**两条互不干扰的退出信号**（`Main` 里用 `WaitHandle.WaitAny` 同时监听；`ExitSignalName(bool keepModel)`）：

| 触发 | 事件名 | 行为 |
|---|---|---|
| `DSHTray.exe --exit`（默认）/ `--exit --nostop`（兼容别名）/ 菜单「退出」 / 菜单「重启托盘」 | `Local\DSH托盘_ExitSignal_NoStop` | `ExitApp(false)` → **不停模型** → 注销图标 → 退出 |
| `DSHTray.exe --exit --stopall` / 菜单「退出（同时停止模型服务）」 | `Local\DSH托盘_ExitSignal` | `ExitApp(true)` → `StopAll()` **停模型** → 注销图标 → 退出 |

- **两次语义翻转的历史**（老文档/老脚本按这个对号入座）：
  - `2026-09-10b`：新增第二条信号，`--exit` 仍是"停模型"，保留模型要加 `--nostop`；
  - `2026-09-11`：**默认反转** —— `--exit` 改为保留模型，停模型要 `--stopall`；`--nostop` 降级为兼容别名（等价默认，旧脚本原样可用）；菜单两项随之改名（`退出` / `退出（同时停止模型服务）`）。
- **为什么两条信号名保持不变**：事件名与各自含义是**配套固定**的 —— `_ExitSignal` 恒为"停模型"、`_ExitSignal_NoStop` 恒为"保留模型"。因此反转默认值**只改发送端选哪条**，不改名字：老发送方（旧 `--exit` 想停模型）发 `_ExitSignal` → 新托盘照样停；旧发送方 `--nostop` 发 `_NoStop` → 新托盘照样保留。**跨版本两个方向都安全**（实测：新 exe 对老托盘发保留信号 → 老托盘听得懂，语义一致）。
- **为什么不用 `taskkill /F` 保模型**：强杀能保住模型，但走的不是 `ExitApp` → `icon.Visible=false` 不执行 → 通知区留死图标槽，且老实例的 `svc.proc` 句柄丢失。信号退出是优雅退出：图标干净注销、模型照常服务、下次启动 `Adopt()` 接管。
- 发送方会写 `exit-signal.txt`（放在 exe 同目录），并用**退出码**表态：`0`=信号已送达（有监听者）、`2`=没有托盘在跑（无人接收，纯 NOOP）。便于脚本判定。
- 自检 `--selftest-exit` → `selftest-exit.txt`：
  ```
  EXIT SIGNAL PROBE  (Local\ session 命名事件)
  semantics: --exit = keep model (default) | --exit --stopall = stop model too
  keep    name=Local\DSH托盘_ExitSignal_NoStop  listener=YES (托盘在跑，信号可送达)
  stopall name=Local\DSH托盘_ExitSignal         listener=NO  (无托盘监听)
  ```
- 例外：诊断开关和 `--exit` 都在**单实例锁之前**返回，天然可安全重复调用；`--exit [--stopall] --selftest-exit` 组合时**诊断优先**（只探测不发信号，实测未写 `exit-signal.txt`）。

### 11.8 每模型二级菜单（**2026-09-26 已取消**，见 §11.10）+ 「重启模型」不中断会话（2026-09-11）

> ⚠️ **本节描述的是 09-26 之前的形制**。那次改造把每模型的二级菜单**整体移除**（功能上移到一级菜单的
> 「当前模型」组），下面这段保留作历史依据（状态语义 / 重启不中断会话 / `MenuKeepOpen` 三件套都仍然成立），
> 但「每模型一条二级菜单」这个形态**已不存在**，判据见 §11.10。

**原一级项 = `<状态圆点> <模型名> (<端口>)`**（原一级的「启动 <模型> (端口)」与「打开 DSH 会话（<模型>）」合并进来，一级不再单列每模型入口 —— 用 `--selftest-svcmenu` 的 `OLD-TOPLEVEL-ENTRIES = 0` 判定）。

**状态语义**（`Service.Start / PortBusy` + `SvcState/SvcDot/SvcEnable` 三个纯函数，便于枚举验证）：

| 状态 | 判定 | 圆点 | 启动 | 重启 | 停止 |
|---|---|---|---|---|---|
| 未运行 | 无进程、端口无响应 | 红 | ✔ | ✔ | ✘ |
| 启动中 | `Start()` 已拉起进程、`/health` 未就绪（`Starting=true`，Tick 每 2s 探活） | 黄 | ✘ | ✔ | ✔ |
| 运行中 | `proc` 存活（本托盘启动或 `Adopt()` 接管） | 绿 | ✘ | ✔ | ✔ |
| 运行中(未托管) | `proc` 为空但端口 `/health` UP（外部/上一实例启动、且接管失败，如权限降级） | 橙 | ✘ | ✔ | ✔ |

- 就绪判定与实测：Tick 对「启动中」服务每 2s GET `/health`（`{"status":"ok"}`），就绪时记录耗时并 GET `/props` 写 `runCtx`/`runVision`；运行中服务每 10s 复采一次。**配置行里的「实测」就是它俩** —— 实测值可能与托盘配置不同（例：托盘 ctx=192K、实际服务端 256K）。
- `Start()` 设 `Starting=true`；进程若秒退，Tick 判 `HasExited` → 清标志并提示（看日志窗口 / `model-start.log`）。
- **「重启模型」= 只重启 llama-server**：`ReloadSvcConfig` → `Stop` → 等端口释放（≤6s）→ `Start`。
  - `ReloadSvcConfig` **只读解析** `dsh-tray-config.json`（**绝不调用 `Config.Load()`** —— 它在文件缺失/无服务项时会**把默认配置写回文件**，会抹掉用户真实配置；`dsh-tray-config.json` 不在 git、不会自动重建）。按 `Name` 匹配、回退 `Port`，把 `Model/Port/UseMmproj/Mmproj/Batch/Ubatch/SpecDecode/Provider` 的差异逐条打进日志；同时 `LoadCfg()` 重读 `dsh-tray.cfg`（ctx/KV/切分/MTP/GPU/缓存内存/监听…）并 `RefreshChecks()`。
  - **不碰 DSH(3080)** → dsh 会话（对话历史）不中断，仅"正在生成的那一轮"需重发。**端口不变时 dsh 侧无需改配置**；改了端口请点一次「启动 DSH 会话」重新校准 provider baseURL（§4）。
  - `Stop` 兜底：`proc` 为空但端口有 llama → `KillByPort`（netstat → LISTENING pid → **进程名含 llama 才杀**），否则菜单会出现「端口被占却停不掉」的死角。
- 「启动/重启/停止」三项**点击后不收起菜单**：统一走 `MenuKeepOpen`（`MenuKeepOpen.cs`）。
  - 🔴 **实测时序（与直觉相反）：`Closing` 早于 `Click`** —— 点一个项的序列是 `Closing(ItemClicked) → Click`；真鼠标路径下根菜单 `Closing(AppFocusChange)` 还**先于**子菜单 `Closing(ItemClicked)`。所以"在 `Click`/`CheckedChanged` 里记标志位、再到 `Closing` 里查"**根本走不通**（旧 `KeepOpenOnClick` 就是这么写的，一次也拦不下来，本轮已删）。
  - 正解 = **先拦后判三件套**：① `Closing` 遇 `ItemClicked` 一律先 `e.Cancel = true`；② `AppFocusChange` 只在"500ms 内真按过本菜单项"（`MouseDown` 早于两次 `Closing` 留下的印记）时才拦，否则菜单会变钉子；③ 被拦下的③类项由我们 `BeginInvoke` **延后**补关（**绝不**同步 `Close()`，否则 `Closed → Rebuild → Dispose 正在处理点击的项` = 抽地板 → 原生崩溃）。
  - 纪律**唯一源**：`~/.workbuddy/skills/tray-menu-keepopen`（改这类菜单前**先读它**，不要照抄某个托盘的写法 —— 模板不是参考系，语义才是）。
  - 落地：这三项（连同**「当前模型」组的 start/restart/stop**、每模型一级项、`重启全部`、DSH 启停、各勾选项）由 `TrayApp` 构造末尾的 `_keep.DeclareStateItems(_keepItems)` 声明为①②类（**不用 `Tag`** 作判据载体）；8 个参数面板下拉走 `_keep.DeclareWholeState(...)`；其余③类项**不声明** ⇒ fail-safe 自动补关。**未声明的项最坏只会"点了关"（原缺陷），绝不会变成关不掉的钉子。**
    ⚠️ 计数契约随之改口径（09-26）：`StateItemCount == 12 + svcMenus.Count`（全局 9 「当前模型」组 3，加每个一级模型行 1）、`AttachedDropdowns == 10`（不再 +svcMenus.Count）。
- ~~子菜单**元素固定**（20 项：…）~~ → 二级菜单已于 09-26 取消，**这条纪律的落点变成「「当前模型」组的那 9 条（3 启停 + 2 会话 + 1 标题 + 1 状态 + 1 配置行）**：运行期只改 `Text/Image/ToolTipText/Enabled`，**绝不增删 `DropDownItems`**（菜单显示期间改结构会静默破坏弹出，§9）。
  09-26 补一条**叶子项契约**：一级模型行的 `DropDownItems.Count` 必须恒为 0（即"不许长出二级菜单"）—— 这与旧"20 项"是同一个防线的两种写法，新的那个不会因为新增项而误报。
- ⚠️ `ToolStripItem.Visible` 的 getter 在**父下拉从未显示过**时恒为 false —— 自检输出不要用 `it.Visible` 判空（原探针因此漏印 6 行配置），按原始文本打印。
- ⚠️ 悬浮提示：`ContextMenuStrip.ShowItemToolTips` 需显式置 true（托盘已置）。
- 自检 `--selftest-svcmenu` → `selftest-svcmenu.txt`：
  ```
  SVC MENU PROBE (ascii-anchor; svcmenus=3)          ← ASCII 锚点，供 .cmd 判定新版
  === 状态映射矩阵 ===                                 四种 starting/running/busy 组合 → 圆点/状态文本/启停可用
  === 真实端口探测（只读：Adopt + /health + /props）===   逐服务 health/是否接管/实测 ctx/视觉
  === 一级模型行 + 「当前模型」组（真实配置渲染，逐个选中模型打一遍）===
  === 一级菜单是否残留每模型旧入口 ===  OLD-TOPLEVEL-ENTRIES = 0  (OK)
  === 一级模型行是否为叶子项 ===         SVC-MENU-LEAF-ITEMS = 3 (OK)
  ```

### 11.9 一级项快捷启动（2026-09-12）
- **交互**：一级项（`DSH` / 每个 `<模型名> (端口)`）在**未启动**时，直接点它 = 快捷启动，不必展开子菜单到「启动 DSH」「启动模型」。已启动/启动中/端口被外部占用 ⇒ 只走原行为（展开子菜单）。
  动机是**交互成本**：一级项位置固定、抬手就到，而「启动…」藏在子菜单里 —— 省的是"视觉搜索 + 瞄准"（悬停本来就会展开子菜单，点击次数并没变）。
- **叠加式**：**不改变任何菜单展开/关闭语义** —— 子菜单照常弹出、菜单该关还关。所以一级项**仍是③类**（`_keepItems` 里没有它们），`MenuKeepOpen` 的判据一个都没动。
- **判据铁律（必须与用户所见一致）**：只有**子菜单里那个「启动…」项当前可点**时才触发 —— 模型 = `SvcStatus.Enable(starting,running,busy).start`（与 `m.start.Enabled` 同源），DSH = `dshState==0`（与 `dshStartItem.Enabled` 同源）。⇒ 圆点红 / 项可点 / 点一级项会启动，**三者同真同假**。
  ⚠️ 判据只能读**缓存的运行时状态**（Tick 后台探针刷新的），**绝不能**在点菜单时现探端口：`SvcProbe.PortUp` / `DshUp` 都是同步 HTTP/TCP，会当场冻住 UI。
- **幂等**：`Click` 与 `MouseUp` 两路都挂（父项点它到底触发不触发 `Click`，程序化实测**会触发**——见 `MenuKeepOpen` 那条注释），共用一个 **1500ms 闸门** ⇒ 一次点击最多放行一次，连点不会重复拉起进程。`start` 侧还有二层守卫（`Start(svc)` 拒"已运行/端口被占"、`DshStart()` 拒"3080 已在听"）。
- **回执**：**气泡（主回执）** + 日志 `[快捷启动] …`。⚠️ 别把"子菜单里的状态行"当成必然回执 —— 一级项是③类，`Click` 触发时裁决器会补关菜单（程序化实测如此）⇒ 菜单可能已经关了；「状态：启动中 · 约 30-60s」是**下次打开菜单**才可见。
- 实现：`TrayApp.QuickStart.cs`（`WireQuickStart`）；接线点 **只有** `TrayApp.cs:143`（DSH）——
  ⚠️ 2026-09-26 起**模型的一级项不再接快捷启动**（语义改成"点=选中"，§11.10），接线数期望由 `1+svcMenus.Count` 降为 `1`。
  可发现性提示：DSH 在 `dshMenu.ToolTipText`。
- 自检：`--selftest-menukeep` 的 **⑤ 节** —— 接线数、判据同源、判据穷举（4 条含 3 条负向）、DSH 一级项仍③类、隔离菜单行为（`canStart=true` 触发恰好 1 次 / 闸门拦第二次 / `canStart=false` 不触发）。
  ⚠️ **别用反射读项的事件订阅数**：WinForms 的 `ToolStripItem` 事件走 `EventHandlerList(Events)`，**没有同名 backing field**（那样写恒得 0，是读法错，不是没挂）。

### 11.10 一级模型行 = 选中 + 「当前模型」组 + 参数跟随（2026-09-26）

用户指令原文要点：MTP 可选项 4 → 6；一级菜单的模型名**从"点击启动"改为"点击选中（互斥）"，默认上一个选中的模型**；
下方模型启动配置**跟随当前选中模型**，语义从「启动时覆盖」改为「**修改对应模型配置的持久化配置**」；
**取消模型选项的二级菜单**，所需功能**上移一级菜单**。

- **选中态**：`selSvc` 是唯一事实源。一级模型行点击 → `SelectSvc()`（①类：点完不收起菜单，好接着调参数）；
  启动时 `SelectInitial()` 按 `dsh-tray.cfg` 的 `selectedModel=` 复原，解析不出则回落**配置里的第一个模型**
  ⇒ 保证 `selSvc` 永不为 null，下游不必判空。
  ⚠️ 为什么选中态**不写** `dsh-tray-config.json` —— 那是服务清单（会被增删改名/改端口），往里塞"上次选中谁"
  会让清单语义 polluted；`selectedModel=` 是托盘自身的偏好，落 `dsh-tray.cfg` 才对。
- **参数语义（本次的核心变更）**：9 项参数（ctx / KV / 缓存内存 / 切分 / GPU1 占比 / MTP / 参数组 / 监听 / GPU 选择）
  在 `ServiceConfig` 上新增**同名可空**字段（注意是 `int?`/`bool?` 而不是 `0` —— `0` 是合法值：缓存内存=禁用、ctx=8K）。
  - 合成规则收在**纯函数** `SvcParams.Resolve(spec, 全局默认…)`：本模型覆盖优先，**逐项**回落全局默认
    （不是整组覆盖：给 8082 改 KV 不该牵连它的上下文）。覆盖项逐个计数进 `Overrides`，菜单据此显示"本模型覆盖 N 项"。
  - 写回链路 `PersistParam(apply,label)`：改一个参数 → 立刻 `Config.Save(cfg)` 写 `dsh-tray-config.json`
    对应服务项 → 日志写明"已存到模型 X 的配置；重启该模型生效"。**传的是 `cfg` 本体不是重建的默认配置**
    （`Config.Save` 是整份覆盖写，传错会抹掉手工配置）。
  - 全局默认（`dsh-tray.cfg`）**没有删**，退居"未覆盖模型用的基线"，保住"多个模型共用一个基线"这条既有行为。
- **四处同源**：启动(`Start`) / 状态行(`ToView`) / 「运行时配置」行(`RefreshCurGroup`) / 启动日志，全部读同一个
  `SvcParams.Resolve` 结果 —— 否则会出现"菜单写着 192K、实际按 128K 起"（执行鸿沟）。
- **无二级菜单的代价**：会话入口与启停都变成"作用于当前选中模型"，所以组里每条**文案/后缀/tooltip 必须能预测行为**
  （唯一源仍是 Core 的 `SessionEntryLabels`）：「启动 DSH 会话」随内置渲染开关加「（内置渲染）」后缀，
  「启动内置对话」恒走内置渲染、不受开关影响（`OpenBuiltinText()`）。
- **契约红线（自检会判，改动必须同步）**：`--selftest-menukeep`（A10 `AttachedDropdowns==10` / A11 `StateItemCount==12+svcMenus.Count` /
  A12 一级模型行=叶子项 `DropDownItems.Count==0` / ⑤ 接线数==1）、`--selftest-svcmenu`（`SVC-MENU-LEAF-ITEMS`）、
  `--selftest-tips`（D1a 全树下拉数==10）、`SvcParamsTests`（覆盖/回落/0 值/往返）。

## 12. 源文件结构（2026-09-11 S1 按类型拆分 → 09-12 S3 分工程 → S4 加测试）

> 拆分是**零逻辑改动**的（`partial` 对编译器是同一类型），但**改代码前必须先找对文件** —— 原来全挤在 `Program.cs` 里的东西现在按职责分了 20 个文件。原文行号对照见 `docs/2026-09-11-architecture-governance.md` §10.1。
>
> ⚠️ **S3–S5 起有三个工程**：无 UI 依赖的逻辑层在 `src/QwenTray.Core/`（下表标 **〔Core〕** 的行），单测在 `tests/QwenTray.Tests/`。**加新文件前先想清楚它属于哪边** —— 放进 Core 而它引用了 WinForms，会直接编译失败（这是故意的，见 §12.1）。

| 文件 | 职责 | 关键内容 |
|---|---|---|
| `Program2.cs` | **入口** | `Main` + 全部分派、单实例互斥、两条退出信号监听（S4 起命令行**解析**在 `CliOptions`，见下一行） |
| `src/QwenTray.Core/Cli.cs`〔Core〕 | **命令行解析**（S4 抽出） | `CliOptions.Parse`（纯数据）；`IsDiagnostic`（全部诊断/自检模式）、`IsLockFree`（诊断或 `--bench`）—— 两个判定各被单测**穷举**，新增探针忘了登记就会红 |
| `src/QwenTray.Core/SvcStatus.cs`〔Core〕 | **服务状态映射 + 探活**（S5 抽出） | `SvcStatus.State/Dot/Enable`（四态文本 / 圆点色名 / 三个启停项可用性，**8 种组合穷举钉住**）；`SvcProbe.PortUp/HealthUp/KillByPort`（端口探活与按端口回收 llama）。⚠️ `PortUp` 与 `HealthUp` **语义不同**（前者"端口上有东西"，后者"响应体含 ok"），别合并 |
| `src/QwenTray.Core/SvcLines.cs`〔Core〕 | **菜单文本合成**（S5-3 抽出） | 4 个档位标签（`KvLabel`/`CacheRamLabel`/`MtpLabel`/`ParamLabel`，越界回落到最保真档）、`Mid`（宽度截断，**必须恰好 `max` 字符**）、`Dur`、`CfgLines`（「运行时配置」**恒 6 行** —— 菜单按这个数建行池）、`StatusTip`。输入是 `SvcView`，由 `TrayApp.ToView(Service)` 拍快照（**`Service` 与 Core 之间唯一的收口点**）。⚠️ 已知可见缺陷：多卡行渲染成 `GPU0+1`（`Join("+")` on `List<int>`），与 `GpuSelection.ShortLabel()` 的 `GPU0+GPU1` 不一致 —— **未修**，断言钉住现状 |
| `src/QwenTray.Core/SvcConfigDiff.cs`〔Core〕 | **重读配置的判定内核**（S5-4 抽出） | `Find`（Name 忽略大小写 → 退同 Port）+ `Apply`（就地写穿 `ServiceSpec` + 返回人可读差异列表）。**三条守卫**：`Batch`/`Ubatch` 只在 `>0` 时覆盖、`Provider` 只在非空时覆盖、`Mmproj` 是双条件 —— 它们决定"用户手改配置能否生效 / 会不会把内存里的值抹掉" |
| `TrayApp.cs` | 托盘骨架（partial 1/5） | 全部字段声明、`TrayApp()` 构造函数（建菜单/图标/`clock`）、`dsh-tray.cfg` 读写（`Cfg`/`LoadCfg`/`SaveCfg`/`SaveAllPos`）、`RefreshChecks`、显存分摊提示。4 个档位标签留**迁移期兼容层**转发到 `SvcLines`（与 `Service` 那 10 个转发属性同一惯例） |
| `TrayApp.Services.cs` | 模型服务（partial 2/5，2026-09-26 改造） | `BuildSvcMenu`（一级模型行=叶子项，点击=选中）/ `SelectSvc` / `RefreshCurGroup`（「当前模型」组渲染）/`ToView(Service)`（拍 `SvcView`）、`Start`/`Stop`/`StopAll`/`RestartSvc`/`ReloadSvcConfig`（进程编排面）。探活/状态映射/文本合成/配置差异判定**均已移出**，见上三行 |
| `TrayApp.Dsh.cs` | DSH 与日志（partial 3/5） | `DshUp`/`DshStart`/`DshStop`/`DshRestart`、`ScrubWorkBuddyEnv`/`StripWorkBuddyShim`（崩溃链 A 修复）、`TailLog`/`ClearDshLogFiles`/`OpenDshLog`、`Bg`/`Ui`/`ReportEx`（S0 调度器入口） |
| `TrayApp.Sessions.cs` | 会话与弹窗（partial 4/5） | `UiThreadProbe`（S0 自检）、dsh 配置 YAML 读写（`ExtractBlock`/`ReplaceBlock`/`EnsureLlamaProvider`）、`OpenPop`/`OpenThin`/`OpenOfficial`、插件管理入口、最近会话（`ReadRecent`/`RebuildRecentMenu`/`DotFor`） |
| `TrayApp.Lifecycle.cs` | 生命周期与自检（partial 5/5） | `Adopt`（接管外部 llama）、`Tick`（1s 节拍）、`ExitApp`/`ExitFromSignal`/`RestartTray`、全部 `*Probe()` 自检入口 |
| `LogForm.cs` | 统一日志窗口 | 单窗口双页签 + 工具栏 + 时间戳规则（`Append`/`AppendDsh`/`AppendDshStamp`/`Trim`） |
| `SvcMenu.cs` | 每模型一级行的容器（2026-09-26） | 只留 `svc` / `root` / `sig`；其余 11 个字段是二级菜单时代的遗留，注释标明未装配（形态若复活可直接接回） |
| `src/QwenTray.Core/SvcParams.cs` | 参数解析（2026-09-26 新增） | 纯函数 `Resolve`：`本模型覆盖优先 + 逐项回落全局默认`，四处消费点同源 |
| `UiDispatcher.cs` / `SelfTests.cs` | S0 基础设施（留在主工程） | UI marshal 锚点 / 自检实现 |
| `src/QwenTray.Core/LogSink.cs`〔Core〕 | **有界日志缓冲**（S0 起就是独立类型，S5 进 Core） | 三写一读线程安全、`Length` 单调可当游标、超上限丢**最早**且**至少留一行**、`Read` 对滑出窗口的游标**夹取而不抛**、`Clear` 不重置 `Length`（9 个单测逐条钉住） |
| `src/QwenTray.Core/Perf*.cs`（5 个）〔Core〕 | 性能**数据层** | `PerfFingerprint`(指纹) → `LlamaLogParser`(stdout 解析) → `PerfModel`(数据模型) → `PerfStore`(存储+闸门) → `PerfSampler`/`PerfRuntime` |
| `PerfPanel.cs` / `PerfProbe.cs` | 性能**UI 与自检**（留在主工程） | `PerfPanel` 是页签 UI；`PerfProbe` 是 `--selftest-perf` 的探针实现（含 `FP-REAL-8081` 回归锚） |
| `src/QwenTray.Core/ServiceSpec.cs`〔Core〕 | **配置描述**（S2 切出） | 10 个配置字段 + `ServiceSpec.From(ServiceConfig)` 唯一构造入口。**不引用** `Process`/WinForms` |
| `Service.cs` | **运行时状态袋**（留在主工程） | 组合 `Spec` + `proc`/`log`/`Starting`/`runCtx` 等。10 个转发属性（`Name`/`Port`/`Model`…）是迁移期兼容层，**也是 S5 的删除清单** —— 到时改成直接访问 `Spec`，编译器会逐处报错指路 |
| `src/QwenTray.Core/Config.cs` / `LaunchArgs.cs`〔Core〕 | JSON 配置契约 / 启动参数构建 | `LaunchArgs.Build` **只依赖 `ServiceSpec`**（S2 起） |
| `ThinChatForm.cs` / `PluginCenter.cs` / `PluginManagerForm.cs` | 内置渲染 / 插件中心 / 插件管理弹窗 | |
| `src/QwenTray.Core/DshAuth.cs` / `DshRpc.cs` / `HwInfo.cs` / `SysInfo.cs` / `GpuInfo.cs` / `BenchRunner.cs`〔Core〕 | 无 UI 依赖的工具类 | S3 已收纳（Core 编译期不许引用 `System.Windows.Forms`） |
| `AutoStart.cs` | 开机自启（留在主工程） | 真用 `Application.ExecutablePath` ⇒ **不能进 Core**；按目标架构它归 `Integr.` 而非 `Core`（报告 §12.1） |
| `src/QwenTray.Core/MemTrim.cs`〔Core〕 | **内存回收判定内核**（2026-09-13 新增） | `TrimKind`（Manual/OnReady/Idle）、`AllowIdle/AllowOnReady/Allow`、`ClampInterval`/`IntervalLabel`/`ResultLine`/`Bytes`/`PolicyText`。**纯函数、无 UI 依赖**；唯一硬规则 `anyProcessing ⇒ false`（单测钉住） |
| `src/QwenTray.Core/WinMem.cs`〔Core〕 | **工作集回收 P/Invoke**（2026-09-13 新增） | `OpenProcess(QUERY_INFORMATION\|SET_QUOTA)` + `EmptyWorkingSet` + `CloseHandle`；`WorkingSetBytes`/`TrimWorkingSet`/`PidByPort`（netstat）。⚠️ **刻意不用 `Process.Handle`**（Adopt 的外部实例权限不全 ⇒ 静默失败） |
| `src/QwenTray.Core/MemTrimCli.cs`〔Core〕 | `--trim [port]` 脚本入口（2026-09-13 新增） | **不建 `TrayApp`**，逐服务回收并打印 `RESULT OK/NOOP`；写 `selftest-trim.txt` |

**回滚（S1 结构拆分）**：`Program.cs.bak-s1` / `ModelPerf.cs.bak-s1` 是这两个文件的前身（`.cs.bak-*` 后缀不被 SDK 编译）；回滚 = 删掉 20 个新文件 + 把这两个改名回去。

### 12.1 三个工程（S3 分工程 → S4 加测试 → S5 补测试，2026-09-12）

```
dsh-chat-popup/
├─ QwenTray.csproj           ← DSHTray.exe（WinForms 托盘，UseWindowsForms=true）
│  └─ *.cs（20 个：TrayApp 5 个 partial / Program2 / LogForm / UiDispatcher / Service / …）
│     └─ ProjectReference ──┐
├─ src/QwenTray.Core/       │
│  ├─ QwenTray.Core.csproj ─┘  ← QwenTray.Core.dll（UseWindowsForms=false）
│  └─ *.cs（24 个：Config / ServiceSpec / Cli / LaunchArgs / LogSink / SvcStatus / SvcLines / SvcConfigDiff / NodeLocator / Perf 数据层 / MemTrim / WinMem / MemTrimCli / SessionEntryLabels / DshAuth / DshRpc / 硬件采集）
└─ tests/QwenTray.Tests/       ← 247 个单测（xunit 2.5.3 + coverlet，UseWindowsForms=false）
   ├─ QwenTray.Tests.csproj
   └─ *Tests.cs（CliOptions / LlamaLogParser / PerfFingerprint / Config / LaunchArgs / LogSink / NodeLocator / SvcStatus / SvcLines / SvcConfigDiff）
```

**归属口径**：碰 `System.Windows.Forms` / `Application` 的**必须留主工程**（例：`AutoStart.cs` 用 `Application.ExecutablePath`，进了 Core 直接 CS0234）。反过来，"只是返回颜色名/中文文本"的**纯映射也算纯逻辑** —— `SvcStatus.Dot` 返回 `"yellow"` 就该进 Core，因为它可测而 UI 面不可测。

**命名空间两边都是 `QwenTray`**（只是程序集不同）⇒ 调用点零改动。

**构建/发布不变**：`dotnet build -c Release` 会先编 Core 再编主工程；`dotnet publish -c Release -o publish-next` 产物里会多一个 `QwenTray.Core.dll`。`apply-dsh-tray.cmd` 用的是 `robocopy /E` **整目录**同步，**不需要改脚本**。测试工程**不在**构建/发布链上（主工程不引用它）—— 这是有意的，发布产物里不会混进 xunit。

**四条必须知道的规则**：

1. **Core 不许引用 WinForms** —— 由 `UseWindowsForms=false` 在编译期强制（引用了就 `CS0234`，不是警告、不是约定）。不要把某个文件搬进 Core 后再把开关打开去迁就它；那个文件属于 Ui 侧。
   *附带收益*：这条围栏在 S3 落地当天就清出了 S1 切分时**复制进每个文件**的 14 行僵尸 `using`（`System.Drawing` + `System.Windows.Forms`）。详见报告 §12.2。
2. **主工程排除两个子工程目录，必须排整个目录**：
   ```xml
   <DefaultItemExcludes>$(DefaultItemExcludes);src/QwenTray.Core/**;tests/**</DefaultItemExcludes>
   ```
   只排 `*.cs` 会让子工程 `obj/*.cs`（`AssemblyInfo` 那批）卷进主编译，报 **CS0579「特性重复」**（报告 §9.5 / §12.3）。
3. **不要建 `.sln`** —— 根目录必须保持「只有一个 `.csproj`」。发布流程用 `dotnet publish -c Release -o publish-next`，**无参数 `publish` 在含 `.sln` 的目录会要求显式指定工程而失败**。跑测试显式给路径。
4. **改探针清单要同步改两处** —— `Cli.cs` 的 `IsDiagnostic` 与 `tests/…/CliOptionsTests.cs` 的 `DiagnosticFlags()`。漏改会让那条穷举断言变红（这正是它存在的意义：替代"靠人记住"）。

**跑单测**（不需要托盘、不需要模型、不碰真实配置）：

```powershell
dotnet test tests/QwenTray.Tests/QwenTray.Tests.csproj -c Release
# 带覆盖率
dotnet test tests/QwenTray.Tests/QwenTray.Tests.csproj -c Release --collect:"XPlat Code Coverage"
# 解析覆盖率报告（留档脚本）
python docs/temp/gov/s4_coverage.py
```

⚠️ 测试**绝不调用 `Config.Load()`** —— 它有写盘副作用（读不到就把默认配置写回进程目录）。要测回填语义请用纯函数 `Config.MergeFrom` / `Config.Merge`。

**S4 的改造验收**：`dotnet test` 93/93 全绿；编译 0 错误 / 30 警告（= S3 基线）；10 项自检与 S3 主树**同时刻**差分 ⇒ 8 项逐字节一致、2 项仅运行时刻差异；另对断言本身做了负向测试（摘掉一个探针 ⇒ 如期 1 个失败）。详见报告 §13。

**S5 的改造验收（S5-1/S5-2 + S5-3/S5-4）**：`dotnet test` **193/193**；编译 0 错误 / 30 警告（无新增）；11 项自检 + `--dump-menu` 与**同机基线**逐字节差分 ⇒ **8 项一致**、3 项差异**逐条定性**全为时刻/环境类（`menu-dump` 归一化"相对时间"后**逐字节一致 ⇒ 菜单可见文本零变化**；`logwin` 仅时间戳 + 1 个会话注入的 `CODEBUDDY_CURRENT_MODEL_ID`；`perf` 仅 `ts`，`cfg` 指纹与统计量全等）；负向测试改坏目标错因 ⇒ 如期红（S5-1/2：`SvcStatus.State` 优先级 3 红；S5-3/4：摘 `Apply` 的 `Batch>0` 守卫 + 把 `Mid` 宽度算错 1 ⇒ 3 红 / 190 通过），还原后与 HEAD 内容一致。⚠️ 基线**必须取在干净 worktree 检出上** —— 主树当时有**另一会话**的在制品，拿主树当基线会把他人的改动算成我的差异（报告 §15.5 另记了 `sha256 -c` 假红这条行尾坑）。详见报告 §14 + §15。

**S5 收口说明（2026-09-12 下午）**：S5 里**能自动验证的部分已全部交完**。原定"把 `MenuBuilders` / `ServiceManager` 搬成**独立类型**"那两块**判定不做** —— 按 §11.3 判据（拆分要看它**解锁了什么下游动作**）它们解锁 0 自动验证，而要动 `TrayApp` 的 ~50 个菜单字段 / 5 个 partial 约 50 处调用点（含 `TrayApp.Dsh.cs::RefreshDshUi`），风险最高且验收只能靠真人过报告 §7 的**托盘行为回归清单 10 项**。真要做得单独开一次带人工回归窗口的任务。`Service` 的 10 个转发属性同理留着（删它们就是 S5-4 的删除清单，编译器逐处报错指路）。

**S3 的改造验收（同机同配置差分法）**：改造前后各跑一遍 `--selftest-*` 全套 + `--dump-menu`，逐字节比对 ⇒ 实质内容一致；`FP-REAL-8081 = PASS (966d1620a3)`；真实 `--bench 8081` 打印 `已记入台账 cfg=966d1620a3`，台账 17 条不新增。基线留档 `~/.workbuddy/_backup/20260912_s3_baseline/`。

## 13. 内存回收（三路径）与三级 KV 缓存（2026-09-13 新增）

### 13.1 内存回收 —— 托盘菜单「内存与缓存」

| 路径 | 触发 | 默认 | CLI |
|---|---|---|---|
| 手动 | 菜单「立即回收运行中模型内存」 | 随时可用 | `DSHTray.exe --trim [port]`（**不开托盘也能用**） |
| 就绪后一次 | 服务 `/health` ok 那一拍打标 `pendingTrimOnReady`，下一 tick 消费 | **开** | — |
| 空闲自动 | 每 30 s 评估一次 | **开**，间隔 **30 分钟**（6 档：5/15/30/60/120 分钟 + 仅手动） | — |

- **判定内核在 Core**：`MemTrim.cs`（纯函数）+ `WinMem.cs`（P/Invoke）+ `MemTrimCli.cs`（`--trim` 入口）。托盘侧只有 `ToggleTrim*` / `TrimOne` / `TrimAll` / `AppendTrimLog` / `TrimTick`（每 30s，`_trimBusy` 互斥，重活丢 `Bg`）。
- 🔑 **硬规则：`anyProcessing == true ⇒ 一律不回收`**（推理中回收 = 缺页抖动）。`SvcProbe.Busy(port)` 探不到 `/slots` 时**返回 true（当"忙"）**——保守优先，绝不误伤推理。单测 `Idle_ProcessingInFlight_NeverAllows` 钉住。
- ⚠️ `WinMem` **刻意不用 `Process.Handle`**：`Adopt()` 接管的外部实例句柄权限不全，用它调 `EmptyWorkingSet` 会**静默失败**（不抛异常、也不回收）。一律自己 `OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_SET_QUOTA)` + `CloseHandle`，回收后 `Thread.Sleep(120)` 等内核摘页。
- **只推工作集**：不改提交量、不释放显存。与 KV 缓存**正交**，可叠加。
- 台账 `E:\kv_cache\mem-trim.ndjson`（每回收一行 ndjson）。间隔经 `ClampInterval` 夹取（`[60, 86400]`），cfg 里的非法值绕不过去；`仅手动` = 86400 档。
- 自检：`--selftest-memtrim [live]` → `selftest-memtrim.txt`（20 项；不加 `live` 只验策略矩阵 + 菜单接线 + 台账可写，**不真回收**）。

### 13.2 三级 KV 缓存（对标 DeepSeek 落盘）

| 层 | 载体 | 开关 | 谁管 |
|---|---|---|---|
| L1 显存 | llama.cpp KV cache | `-c` / `--cache-type-k\|v` | 自动 |
| L2 内存 | host 前缀缓存（context checkpoints） | `--cache-ram <MiB>` | 自动（LRU） |
| L3 硬盘 | slot 快照 | `--slot-save-path DIR`（托盘默认 `E:\kv_cache\slots`，菜单「硬盘 KV 缓存」可关）+ `POST /slots/{id}?action=save\|restore` | ⚠️ **上游只给机制、不给策略** ⇒ 外部驱动 |

- **上游为什么不够**：FR #17107「auto-persist slot」被官方关闭为 *not planned*。策略层落在 `E:\kv_cache\kvctl.py`（`status/ls/save/restore/drop/prune/watch/warmup/selftest`）+ `cache-policy.json`（配额 / LRU / 必须绑模型指纹 / **只有完整匹配才算命中**）。复刻依据 = DeepSeek-V4 §3.6.2 *On-Disk KV Cache Storage* + LMCache（arXiv 2510.09665）。
- ⚠️ **501 不是 404**：没带 `--slot-save-path` 时端点回 **501 `not_supported_error`**；带了就 **200 可用**。判据 = `curl -o /dev/null -w "%{http_code}" -X POST "http://127.0.0.1:<port>/slots/0?action=save" -d '{"filename":"probe.bin"}'`，**别用「目录空」推断不可用**。
- ⚠️ **`n_saved:0` 的两个真因**：① `--cache-idle-slots` 默认开会清空空闲槽；② **请求不落 slot 0**（实测落 slot 3）⇒ 先 `GET /slots` 选 **`n_prompt_tokens` 最大**者，restore 优先找空闲槽。
- **诚实边界**：`/slots` 空闲时只给 `id/is_processing/n_ctx/speculative`，跑过任务后才多 `n_prompt_tokens*`，**没有 prompt、没有 token id 序列** ⇒ 键只能是近似（`sha256(模型指纹|token数|提示)[:16]`）⇒ **不做逐请求自动命中**，只做自动落盘；真命中需 Phase-2 外部代理（当前不做）。
- **体积模型**：`2 × n_layer × n_head_kv × head_dim × bytes_per_elem`（q8_0 ≈ 1.0625 B/elem）。27B 实测 **146,911 B/token**（≈143.5 KiB）；4B 78,352；9B 96,357。⇒ 16 GiB 单条上限 ≈ 114K token。
- 真机实测（2026-09-13 03:1x，部署后的 CLI 打线上 27B）：`--trim 8082` ⇒ **18.50 GiB → 1.1 MiB**（PID 75636），随后 `--bench 8082 256 32 2` ⇒ **pp 414 / tg 39.3 t/s，推理不降速**。
- 改 `--slot-save-path` / `cacheRam` 都是**重启服务才生效**，且会换配置指纹（性能台账按 `cfgId` 分桶会分裂）。
