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
GPU: <选择>（复选） | 上下文: <N>K（单选，标题带当前值） | 切分模式：<当前>（单选）
── 分隔 ──
查看日志（服务日志窗口） | 打开配置文件
开机自启动 | 退出
```

- 规则：**目录/环境类动作放 DSH 二级菜单**（打开 DSH 程序目录、打开 .dsh 目录、查看 DSH 日志）；
  顶层只放高频操作与全局配置。
- 「推理参数组/切分模式/上下文/GPU」标题实时显示当前选中值（RefreshChecks() 统一刷新）。
- 不要加含义模糊的项（曾因「停止所选服务」语义不明被移除）。
- 验证：publish\DSHTray.exe --dump-menu → menu-dump.txt 核对结构与勾选/禁用态。

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
