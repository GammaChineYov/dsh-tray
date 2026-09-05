# DSH托盘 (DSHTray)

Windows 系统托盘应用，用于管理本机 **llama.cpp** 本地大模型服务（Qwen 系列），并在托盘悬浮展示每张 GPU 的实时状态。

## 功能

- **服务管理**：启动 / 停止 / 重启多个 llama-server 服务（通过配置文件可增删改，含端口、模型、mmproj、batch、provider）。
- **GPU 选择**（复选框，可多选）：全部（GPU）/ CPU / 单卡（GPU0、GPU1…）；启动时按所选部署：
  - 多卡 → `CUDA_VISIBLE_DEVICES=<idx,...>` + `--split-mode <layer|row>`（取「切分模式」的值）
  - 单卡 → `CUDA_VISIBLE_DEVICES=<idx>` + `--split-mode none`
  - CPU → `-ngl 0`（关闭 flash-attn / 量化 KV）
- **切分模式**（单选，仅多卡时生效）：按层切分 `layer`（推荐，无需 CUDA split buffers）/ 张量并行 `row`（需 split buffers 支持，用于摊开权重/KV 解锁更大上下文）。
- **上下文长度**（单选）：8K / 16K / 32K / 64K / 128K / 192K / 256K，启动时作为 `-c` 应用。
- **缓存内存**（单选）：512M / 1G / 2G / 4G / 无限制 / 禁用，启动时作为 `-cram/--cache-ram` 应用（prompt/前缀缓存占系统内存上限，MiB；0=禁用、-1=无限制）；重启服务生效。
- **模型监听地址**（复选，默认勾选）：勾选 = 绑定 `0.0.0.0`（局域网设备可直接访问模型端口）；取消 = 仅本机 `127.0.0.1`（llama-server 无鉴权，公网/不可信网络建议取消勾选）。下次启动服务生效。
- **推理参数组**（单选）：通用思考 / 编码思考 / Instruct 三组官方采样参数；菜单标题实时显示当前组（如「推理参数组：编码思考」）。
- **托盘悬浮（tooltip）**：运行中的模型行 + 每张 GPU 的 显存 GB / 占用 % / 温度 °C（nvidia-smi 每 5s）+ CPU 使用率 / 内存占用。
  - CPU 温度只在系统提供**会变化的真实读数**时显示（如 MSAcpi）；主板 ACPI 假恒温（如恒 30.1°C）会被识别并隐藏，避免显示错误数值。
- **打开 DSH 会话**：WebView2 嵌入 DSH Web（`http://127.0.0.1:3080/`），继承 markdown / 工具闭环 / 插件。
- **DSH 服务控制**（菜单最顶「DSH」）：直接 启动 / 重启 / 停止本机 DSH Web（端口 3080），带状态图标（绿=运行中 / 黄=启动中 / 红=未启动）。
- **日志与目录**：「查看日志」打开 llama 服务日志窗口；「查看 DSH 日志」打开专用日志窗口（实时跟踪 DSH 输出/错误日志，无日志文件时给出提示与路径）；「打开 DSH 程序目录」直达 DSH 安装目录（`dshWorkDir`）；「打开 .dsh 目录」直达用户 `~/.dsh`（日志/目录项均在 **DSH 二级菜单**内）。
- **打开官方会话 chat**：WebView2 打开 `https://chat.deepseek.com`，使用独立持久化用户数据目录（登录态跨重启保持）。

## 构建

依赖：
- .NET 8 SDK
- WebView2 Runtime（WinForms 运行时）
- `nvidia-smi`（GPU 状态；无 NVIDIA 卡时仅显示 CPU 模式）

```powershell
dotnet publish -c Release -o publish
```

运行：`publish\DSHTray.exe`（右键托盘图标使用；桌面生成快捷方式可指向它）。

## 配置文件

首次运行在 **exe 同目录**生成 `dsh-tray-config.json`，可编辑：

| 字段 | 说明 |
|------|------|
| `llamaServerExe` | llama-server 可执行文件路径 |
| `dshUrl` | DSH Web 地址 |
| `settingsYamlPath` | DSH `settings.yaml` 路径（打开会话时临时改写 `agent-default-model`） |
| `officialDeepSeekUrl` | 官方会话地址 |
| `dshNodeExe` | 启动 DSH 用的 node.exe 完整路径（「DSH」菜单 启动/重启 用） |
| `dshCliBinJs` | DSH cli 入口，如 `<harness-checkout>\apps\cli\lib\bin.js` |
| `dshWorkDir` | DSH 工作目录（harness checkout） |
| `dshHomeDir` | `.dsh` 目录（托盘「打开 .dsh 目录」用；留空 = 默认 `%USERPROFILE%\.dsh`，DSH_HOME 非默认时填真实路径） |
| `dshOutLog` / `dshErrLog` | DSH stdout/stderr 日志路径（留空 = exe 同目录 `dsh-web-out.log` / `dsh-web-err.log`） |
| `services[]` | 每个服务的 `name/port/model/useMmproj/mmproj/batch/ubatch/specDecode/provider/enabled` |

运行时状态（GPU 选择、ctx、KV 缓存、缓存内存、参数组、切分模式、监听地址）存 `dsh-tray.cfg`（`paramMode` / `gpu` / `ctx` / `kv` / `cacheRam` / `split` / `bind`），自动读写。

> 提示：修改 `dsh-tray-config.json` 后**重启托盘**生效；托盘菜单「打开配置文件」可直接打开该文件编辑。

## 说明

- 托盘只管理**自己启动**的进程（端口监听检测），外部启动的服务会提示"已在跑（非本应用）"。
- 多卡无 NVLink 时，张量并行主要是摊开权重 / KV、解锁更大上下文，解码仍受显存带宽限制。
- 首次请求有 CUDA JIT 暖机（约 20-30s），之后正常。
