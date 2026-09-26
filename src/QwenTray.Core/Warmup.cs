using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace QwenTray;

// ============================================================================
// L3 硬盘 KV 缓存的「启动预热」（2026-09-13，R1 选项 A 落地）
//
// 要解决的问题：llama-server 重启后显存里的 KV 全没了。之前 restore 只能靠人手动点，
// 于是"重启模型 ⇒ 长前缀从零重算"照旧。实测收益：同一条 1651 token 前缀，
//   erase 后重发 prompt_ms = 532.6 → **restore 回槽后 prompt_n = 4 / 48.1 ms（11×）**。
//
// 为什么这里**不**自己算指纹、不自己发 restore（即"为什么不内嵌、也不加代理"）：
//   ① 命中判定**不需要**我们做 —— restore 把 KV 写回 slot 后，llama-server 自己做 LCP
//      前缀匹配（它掌握真 token 序列）。我们只回答"把哪几条、放进哪个槽"。
//   ② 策略层（LRU 挑选 / 指纹绑定 / 配额 / 台账）已经在 E:\kv_cache\kvctl.py 里、
//      且带 35 条纯逻辑自检。在 C# 里再实现一遍指纹（sha256 的 json 序列化细节）只会
//      多一份**可能静默不一致**的实现 —— 预热算错指纹就是"永远不预热"且没人发现。
//   ⇒ 本文件只做两件事：**判定**（何时该预热，纯函数，单测穷举）+ **驱动**（跑 kvctl）。
//
// 安全闸门（与 kvctl 的 binding_check 同一套语义）：
//   源码 src/llama-context.cpp:3274 的 state_seq_load_file() 只校验 (magic, version) 与
//   token 数，**不校验模型 / KV 类型 / flash-attn**；错配会走到 GGML_ASSERT，最坏 abort
//   整个服务。所以"启动方"必须把自己用过的 KV 类型登记下来（_runtime.json），
//   预热前逐项比对：**不一致 ⇒ 拒**；**无从核对 ⇒ 自动路径也拒**。
// ============================================================================
public static class Warmup {

  // kvctl.py（策略层唯一源）与运行态登记的默认落点
  public const string DefaultKvctlPath   = @"E:\kv_cache\kvctl.py";
  public const string DefaultSlotSaveDir = @"E:\kv_cache\slots";
  public const string DefaultRuntimePath = @"E:\kv_cache\slots\_runtime.json";
  public const string DefaultLogPath     = @"E:\kv_cache\warmup.ndjson";

  /// <summary>_runtime.json 必须与 --slot-save-path 同目录（llama 只认那个目录，策略层与它并排最直观）。</summary>
  public static string RuntimePathFor(string slotSavePath) =>
    string.IsNullOrWhiteSpace(slotSavePath) ? "" : Path.Combine(slotSavePath, "_runtime.json");

  // ── 判定内核（纯函数）──────────────────────────────────────────────
  /// <summary>
  /// 该不该现在自动预热？返回 (allow, why)。规则**全是"宁可少做"**的方向：
  /// 开关关 / 有请求在处理 / 没条目 / 没空闲槽 任一命中即不放行。
  /// freeSlots 用调用方探到的**真实空槽数**（等于 --parallel 条里没人用的那些）——
  /// 有几条空槽就最多预热几条；没有空槽时**绝不**覆盖别人正在用的 KV。
  /// <para>
  /// ⚠️ entries / freeSlots 传 <b>负数</b> = "调用方不知道"，此时该条判据**不参与**
  /// （而不是被当成 0 拦掉）。托盘无从知道条目数与空槽数（前者要读 index.json、
  /// 后者要打 /slots），这两条由 kvctl 权威判定；托盘只负责它真正知道的
  /// 「开关」与「有没有请求在处理」。把"不知道"与"已知为 0"分开，
  /// 否则托盘侧的预判会**永远拦死**自动预热，而且日志上看起来像"没有条目"。
  /// </para>
  /// </summary>
  public static (bool allow, string why) Allow(bool enabled, bool anyProcessing, int entries, int freeSlots) {
    if (!enabled)             return (false, "开关关闭");
    if (anyProcessing)        return (false, "有请求在处理（预热会与推理抢显存带宽）");
    if (entries == 0)         return (false, "缓存里没有条目");
    if (freeSlots == 0)       return (false, "没有空闲槽（槽位全被占用）");
    return (true, "");
  }

