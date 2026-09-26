using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace QwenTray;

// ============================================================================
// 「启动预热」的托盘接线（2026-09-13，R1 选项 A）。TrayApp 的 partial 之一。
//
// 为什么单独一个文件而不是塞进 TrayApp.cs / TrayApp.Lifecycle.cs：
//   这两个文件已经分别到 50KB / 46KB，且每加一个功能都要改它们 ⇒ 冲突面越来越大。
//   本文件是**纯增量**（除菜单装配外零修改），把"热点的第二个写者"隔离出来。
//
// 分工（与 MemTrim 完全同构，便于对照排查）：
//   · 判定 = QwenTray.Core/Warmup.cs 的纯函数（单测穷举）
//   · 执行 = Core/Warmup.Run() 起 python 跑 E:\kv_cache\kvctl.py warmup <port> --json
//   · 本文件只负责"什么时候跑、跑完写哪行日志、菜单怎么显示"
// ============================================================================
public partial class TrayApp {

  // —— 菜单项与开关 ——
  // 默认**开**：这就是"选项 A"的全部意义 —— 重启模型后不必有人手动去恢复前缀。
  ToolStripMenuItem? warmupOnReadyItem, warmupNowItem, warmupStatusItem;
  bool warmupOnReady = true;
  int _warmupBusy = 0;   // 同一时刻只允许一次预热在跑（起 python 是秒级活）

  void ToggleWarmupOnReady() {
    warmupOnReady = !warmupOnReady;
    RefreshChecks(); SaveCfg();
    logForm.Append("模型就绪后预热最近 KV: " + (warmupOnReady ? "开" : "关") + "\r\n");
  }

  /// <summary>
  /// 状态行文案。抽成方法而不是内联：它要区分"关着/还没跑过/跑过什么"，
  /// 而菜单文案必须能预测行为（标签与行为不一致 = 执行鸿沟）。
  /// </summary>
  string WarmupStatusText() {
    if (!warmupOnReady) return "仅手动";
    foreach (var s in services) if (s.lastWarmupLine.Length > 0) return s.Name + " · " + s.lastWarmupLine;
    return "就绪后自动（本次尚未执行）";
  }

  /// <summary>
  /// 对一个服务预热一次。auto=true 表示来自"就绪"事件 ⇒ 先过判定内核（有请求在处理直接放弃）。
  /// <para>
  /// ⚠️ 这里**不**替 kvctl 判"有没有条目 / 有没有空槽"：那两件事要读 index.json 与打 /slots，
  /// 让权威侧（kvctl 的 warmup_plan）判，托盘传 -1 = "未知，不参与判定"。
  /// 否则托盘会用自己猜的 0 把自动预热永远拦死，且日志上看起来像"没有条目"（误导）。
  /// </para>
  /// </summary>
  void WarmupOne(Service svc, bool auto) {
    if (auto) {
      var g = Warmup.Allow(warmupOnReady, SvcProbe.Busy(svc.Port), -1, -1);
      if (!g.allow) { svc.lastWarmupLine = "预热跳过：" + g.why; Ui(() => RefreshChecks()); return; }
    }
    var r = Warmup.Run(svc.Port, 0);
    svc.lastWarmupLine = r.line;
    AppendWarmupLog(auto, svc.Name, svc.Port, r);
    logForm.Append("[" + svc.Name + "] " + r.line + "\r\n");
    Ui(() => RefreshChecks());
  }

