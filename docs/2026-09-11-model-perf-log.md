---
created_at: 2026-09-11
update_at: 2026-09-11
summary: 配置粒度模型性能日志 —— 按启动参数指纹记录加载耗时与实测 pp/tg 速度，两层存储 + 四道闸门防膨胀
status: completed
priority: P1
---

# 配置粒度模型性能日志

## 一、要回答的问题

改了 `-ts` / `ctx` / MTP 档 / KV 类型 / `cache-ram` 之后，**到底哪套更快**？

原来的处境：`publish\model-start.log` 记了 26 行启动命令（2026-09-08 ~ 09-10），能看到反复试过
`ts=30,70 / 50,50`、`cacheRam=512/1024/2048/0`、`MTP=2/3/4`、`KV=q8_0/f16`，但**一行性能数据都没有** ——
那些实验的结论只存在脑子里，而且同一配置每次启动都重复追加一行（这就是"膨胀 + 冗余"的来源）。

## 二、数据源（全部实测，8081 上跑着 Qwen3.6-35B）

| 数据源 | 实测结果 | 成本 | 用途 |
|---|---|---|---|
| `llama-server` stdout | `prompt eval time = 2731.49 ms / 4696 tokens (… 1719.21 tokens per second)` / `eval time = … 65.71 tokens per second` | **零** —— 托盘本来就在收 stdout | `src=run`：真实 tg/pp 速度，**已含 MTP 投机解码收益** |
| `/v1/chat/completions` 响应体 | `timings`: `prompt_per_second` / `predicted_per_second` / `prompt_n` / `cache_n` | 一次请求 | `src=bench`：固定规模，跨配置严格可比 |
| `/props` | `build_info: b1-832fd6f`、`n_ctx: 262144`、`total_slots: 4` | 零（已有探测代码） | 版本归因 + 实测 ctx |
| 就绪耗时 | `Tick()` 里早就算好，原来只显示不落盘 | 零 | `src=start`：加载耗时 |

**两个反面结论（免得走弯路）**：

- `/slots` 在新版（`b1-832fd6f`）**已经没有 timing 字段**，只剩 `n_prompt_tokens` 等上下文占用 → 拿不到速度。
- `/metrics` 返回 **501 Not Implemented**（未带 `--metrics` 启动）→ 要它得改启动参数并重启模型，且上面的 stdout 行已经覆盖了需求。

## 三、设计

### 3.1 配置指纹 cfgId

```
cfgId = sha1( 排序后的启动参数 + llama 目录名 )[:10]
```

- **剔除 `--port` / `--host`**：不影响性能，改了不该算"新配置"（否则换个端口就多一条台账）。
- **参数顺序无关**（排序后参与哈希）。
- **llama 目录名（`llama-b10797-cuda12.4`）参与指纹**：升级 llama.cpp 会让同参数配置性能变化，
  不带上就会把"编译器变了"误读成"配置回退了"。`/props` 的 `build_info` 另存字段便于归因。
- 参数解析注意：`--cache-ram -1` 的 `-1` 是**值**不是选项；`--cont-batching` 是**flag**（不吞下一个 `--xxx`）。

### 3.2 存储布局

```
C:\Users\Landrom\.dsh\tools\model-perf\
  configs.json               配置台账（条数 = 改过多少种参数组合，天然有界）
  samples-YYYY-MM.ndjson     样本明细（按月分片）
```

`configs.json` 一条配置一份：

```json
{
  "id": "62d566325a", "name": "Qwen3.6-35B (icompact)", "port": 8081,
  "model": "Qwen3.6-35B-…-I-Compact.gguf",
  "llamaExe": "llama-b10797-cuda12.4", "llamaBuild": "b1-832fd6f",
  "opt": { "batch":"1024","cacheRam":"2G","ctx":"256K","flash":"on","kvK":"q8_0","kvV":"q8_0",
           "mtpN":"2","ngl":"99","split":"tensor","spec":"draft-mtp","ts":"50,50","ubatch":"1024" },
  "args": ["-m","…","-c","262144","…"],
  "env": "cvd=0,1;alr=internal",
  "firstSeen": "2026-09-08T15:43:20+08:00", "lastSeen": "2026-09-11T20:30:00+08:00",
  "runs": 5, "loadLast": 42, "loadMin": 38, "loadMax": 46, "servedCtx": 262144,
  "benchLast": { "ts":"…","runs":3,"ppTps":1146,"tgTps":88.2,"ttftMs":343.8,"promptN":378,"genN":64 },
  "benchBest": { … }
}
```

样本按 `src` 分三类，**配置正文不重复存**（只放 `cfg` 引用）：

```json
{"ts":"2026-09-11T20:41:03+08:00","cfg":"62d566325a","src":"start","port":8081,"result":"ok","loadMs":42,"servedCtx":262144,"build":"b1-832fd6f"}
{"ts":"2026-09-11T20:00:00+08:00","cfg":"62d566325a","src":"run","port":8081,"bucket":"2026-09-11T20",
 "pp":{"med":1146,"min":1100,"max":1146,"n":7,"tok":3500},
 "tg":{"med":88.2,"min":87.1,"max":88.4,"n":7,"tok":700}}
{"ts":"2026-09-11T21:02:11+08:00","cfg":"62d566325a","src":"bench","port":8081,"r":{"…":"见 PerfBenchResult"}}
```

### 3.3 四道闸门（防膨胀）