  // ── 运行态登记（写 _runtime.json）────────────────────────────────
  /// <summary>
  /// 手工拼 JSON 而不用序列化器：键序稳定、字段名与 kvctl 的 BIND_FIELDS 一一对应，
  /// 出错时人能直接看出"哪一项没登记"。**必须**与 kvctl BIND_FIELDS 对齐：
  /// kvTypeK / kvTypeV / flashAttn / nCtx。
  /// </summary>
  public static string RuntimeJson(string model, string kvTypeK, string kvTypeV, string flashAttn,
                                   int nCtx, int pid, int port, string startedAt = "") {
    var sb = new StringBuilder();
    sb.Append("{");
    sb.Append("\"model\":\"").Append(JsonEscape(model)).Append("\",");
    sb.Append("\"kvTypeK\":\"").Append(JsonEscape(kvTypeK)).Append("\",");
    sb.Append("\"kvTypeV\":\"").Append(JsonEscape(kvTypeV)).Append("\",");
    sb.Append("\"flashAttn\":\"").Append(JsonEscape(flashAttn)).Append("\",");
    sb.Append("\"nCtx\":").Append(nCtx).Append(",");
    sb.Append("\"pid\":").Append(pid).Append(",");
    sb.Append("\"port\":").Append(port).Append(",");
    if (startedAt.Length == 0) startedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
    sb.Append("\"startedAt\":\"").Append(JsonEscape(startedAt)).Append("\",");
    sb.Append("\"writer\":\"dsh-tray\"");
    sb.Append("}");
    return sb.ToString();
  }

  /// <summary>JSON 字符串转义。Windows 路径里的反斜杠必须转义，否则写出的文件不是合法 JSON。</summary>
  public static string JsonEscape(string s) {
    if (string.IsNullOrEmpty(s)) return "";
    var sb = new StringBuilder(s.Length + 8);
    foreach (char c in s) {
      switch (c) {
        case '"':  sb.Append("\\\""); break;
        case '\\': sb.Append("\\\\"); break;
        case '\n': sb.Append("\\n");  break;
        case '\r': sb.Append("\\r");  break;
        case '\t': sb.Append("\\t");  break;
        default:
          if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
          else sb.Append(c);
          break;
      }
    }
    return sb.ToString();
  }

  /// <summary>把运行态写进 slot 目录。返回 (ok, err)。目录不存在会先建（与 --slot-save-path 的建目录同款理由）。</summary>
  public static bool WriteRuntime(string runtimePath, string json, out string err) {
    err = "";
    if (string.IsNullOrWhiteSpace(runtimePath)) { err = "运行态路径为空（未启用 --slot-save-path）"; return false; }
    try {
      string dir = Path.GetDirectoryName(runtimePath) ?? "";
      if (dir.Length > 0) Directory.CreateDirectory(dir);
      File.WriteAllText(runtimePath, json, new UTF8Encoding(false));
      return true;
    } catch (Exception ex) { err = ex.Message; return false; }
  }

  /// <summary>
  /// 由托盘的 KV 档位推出真实生效的类型名（用于登记）。
  /// ⚠️ 与 LaunchArgs.Build 是**同一份事实**的两处表达：kvMode 0 表示"不显式传参 ⇒ llama 默认 f16"，
  /// 登记时要写 f16 而不是 "default" —— 否则比对会永远 mismatch。
  /// </summary>
  public static (string k, string v) KvTypes(int kvMode, bool cpu) {
    if (cpu) return ("f16", "f16");          // CPU 模式不传 cache 类型
    if (kvMode == 1) return ("q8_0", "q8_0");
    if (kvMode == 2) return ("f16", "f16");
    return ("f16", "f16");                   // kvMode==0：llama 自身默认
  }

