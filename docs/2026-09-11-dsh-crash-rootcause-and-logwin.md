---
created_at: 2026-09-11
summary: dsh「老是自己挂」的根因定位（WorkBuddy safe-delete 守卫顺着 NODE_OPTIONS 污染长驻 dsh 子进程 → 释放 profiles\node_modules.lock 失败 → 连锁 4 次等锁崩溃）与两条修复链，外加托盘日志窗口统一重构
status: completed
priority: P0
---

# dsh 频繁崩溃根因定位 + 托盘日志窗口统一重构

## 一、结论摘要

`dsh 老是挂` 是**两类独立故障**叠加，实测崩溃率约 **每 4 次启动崩 1 次**（`dsh-web-out.log` 23 次成功启动 / `dsh-web-err.log` 7 次崩溃）。

| # | 故障 | 次数 | 判据 |
|---|---|---|---|
| A | `atomic-write: timed out waiting for the writer lock at …\profiles\node_modules.lock` | 4 | err 日志 160/171/182/193 行 |
| B | `EADDRINUSE: address already in use 127.0.0.1:3080` | 2 | err 日志 15/83 行 |
| A 的**触发者** | `[safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED] {"count":50,"threshold":50,"scope":"turn","targets":["…node_modules.lock"],"targetCount":1}` | 1 | err 日志 **145 行**（紧接在 4 次锁超时之前） |

关键发现：**A 的锁不是"启动中途被 Kill"留下的，而是"释放锁的那次 `rm` 被拦下了"** —— 145 行与 160 行的先后顺序就是因果链。

## 二、链 A 机制（真凶）

1. 托盘若在 **WorkBuddy(CodeBuddy) 血统的进程**里被拉起，`DshStart` 起的 node 子进程会**原样继承** `NODE_OPTIONS`（`--require …\cli\vendor\shim\node-language-shim.cjs`）。该 shim 见 `CODEBUDDY_SESSION_ID` 即激活，并挂上 `node-safe-delete-shim.cjs`。
2. 该 shim 内置**按 turn 统计、默认阈值 50 次删除**的批量守卫，判定 turn 的依据是 `CODEBUDDY_TOOL_CALL_ID`。本机实测环境：`CODEBUDDY_SAFE_DELETE_BULK_THRESHOLD=50`、`CODEBUDDY_SAFE_DELETE_ENABLED=1`、`CODEBUDDY_TOOL_CALL_ID=call_01_OF25…`，**共 51 个 `CODEBUDDY_*` 变量**。
3. **dsh 是长驻进程**，那个 `tool_call_id` 在进程启动时就冻死了 → turn 永不滚动 → **删除计数只涨不归零** → 删到第 50 次后**该进程内所有删除永久被拒**。
4. 中招的正是 `composeProfile → healProfilesModuleFallback` 释放 `profiles\node_modules.lock` 的 `rm` → 异常抛出 → **锁留在盘上** → 之后每次启动 `atomic-write` 死等 2s 超时崩溃（`packages/util/atomic-write` 按设计不回收陈旧锁）。

这同时解释了为什么 `fix-dsh-lock` 自愈脚本"易复发"：只要托盘血统对了，dsh 就带着一颗 50 次删除的定时炸弹在跑。

## 三、链 B 机制

托盘自身有 `DshUp()` 前置判断，但**桌面 `DeepSeek Harness.cmd` 与 `dsh-web-server.cmd` 都没有**。托盘已跑 dsh 时再双击桌面入口 → 无条件再起一份 → 新实例 `listen EADDRINUSE` 秒退（`dsh-web-server.cmd` 只调 `fix-dsh-lock.cmd` 清锁，从没探过端口）。用户视角即"点了没反应 / dsh 又崩了"。

## 四、修复清单

### 1. dsh 稳定性（`C:\Users\Landrom\dsh-chat-popup\Program.cs`）

| 位置 | 改动 |
|---|---|
| `TrayApp.ScrubWorkBuddyEnv(ProcessStartInfo)` | 新方法。摘掉全部 `CODEBUDDY_*` 与 `CLAUDE_SESSION_ID`；调 `StripWorkBuddyShim` **按 token** 剔除 `NODE_OPTIONS` 里指向 WorkBuddy/CodeBuddy shim 的 `--require`；置 `CODEBUDDY_SAFE_DELETE_ENABLED=0` 双保险。⚠️ **不整条删 `NODE_OPTIONS`** —— 用户要用 `--use-system-ca` 走企业网 TLS |
| `TrayApp.StripWorkBuddyShim(string)` | 正则 `--require\s*=\s*("[^"]*"\|'[^']*'\|\S+)` 逐个 token 判定，只删命中 shim 名的 |
| `TrayApp.Stamp1Line(string)` | dsh 子进程 stdout/stderr **落盘逐行前置 `[HH:mm:ss]`**（原来裸写 `e.Data`，整份 out/err 无时间信息） |
| `DshStart()` | 建档 `envNote` → `psi` 后立刻 `ScrubWorkBuddyEnv(psi)` → 启动日志行追加 `| 环境清洗: …` |

### 2. 端口占用入口互斥（两个 `.cmd`，字节级追加、纯 ASCII）

`dsh-web-server.cmd` 与桌面 `DeepSeek Harness.cmd` 在 `cd` 之后插入：

```bat
netstat -ano | findstr ":3080 " | findstr "LISTENING" >nul 2>nul
if not errorlevel 1 goto already
```

命中则 `goto already`：只打开浏览器、**不再起第二份 dsh**、`exit /b 0`。