  // 预热台账（ndjson，一行一次）：与回收台账同款 —— 用户据此判断"自动预热到底有没有在跑、恢复了什么"。
  // 没有它，自动预热失败时用户只会看到"菜单上什么都没变"，无法区分"没跑"与"跑了但没命中"。
  readonly object _warmupLogLock = new object();
  void AppendWarmupLog(bool auto, string name, int port, Warmup.Result r) {
    try {
      string dir = Path.GetDirectoryName(Warmup.DefaultLogPath) ?? "";
      if (dir.Length == 0) return;
      Directory.CreateDirectory(dir);
      var line = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>{
        ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        ["event"] = "kv-warmup", ["auto"] = auto, ["service"] = name, ["port"] = port,
        ["ok"] = r.ok, ["restored"] = r.restored, ["attempted"] = r.attempted, ["line"] = r.line,
      });
      lock (_warmupLogLock) File.AppendAllText(Warmup.DefaultLogPath, line + "\n");
    } catch { }
  }

  /// <summary>
  /// 启动预热节拍（在 1s 心跳上调用）。与 TrimTick 同款"打标—消费"：
  /// 就绪那一刻只**打标**（探活线程还要接着探别的服务），真正执行放到这里并丢后台线程。
  /// 与 trim 的差别：trim 是清页（毫秒级实测 120ms 等待），预热要起 python 子进程（秒级）⇒ 更不能挂在探活线程上。
  /// </summary>
  void WarmupTick() {
    foreach (var svc in services) {
      if (!svc.pendingWarmupOnReady) continue;
      if (!warmupOnReady) { svc.pendingWarmupOnReady = false; continue; }   // 关着就别留标志（重启一次服务不该"补做"）
      if (System.Threading.Interlocked.Exchange(ref _warmupBusy, 1) == 1) return;  // 已有预热在跑 ⇒ 下个节拍再来（标志不清，不会丢）
      svc.pendingWarmupOnReady = false; var t = svc;
      Bg(() => { try { WarmupOne(t, true); } finally { System.Threading.Interlocked.Exchange(ref _warmupBusy, 0); } });
    }
  }

  // 隐藏自检模式：--selftest-warmup（启动预热：判定矩阵 + 运行态登记 + kvctl 可达性 + 菜单接线）
  // L1（可自动跑）：不弹菜单、不碰鼠标、不动真实服务（会写一次 _runtime.json —— 那正是被测对象）。
  // L2（live）：真跑一次 kvctl warmup —— 这是"判据在真环境成立"的那一半，需显式索要。
  public string WarmupProbe(bool live) {
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("KV WARMUP PROBE (ascii-anchor; selftest-warmup" + (live ? " live" : "") + ")");
    sb.AppendLine("impl: src/QwenTray.Core/Warmup.cs + TrayApp.Warmup.cs | unit: tests/WarmupTests.cs | policy: E:\\kv_cache\\kvctl.py");
    int pass = 0, fail = 0;
    void Check(string name, bool ok, string detail = "") {
      if (ok) pass++; else fail++;
      sb.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail.Length > 0 ? ("   | " + detail) : ""));
    }
    try {
      sb.AppendLine();
      sb.AppendLine("=== ① 判定矩阵（纯函数；与 WarmupTests 同一组判据）===");
      Check("正常放行：开 + 无请求 + 有条目 + 有空槽", Warmup.Allow(true, false, 3, 2).allow);
      Check("负向：开关关 ⇒ 不放行", !Warmup.Allow(false, false, 3, 2).allow);
      Check("负向：有请求在处理 ⇒ 不放行（预热会与推理抢显存带宽）", !Warmup.Allow(true, true, 3, 2).allow);
      Check("负向：条目为 0 ⇒ 不放行", !Warmup.Allow(true, false, 0, 2).allow);
      Check("负向：空槽为 0 ⇒ 不放行（绝不覆盖别人正在用的 KV）", !Warmup.Allow(true, false, 3, 0).allow);
      Check("未知项(-1)不参与判定：托盘不知道条目/空槽数时仍可放行",
            Warmup.Allow(true, false, -1, -1).allow);
      Check("但「未知」不等于放行一切：开关关/有请求时 -1 照样拦",
            !Warmup.Allow(false, false, -1, -1).allow && !Warmup.Allow(true, true, -1, -1).allow);

      sb.AppendLine();
      sb.AppendLine("=== ② 运行态登记（_runtime.json：KV 类型/flash-attn 的唯一来源）===");
      var t0 = Warmup.KvTypes(0, false);
      var t1 = Warmup.KvTypes(1, false);
      Check("kvMode 0 ⇒ f16/f16（llama 默认；不能写成 default，否则比对永远 mismatch）", t0.k == "f16" && t0.v == "f16");
      Check("kvMode 1 ⇒ q8_0/q8_0", t1.k == "q8_0" && t1.v == "q8_0");
      Check("CPU 模式 ⇒ 不传 cache 类型 ⇒ f16", Warmup.KvTypes(1, true).k == "f16");
      string rj = Warmup.RuntimeJson(@"E:\models\x.gguf", "q8_0", "q8_0", "on", 262144, 1234, 8082);
      Check("路径反斜杠被转义（否则写出的不是合法 JSON）", rj.Contains(@"E:\\models\\x.gguf"), rj.Substring(0, Math.Min(90, rj.Length)));
      Check("四个绑定字段齐全（与 kvctl BIND_FIELDS 对齐）",
            rj.Contains("\"kvTypeK\"") && rj.Contains("\"kvTypeV\"") && rj.Contains("\"flashAttn\"")
            && rj.Contains("\"nCtx\"") && rj.Contains("\"pid\""));
      string rtPath = Warmup.RuntimePathFor(LaunchArgs.DefaultSlotSavePath);
      Check("运行态与 --slot-save-path 同目录", rtPath == Warmup.DefaultRuntimePath, rtPath);
      // ⚠️ 探针**绝不能**写真实的 _runtime.json：上面那个 rj 是**假签名**（pid=1234、假模型名），
      // 覆盖上去就等于把绑定校验这道闸门**自己拆掉** —— 之后的自动预热会拿假签名放行旧条目，
      // 而错配的 restore 会走到 GGML_ASSERT。所以往返测试只写临时目录，真实文件**只读回显**。
      string tmpRt = Path.Combine(Path.GetTempPath(), "qwentray-warmup-probe.json");
      string werr = ""; bool wok = Warmup.WriteRuntime(tmpRt, rj, out werr);
      Check("运行态可落盘（临时文件往返；不碰真实证据）", wok, werr);
      try {
        if (File.Exists(rtPath)) {
          sb.AppendLine("  真实运行态 = " + rtPath);
          sb.AppendLine("    " + File.ReadAllText(rtPath).Trim());
          Check("真实运行态是合法 JSON（人工可核 KV 类型/flash-attn）",
                File.ReadAllText(rtPath).TrimStart().StartsWith("{"));
        } else {
          sb.AppendLine("  真实运行态 = (尚未生成：" + rtPath + " —— 由托盘在启动模型时写)");
        }
      } catch { }
      try { File.Delete(tmpRt); } catch { }

      sb.AppendLine();
      sb.AppendLine("=== ②b 同一事实两处表达的漂移闸门（LaunchArgs.Build ↔ Warmup.KvTypes）===");
      // 🔑 为什么必须打这一条：KV 类型在代码里有**两处**表达 —— 启动参数由 LaunchArgs.Build 拼，
      //    运行态签名由 Warmup.KvTypes 给。二者一旦漂移，后果不是「报错」而是**静默错配**：
      //    登记说 f16、实际跑 q8_0 ⇒ binding_check 判「一致」⇒ 拿错类型的条目 restore
      //    ⇒ 走到 GGML_ASSERT（最坏 abort 掉整个服务）。所以它必须是一条可断言的不变量。
      string ArgValue(List<string> a, string name) {
        int i = a.IndexOf(name);
        return (i >= 0 && i + 1 < a.Count) ? a[i + 1] : "";
      }
      var cSpec = new ServiceSpec { Model = @"E:\m\x.gguf" };
      foreach (var kv in new[] { 0, 1, 2 }) {
        var aCpu = LaunchArgs.Build(cSpec, GpuSelection.FromCfg("cpu"), 262144, 1, 0, kv, 512, 50, 2, true, 0, @"E:\kv_cache\slots").args;
        var aGpu = LaunchArgs.Build(cSpec, GpuSelection.FromCfg("all"), 262144, 1, 0, kv, 512, 50, 2, true, 0, @"E:\kv_cache\slots").args;
        var kCpu = Warmup.KvTypes(kv, true); var kGpu = Warmup.KvTypes(kv, false);
        string gk = ArgValue(aGpu, "--cache-type-k"), gv = ArgValue(aGpu, "--cache-type-v");
        // kvMode==0 时 Build 刻意**不写** cache 类型（用 llama 默认 f16）⇒ 登记也必须写 f16 而不是 "default"
        bool gpuAgree = kv == 0 ? (gk.Length == 0 && kGpu.k == "f16") : (gk == kGpu.k && gv == kGpu.v);
        Check("kvMode=" + kv + "：Build 实参与登记运行态一致（GPU 模式）", gpuAgree,
              "args=" + (gk.Length == 0 ? "(未指定⇒llama 默认)" : gk) + " / 登记=" + kGpu.k);
        Check("kvMode=" + kv + "：CPU 模式不写 cache 类型且登记为 f16",
              ArgValue(aCpu, "--cache-type-k").Length == 0 && kCpu.k == "f16");
      }
      // flash-attn 与量化 V 的**源码级**约束：量化 V cache 强制要求 flash-attn
      // （src/llama-context.cpp:3775：AUTO 会自动升级、DISABLED 直接报错返回 nullptr）
      // ⇒ 只要开了量化 KV，"flashAttn=on" 就必须为真，否则服务根本起不来；这条钉住它别被改回去。
      foreach (var kv in new[] { 1 }) {
        var a = LaunchArgs.Build(cSpec, GpuSelection.FromCfg("all"), 262144, 1, 0, kv, 512, 50, 2, true, 0, @"E:\kv_cache\slots").args;
        var k = Warmup.KvTypes(kv, false);
        Check("量化 KV 必然带 --flash-attn on（否则 llama-server 起不来）",
              k.k != "f16" ? ArgValue(a, "--flash-attn") == "on" : true,
              "kv=" + k.k + " flash-attn=" + ArgValue(a, "--flash-attn"));
      }

      sb.AppendLine();
      sb.AppendLine("=== ③ kvctl 可达性（自动预热 = 起 python 跑 kvctl，这条路必须通）===");
      string py = Warmup.PickPython(Warmup.PythonCandidates(),
                                    p => p.IndexOf('\\') < 0 ? true : File.Exists(p));
      sb.AppendLine("  python  = " + (py.Length > 0 ? py : "(未找到)"));
      sb.AppendLine("  kvctl   = " + Warmup.KvctlPath() + (File.Exists(Warmup.KvctlPath()) ? "  [存在]" : "  [缺失]"));
      sb.AppendLine("  运行态  = " + rtPath);
      sb.AppendLine("  台账    = " + Warmup.DefaultLogPath);
      Check("找到 python（否则自动预热静默失效 —— 这是本功能最可能的失效方式）", py.Length > 0);
      Check("kvctl.py 存在", File.Exists(Warmup.KvctlPath()));

      sb.AppendLine();
      sb.AppendLine("=== ④ 菜单接线（结构断言；不点、不弹）===");
      Check("「模型就绪后预热最近 KV」开关存在", warmupOnReadyItem != null);
      Check("「立即预热」项存在", warmupNowItem != null);
      Check("预热状态行存在", warmupStatusItem != null);
      Check("预热开关挂在「内存与缓存」下拉里", trimMenu != null && warmupOnReadyItem != null
            && trimMenu.DropDownItems.Contains(warmupOnReadyItem));
      Check("预热开关已登记为①类（点了不收起菜单）", warmupOnReadyItem != null
            && trimMenu != null && _keep.IsState(trimMenu.DropDown, warmupOnReadyItem));

      sb.AppendLine();
      sb.AppendLine("=== ⑤ 当前生效策略（读内存态，不是常量）===");
      sb.AppendLine("  " + Warmup.PolicyText(warmupOnReady));
      sb.AppendLine("  硬盘 KV 缓存 = " + (slotSaveOn ? "开" : "关") + "（预热的前提：没有 --slot-save-path 就没有条目）");
      sb.AppendLine("  状态行 = " + WarmupStatusText());
      if (!slotSaveOn) sb.AppendLine("  ⚠️ 硬盘 KV 缓存关着 ⇒ 预热必然「没有条目」（先打开它，再重启模型）");

      sb.AppendLine();
      sb.AppendLine("=== ⑥ 实测预热（" + (live ? "已索要 live" : "默认跳过；加 live 参数才执行") + "）===");
      if (live) {
        int runN = 0; foreach (var s in services) if (SvcProbe.HealthUp(s.Port)) runN++;
        if (runN == 0) sb.AppendLine("  无运行中的模型 —— 跳过");
        else {
          var up = services.Find(s => SvcProbe.HealthUp(s.Port))!;
          var r = Warmup.Run(up.Port, 0);
          sb.AppendLine("  " + r.line);
          Check("实测：走到了 kvctl（有回执即说明 python 与脚本都通）",
                r.line.Length > 0 && !r.line.StartsWith("预热不可用") && !r.line.StartsWith("预热异常"), r.line);
        }
      } else {
        sb.AppendLine("  （跳过。要真预热请跑：DSHTray.exe --selftest-warmup live）");
      }
      sb.AppendLine();
      sb.AppendLine("  也可以不开托盘直接预热：DSHTray.exe --warmup [port]  →  selftest-warmup.txt");
    } catch (Exception ex) { fail++; sb.AppendLine("[FAIL] PROBE EX: " + ex.Message + "\r\n" + ex.StackTrace); }
    sb.AppendLine();
    sb.AppendLine("SUMMARY pass=" + pass + " fail=" + fail);
    sb.AppendLine(fail == 0 ? "RESULT PASS" : "RESULT FAIL");
    return sb.ToString();
  }
}