  // ── 结果解析/文案（纯函数）────────────────────────────────────────
  public sealed class Result {
    public bool ok;
    public int restored, attempted, entries;
    public string line = "";      // 给人看的一行
    public string raw  = "";      // 原始输出（排障用）
  }

  /// <summary>
  /// 解析 kvctl `warmup --json` 的单行 JSON（kvctl 可能夹前导日志行 ⇒ 从后往前找第一条**能被解析**的 JSON 行）。
  /// 解析失败时返回 ok=false 且 line 带原文，绝不抛。
  /// <para>
  /// 🔴 为什么必须用真解析器，不能手写扫描（2026-09-13 端到端实测踩到）：
  /// 第一版用 <c>IndexOf("\"restored\":")</c> + <c>Contains("\"ok\":true")</c> 取字段，而 kvctl 用的是
  /// python <c>json.dumps</c> 的**默认分隔符**（<c>", "</c> 与 <c>": "</c>）⇒ 实际输出是
  /// <c>"restored": 1</c> / <c>"ok": true</c>，**冒号后面有一个空格**。于是字段全部取不到：
  /// 整数恒 0、布尔恒 false —— 托盘对着"已恢复 1 条"报「未恢复（计划 0 条）/ RESULT NOOP」。
  /// 单测没拦住，是因为夹具照**自己实现的假设**写了紧凑 JSON，而不是照**真实契约**；
  /// 现在夹具改成下面 e2e 抓到的那两行**实拍原文**。
  /// 教训：这里解析的是**别人的**输出格式，任何"省一个依赖"的手写扫描都在赌对方的序列化细节。
  /// </para>
  /// </summary>
  public static Result ParseResult(string stdout) {
    var r = new Result { raw = stdout ?? "" };
    if (string.IsNullOrEmpty(stdout)) { r.line = "预热：无输出（kvctl 未产生结果）"; return r; }
    var lines = stdout.Replace("\r\n", "\n").Split('\n');
    JsonDocument? doc = null;
    for (int i = lines.Length - 1; i >= 0; i--) {
      string t = lines[i].Trim();
      if (t.Length < 2 || t[0] != '{' || t[^1] != '}') continue;
      try { doc = JsonDocument.Parse(t); break; } catch (JsonException) { /* 不是 JSON 行，继续往前找 */ }
    }
    if (doc == null) { r.line = "预热：输出里没有 JSON（原文：" + LastLine(stdout) + "）"; return r; }
    using (doc) {
      var root = doc.RootElement;
      r.restored = IntField(root, "restored");
      r.attempted = IntField(root, "attempted");
      r.ok = BoolField(root, "ok");
      r.line = r.restored > 0
        ? "预热：已恢复 " + r.restored + "/" + r.attempted + " 条到空闲槽" + SkipHint(root)
        : "预热：未恢复（计划 " + r.attempted + " 条）" + SkipHint(root);
    }
    return r;
  }