| 闸门 | 规则 |
|---|---|
| 同配置折叠 | 同一 cfgId 重复启动只 `runs++`，不新增样本行；`model-start.log` 只在**首次出现**该配置时写一行 |
| 按月分片 | `samples-YYYY-MM.ndjson`，天然可按月归档 |
| 总量上限 | 默认 20MB；超限把**最老分片压成 `.gz`**（永久保留模式下"省空间"与"不丢数据"同时成立，**只压不删**） |
| 短请求丢弃 | 生成 token < 8 的请求不采样（排除探针 / `max_tokens=1` 的噪声） |

### 3.4 三道降噪（防冗余）

1. **run 样本按小时聚合**：不是"每请求一行"，而是把该小时内所有请求的速度聚合成**一条中位数样本**（附 min/max/n）。
   一天最多 24 行/配置。
2. **配置正文只存一份**（`configs.json`），样本只引用 `cfg`。
3. **pp 样本要求 prompt ≥ 16 token**：太短的请求（缓存命中）会虚高，不参与 pp 统计。

## 四、使用

### 4.1 托盘

右键托盘 → **模型性能日志**（或打开日志窗口后切到第 3 个页签「模型性能」）。

| 列 | 含义 |
|---|---|
| 配置 / 服务 / 参数摘要 | `ctx256K/…/ts50,50/mtpN2`，一眼看出差异 |
| 启动 / 加载s | 启动次数、最近一次加载耗时 |
| pp t/s / tg t/s | 最近一条 `run` 样本的中位数（来自真实请求） |
| 基准tg | 历史最佳基准结果（来自「跑基准」） |

工具栏：**跑基准**（对选中配置的端口发固定规模请求，默认 1024/128×3，第 1 次作 warmup 丢弃）/
**刷新** / **导出 CSV** / **打开目录**；底部下拉可切基准规模（512/64、1024/128、2048/256、4096/128）。
双击选中行可在下方详情框看到完整参数与最近 12 条 run 样本。

### 4.2 命令行

```bat
DSHTray.exe --bench 8081                 :: 512/64×3，结果写入台账
DSHTray.exe --bench 8081 1024 128 3      :: 自定义 prompt / gen / 次数
DSHTray.exe --selftest-perf              :: 数据层自检（39 项，写 selftest-perf.txt）
```

> 命令行基准走与按钮完全相同的 `BenchRunner` → `RecordBenchResult` 路径。若该端口在台账里没有配置
> （服务是外部启动的），只输出结果、不记档。

### 4.3 直接分析文件

```bash
# 各配置的 run 中位数 tg 速度排名（当月）
jq -r 'select(.src=="run" and .tg) | [.cfg, (.tg.med|tostring)] | @tsv' samples-2026-09.ndjson \
  | awk '{s[$1]+=$2;n[$1]++} END{for(k in s) printf "%s\t%.1f\n",k,s[k]/n[k]}' | sort -k2 -rn
```

```python
import json, pandas as pd
rows = [json.loads(l) for l in open('samples-2026-09.ndjson', encoding='utf-8')]
df = pd.DataFrame([{**{k:v for k,v in r.items() if k not in ('pp','tg','r')},
                    'pp': (r.get('pp') or {}).get('med'),
                    'tg': (r.get('tg') or {}).get('med')} for r in rows])
```

## 五、验证记录（2026-09-11）

- **数据层自检 `--selftest-perf`：39/39 PASS**（指纹稳定性/忽略端口/ts 变化/版本变化、参数归一化负值与 flag、
  参数摘要、真实日志行解析、噪声行拒绝、台账新建与复用、聚合中位数、运行时门面、基准落盘、体积轮转、
  历史导入去重与幂等）。
- **日志窗口自检 `--selftest-logwin`：全 PASS**，其中 `TABS-3` / `PERF-TAB` 为本次新增断言。
- **真实基准（8081 · Qwen3.6-35B）**：
  ```
  run 1/3: pp  555 t/s · tg 87.3 t/s     ← warmup，冷启动，被丢弃
  run 2/3: pp 1100 t/s · tg 88.2 t/s
  run 3/3: pp 1146 t/s · tg 88.1 t/s
  BENCH OK  pp=1146.0 t/s  tg=88.2 t/s  TTFT=343.8 ms
  ```
- 编译 `0 error`；`ModelPerf.cs` 自身 `0 warning`（清掉了新引入的 `SYSLIB0014`）。
- 二进制验真：`publish-next\DSHTray.dll` 含 `PerfFingerprint/PerfStore/PerfSampler/BenchRunner/PerfRuntime/PerfProbe`
  与全部界面文案。
- `publish\dsh-tray-config.json` 全程未触碰（1743B / md5 `6fcead9419b6` 前后一致）。

## 六、运维

- **改体积上限 / 聚合窗口 / 基准规模**：编辑 `configs.json` 的 `settings`
  （`maxTotalBytes` / `bucketMinutes` / `minRunTokens` / `benchPromptTok` / `benchGenTok` / `benchRuns`）。
- **要重新导入老 `model-start.log`**：把 `configs.json` 里的 `legacyImported` 改回 `false` 后重启托盘。
  注意：导入读的是 **exe 所在目录**的 `model-start.log`（上线后在 `publish\` 下）。
- **`.gz` 分片**：是永久归档，`gzip -d` 或 `zcat` 可还原，不是删除。

## 七、已知边界

- 台账是**单实例内存 + 原子写文件**。同一进程内必须共用一个 `PerfStore`（`PerfPanel.Store` 与 `TrayApp.perf.Store`
  是同一个）；若在托盘运行期间手工编辑 `configs.json`，改动不会被托盘感知，下次写入会覆盖。
- `run` 数据依赖真实请求。模型空转时没有样本（这是特性不是缺陷 —— 用它当持续 benchmark 会污染数据）。
- `benchPromptTok` 是**按字符估算**（英文约 4 字符/token），实际 token 数以响应里的 `timings.prompt_n` 为准并记录。