⚠️ 追加 `:label` 时同步修掉了它引入的流程漏洞：原文件末尾就是 `pause`，标签直接续在后面会让**服务正常退出后按任意键掉进 `:already`**（桌面那个是 `:fail` 之后掉进去）。已在两处 `pause` 后补 `exit /b 0` / `exit /b 1`。

### 3. 日志窗口统一重构（`LogForm`）

原状：`LogForm` 是同一个类，却被当**两个窗口**用（`logForm` 托盘事件 / `dshLogForm` DSH 输出），每个只有一个裸 `RichTextBox`；`Append` 即裸 `AppendText` —— **无清空按钮、无时间戳、无自动滚动**。

现版本：**单窗口 + 双页签**（`托盘事件` / `DSH 输出`）+ 一条共享 `ToolStrip`；菜单两个入口都打开同一窗口，仅切页签。

| 元素 | 语义 |
|---|---|
| `Append(s)` | 托盘事件页，入库即打**真实** `[HH:mm:ss]` |
| `AppendDsh(s)` | DSH 页文件内容 —— 已有戳原样保留，无戳老行补 `[--:--:--]`（标示"时间未知"，不伪造） |
| `AppendDshStamp(s)` | DSH 页中托盘自己生成的行（载入标题、清空摘要）→ 打真实戳 |
| 清屏 | 只清窗口文本，**不动磁盘文件** |
| 清空日志文件 | 截断 out/err 各留一行 `=== 时间 由托盘清空（原 N 字节）===`，并把 `lastOutPos/lastErrPos` 归零 |
| 复制全部 / 打开日志文件夹 / 自动滚动 / 暂停刷新 | 暂停时按页签缓冲（上限 3000 条），取消暂停后补录 |

配套两个**必须一起改**的点：
- 托盘**落盘打戳**（`Stamp1Line`），否则 DSH 日志永远没有时间信息；
- `TailLog` 加 `if(len<pos) pos=0;` —— 文件被"清空日志文件"截断后若不回退，跟踪指针会**永远停在被截断前的旧 EOF 上、静默失效**。

顺带修掉：`OpenDshLog` 重开时不清屏导致的**内容重复堆叠**；删掉 `dshLogForm` 字段（`ShowLogWinAt(bool)` + `MakeLogForm()` 统一入口并注入 `LogDirProvider` / `ClearFilesAction` 委托）；`AutoScroll` 字段改名 `AutoFollow`（原名隐藏 `Form.AutoScroll`，触发 `CS0108`）。

## 五、验证清单（全部实测）

| 项 | 方法 | 结果 |
|---|---|---|
| 编译 | `dotnet build -t:Rebuild -c Release` | **0 error**；`CS0108` 与我引入的可空赋值警告均已消除 |
| 二进制验真 | Python 检索 UTF-16 常量 | `publish-next\DSHTray.dll` 含 `清空日志文件/清屏/暂停刷新/自动滚动/CODEBUDDY_SAFE_DELETE_ENABLED/CLAUDE_SESSION_ID/--require/node-language-shim`；`AutoFollow` 在、`AutoScroll` 已不在 |
| 日志窗口功能 | `DSHTray.exe --selftest-logwin` | `TRAY-TIMESTAMP` / `LEGACY-MARKED` / `STAMP-PRESERVED` / `CLEAR-BUTTONS` / `TABS-2` **全 PASS** |
| 环境清洗 | 同上自检内置样本 | `KEEP-USE-SYSTEM-CA` / `DROP-WB-SHIM` / `DROP-SESSION-ID` / `SAFE-DELETE-OFF` **全 PASS** |
| 端口判据 | `netstat -ano \| findstr ":3080 " \| findstr "LISTENING"` | 3080 空闲 rc=1（不跳转，正常启动）；对照 8080 在监听 rc=0（跳 `:already`）—— **两边取值都验过** |

## 六、上线步骤（用户动作）

构建已发布到 `publish-next`（**没有动正在跑的 `publish\`**）。

1. 双击桌面 **`apply-dsh-tray.cmd`**（UAC 自提权 → 校验源构建 → 请旧托盘优雅退出（保留模型）→ robocopy → 启新托盘）。
2. 上线后验证：托盘右键 → `DSH` → **查看 DSH 日志**，应看到单一窗口、两个页签、顶部工具栏（清屏 / 清空日志文件 / 复制全部 / 打开日志文件夹 / 自动滚动 / 暂停刷新），且每行都有 `[HH:mm:ss]`。
3. 可选：托盘右键 → `DSH` → `启动 DSH`，观察启动日志行末尾出现 `| 环境清洗: 已摘除 CODEBUDDY_… / NODE_OPTIONS:shim`。

## 七、备份与回滚

| 文件 | 备份 |
|---|---|
| `Program.cs` | `Program.cs.bak-logrefactor-20260911-2015` |
| 桌面 `DeepSeek Harness.cmd` | `DeepSeek Harness.cmd.bak-portguard-<时间戳>` |
| `dsh-web-server.cmd` | `dsh-web-server.cmd.bak-portguard-<时间戳>` |
| skill `dsh-tray-dev/SKILL.md` | `SKILL.md.bak-<时间戳>` |

`publish\dsh-tray-config.json` **全程未被触碰**（1743B / md5 `6fcead9419b6`，切换前后一致）。已顺手清掉 `publish-next` 里残留的**默认配置模板**（1461B，占位符版，`apply` 的 `/XF` 也会排除它）。

## 八、遗留

- `dsh-tray-config.json` 的 `DshNodeExe` 仍硬编码 node `22.22.2-2`，WorkBuddy 升级 node 版本目录即失效，宜改读 `versions/current`。
- 真托盘提权原因未定。
- `Program.cs` 仍有 31 条历史警告（可空引用为主），未在本次范围。