  /// <summary>skips[0].why 的原文（截断 90 字）：够人判断"是没条目 / 没空槽 / 绑定不一致"。</summary>
  static string SkipHint(JsonElement root) {
    if (!root.TryGetProperty("skips", out var sk) || sk.ValueKind != JsonValueKind.Array) return "";
    foreach (var s in sk.EnumerateArray()) {
      if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("why", out var w)
          && w.ValueKind == JsonValueKind.String) {
        string t = w.GetString() ?? "";
        return "  · " + (t.Length > 90 ? t.Substring(0, 90) + "…" : t);
      }
    }
    return "  · 无跳过原因";
  }

  static int IntField(JsonElement o, string key)
    => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
       && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;

  static bool BoolField(JsonElement o, string key)
    => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
       && v.ValueKind == JsonValueKind.True;

  static string LastLine(string s) {
    var t = s.TrimEnd('\r', '\n');
    int i = t.LastIndexOf('\n');
    string l = i >= 0 ? t.Substring(i + 1) : t;
    return l.Length > 120 ? l.Substring(0, 120) + "…" : l;
  }

  public static string ResultLine(bool ok, int restored, int attempted, string detail) =>
    (ok ? "[OK] " : "[--] ") + "预热 " + restored + "/" + attempted + " 条" + (detail.Length > 0 ? "  " + detail : "");

  public static string PolicyText(bool onReady) =>
    "启动预热 = " + (onReady ? "开（模型就绪后自动把最近条目恢复到空闲槽）" : "关（只能手动「立即预热」）");

  // ── python / kvctl 定位 ───────────────────────────────────────────
  /// <summary>候选 python 解释器：环境变量 DSH_PYTHON 优先，其次 PATH 上的 python，最后本机常见位置。</summary>
  public static List<string> PythonCandidates() {
    var l = new List<string>();
    string? env = Environment.GetEnvironmentVariable("DSH_PYTHON");
    if (!string.IsNullOrWhiteSpace(env)) l.Add(env!);
    l.Add("python.exe");
    l.Add("python");
    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    l.Add(Path.Combine(home, ".global-python", "Scripts", "python.exe"));
    l.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "Programs", "Python", "Python311", "python.exe"));
    return l;
  }

  /// <summary>纯选择逻辑：给"候选 + 存在性判定"⇒ 第一个可用者（便于单测，不碰真实 PATH）。</summary>
  public static string PickPython(IEnumerable<string> candidates, Func<string, bool> exists) {
    foreach (var c in candidates) if (!string.IsNullOrWhiteSpace(c) && exists(c)) return c;
    return "";
  }

  public static string KvctlPath() {
    string? env = Environment.GetEnvironmentVariable("DSH_KVCTL");
    return !string.IsNullOrWhiteSpace(env) ? env! : DefaultKvctlPath;
  }

  // ── 执行器（起 python 跑 kvctl warmup）───────────────────────────
  /// <summary>
  /// 执行一次预热。**不开托盘也能用**（`DSHTray.exe --warmup [port]` 走的就是这里）。
  /// 任何失败都收敛成"给人看的一行 + 原始输出"，绝不抛异常（预热失败不该拖垮托盘）。
  /// </summary>
  public static Result Run(int port, int top = 0, int timeoutMs = 180000) {
    var r = new Result();
    string py = PickPython(PythonCandidates(), p => {
      if (p.IndexOf('\\') < 0 && p.IndexOf('/') < 0) return true;   // PATH 上的名字：交给 Process 去解析
      return File.Exists(p);
    });
    if (py.Length == 0) { r.line = "预热不可用：找不到 python（设 DSH_PYTHON 或把 python 加进 PATH）"; return r; }
    string kvctl = KvctlPath();
    if (!File.Exists(kvctl)) { r.line = "预热不可用：找不到 " + kvctl + "（设 DSH_KVCTL 覆盖）"; return r; }

    var psi = new ProcessStartInfo(py) { UseShellExecute = false, RedirectStandardOutput = true,
                                         RedirectStandardError = true, CreateNoWindow = true,
                                         StandardOutputEncoding = Encoding.UTF8,
                                         StandardErrorEncoding = Encoding.UTF8 };
    psi.ArgumentList.Add(kvctl);
    psi.ArgumentList.Add("warmup");
    psi.ArgumentList.Add(port.ToString());
    if (top > 0) { psi.ArgumentList.Add("--top"); psi.ArgumentList.Add(top.ToString()); }
    psi.ArgumentList.Add("--json");
    try {
      using (var p = Process.Start(psi)) {
        if (p == null) { r.line = "预热不可用：进程未起"; return r; }
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } r.line = "预热超时（>" + (timeoutMs / 1000) + "s）"; r.raw = o; return r; }
        r.raw = e.Length > 0 ? (o + "\r\n[stderr] " + e) : o;
        var parsed = ParseResult(o);
        parsed.raw = r.raw;
        if (parsed.line.StartsWith("预热：无输出") && e.Length > 0) parsed.line = "预热失败：" + LastLine(e);
        return parsed;
      }
    } catch (Exception ex) { r.line = "预热异常：" + ex.Message; return r; }
  }
}
