using System;
using System.Collections.Generic;
using System.Text;

namespace QwenTray;

// ============================================================================
// LaunchPlan（2026-09-20）：把「服务项的覆盖」与「托盘内置模板」合成成**最终要执行的东西**。
//
// 要解决的问题：托盘的启动面原先只有一个全局 exe（AppConfig.LlamaServerExe）与一套写死的
// llama-server 参数模板（LaunchArgs.Build）。但本机上已经存在**第二条 llama 家族**——
// llm-deploy 的 kvmem 移植（`llama-kvmem-server.exe`），它换的是 exe、参数方言
// （`--kv-dtype` / `--kvmem-*` 而非 `--cache-type-k/v`）、以及四个 CUDA 环境变量。
// 三样都得能按**服务项**覆盖，它才可能作为一条服务出现在托盘里。
//
// 为什么单独一个文件、而不是塞进 LaunchArgs.Build：
//   LaunchArgs.Build 的职责是「把托盘参数翻译成上游 llama-server 的 argv」，它对 kvmem 一无所知、
//   也不该知道；一塞进去就要在模板里长出一堆 `if (kvmem)` 分支。这里换成**合成**：
//   模板照旧产出（可能整份作废），override 决定最终用谁。LaunchArgs 一行不改。
//
// 合成规则（唯一的裁决点，见 Compose）：
//   ① Exe  ：服务项写了就用服务项的；没写用全局。
//   ② Args ：服务项写了完整参数串 ⇒ **完全取代**内置模板（不做前缀/后缀拼接）。
//            为什么是取代而不是追加：kvmem 那套里 `--cache-type-k/v`、`-ngl`、`--split-mode`、
//            `--jinja`、采样组**都不存在或含义不同**，追加会得到一条既跑不起来又看不出错在哪的命令行。
//            代价是"只想给通用服务再加一个开关"这类需求也得抄整串 —— 但那本来就不是本功能的目标。
//   ③ Env  ：**总是**叠加在最后，同名覆盖。所以自定义参数的服务可以显式压低
//            CUDA_VISIBLE_DEVICES（kvmem 只支持单卡，而托盘全局可能是 `0,1`）。
//   ④ 自定义参数时**不再**注入由 GPU 选择派生的 CUDA_VISIBLE_DEVICES / GGML_CUDA_ALLREDUCE ——
//            那两项是"模板的参数"配套算出来的，模板都作废了，派生物跟着作废才对
//            （否则单卡 kvmem 会顶着 `GGML_CUDA_ALLREDUCE=internal` 上机）。
//
// 「静默降级」是这里最需要防的一类错误（用户记忆：预期异常降级也需留痕）：
// 参数串引号不配对、环境项没有 `=` —— 这两种输入我们**不抛异常**（抛了会炸在菜单刷新的
// try/catch 里被静默吞掉），而是记进 Warnings，由调用方写进日志与悬浮提示。
// ============================================================================
public class LaunchPlan {
  public string Exe = "";
  public List<string> Args = new List<string>();
  public Dictionary<string,string> Env = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  public bool ArgsCustom;                  // true = 参数走了服务项自定义串（内置模板未参与）
  public List<string> Warnings = new List<string>();

  // 完整命令行（人类可读；与 Process.Start 的拼接方式一致）
  public string CommandLine() { return Exe + " " + string.Join(" ", Args); }

  // 环境变量的人类可读形式（按 key 排序，保证同一份配置每次渲染一致）
  public string EnvLine() {
    var keys = new List<string>(Env.Keys);
    keys.Sort(StringComparer.OrdinalIgnoreCase);
    var sb = new StringBuilder();
    foreach (var k in keys) { if (sb.Length > 0) sb.Append(" | "); sb.Append(k).Append('=').Append(Env[k]); }
    return sb.ToString();
  }

  public static LaunchPlan Compose(ServiceSpec spec, LaunchResult build, string globalExe) {
    var p = new LaunchPlan();
    p.Exe = string.IsNullOrWhiteSpace(spec.Exe) ? globalExe : spec.Exe.Trim();

    p.ArgsCustom = !string.IsNullOrWhiteSpace(spec.Args);
    if (p.ArgsCustom) {
      p.Args = SplitArgs(SubstitutePaths(spec.Args, spec, p.Warnings), p.Warnings);
      if (p.Args.Count == 0) {
        // 写了但切不出任何参数（例如整串只有空白/引号）：**不**退化成模板 —— 那会让
        // "exe 是自建、参数是内置模板"这种最危险的组合悄悄成立。宁可起不来，也不要起错。
        p.Warnings.Add("参数串为空（切分后无内容），将以零参数启动 " + p.Exe);
      }
    } else {
      p.Args = new List<string>(build.args);
      if (!string.IsNullOrWhiteSpace(build.envCuda))      p.Env["CUDA_VISIBLE_DEVICES"]    = build.envCuda;
      if (!string.IsNullOrWhiteSpace(build.envAllreduce)) p.Env["GGML_CUDA_ALLREDUCE"] = build.envAllreduce;
    }

    ParseEnv(spec.Env, p.Env, p.Warnings);
    return p;
  }

  // 参数串切分：空白分词，双引号内的空白不算分隔（供含空格路径用）。
  // 不配对的引号**不抛异常**：把余下部分并进当前 token，并记一条 Warning。
  public static List<string> SplitArgs(string? raw) { return SplitArgs(raw, null); }

  public static List<string> SplitArgs(string? raw, List<string>? warn) {
    var outp = new List<string>();
    if (string.IsNullOrWhiteSpace(raw)) return outp;
    var cur = new StringBuilder();
    bool inQuote = false, hasToken = false;
    foreach (char c in raw) {
      if (c == '"') { inQuote = !inQuote; hasToken = true; continue; }
      if (!inQuote && char.IsWhiteSpace(c)) {
        if (hasToken) { outp.Add(cur.ToString()); cur.Clear(); hasToken = false; }
        continue;
      }
      cur.Append(c); hasToken = true;
    }
    if (inQuote) warn?.Add("参数串引号不配对（缺一个 \")：余下内容被并入同一个参数");
    if (hasToken) outp.Add(cur.ToString());
    return outp;
  }

  // 参数串里的 {model} / {mmproj} 占位符 → 服务项自己的字段。
  // 为什么要有它：自定义参数串已经把 `-m`/`--mmproj` 也接管了，若让人把路径**再抄一遍**，
  // 改 `Model` 而忘了改 `Args` 就会得到"菜单显示 A、实际加载 B"——又是一种不报错的错。
  // 占位符让路径只有一个真源；直接写死路径也照旧能用（不做替换而已）。
  public static string SubstitutePaths(string? raw, ServiceSpec spec, List<string>? warn = null) {
    if (string.IsNullOrEmpty(raw)) return raw ?? "";
    if (raw.Contains("{model}") && string.IsNullOrWhiteSpace(spec.Model))
      warn?.Add("参数串引用了 {model} 但本服务的 Model 为空");
    if (raw.Contains("{mmproj}") && !spec.UseMmproj)
      warn?.Add("参数串引用了 {mmproj} 但本服务未启用 mmproj（UseMmproj=false）");
    return raw.Replace("{model}", spec.Model).Replace("{mmproj}", spec.Mmproj);
  }

  // 环境串解析：**一行一项**（"K=V"），换行分隔。
  //
  // ⛔ 为什么不支持分号分隔：Windows 上 `PATH` 的值本身就用分号分段（`%CUDA_HOME%\bin;...;%PATH%`），
  //    分号一分就把一个 PATH 项劈成两个 —— 而其中一半连 `=` 都没有，只会得到一条"已跳过"的警告，
  //    用户看到的是"我写了 PATH 却没生效"。JSON 里换行写 `\n`，读起来也不比分号差。
  //
  // key 不区分大小写（Windows 语义）：所以 cuda_visible_devices 能覆盖 CUDA_VISIBLE_DEVICES。
  // %NAME% 会展开，查找顺序 = **本块内已定义的项 → 注入的 lookup（默认本进程环境）**，
  // 所以同一块里可以写 `CUDA_HOME=...` 然后在下一行用 `%CUDA_HOME%\bin`（顺序有意义）。
  // 展开是必需的：ProcessStartInfo.Environment 不做 cmd 展开，不展开就会把字面量 "%PATH%" 塞给子进程。
  public static void ParseEnv(string? raw, Dictionary<string,string> into, List<string>? warn = null,
                              Func<string,string?>? lookup = null) {
    if (string.IsNullOrWhiteSpace(raw)) return;
    var outer = lookup ?? Environment.GetEnvironmentVariable;
    foreach (var piece in raw.Split(new[] { '\n', '\r' })) {
      var item = piece.Trim();
      if (item.Length == 0) continue;
      int eq = item.IndexOf('=');
      if (eq <= 0) { warn?.Add("环境项缺少 '='（已跳过）：" + item); continue; }
      var key = item.Substring(0, eq).Trim();
      // 本块内先查：KeyValuePair 顺序即书写顺序，后写的能看到先写的
      var val = ExpandVars(item.Substring(eq + 1).Trim(),
                           n => into.TryGetValue(n, out var v) ? v : outer(n), warn);
      if (key.Length == 0) { warn?.Add("环境项 key 为空（已跳过）：" + item); continue; }
      into[key] = val;
    }
  }

  // 展开 %NAME%（大小写按 lookup 自己的语义）。%% = 一个字面 %；落单的 % 原样保留；
  // 解析不到的变量**原样保留并留痕** —— 静默替换成空串会把 PATH 悄悄截断，那是最难查的一类。
  public static string ExpandVars(string? v, Func<string,string?> lookup, List<string>? warn = null) {
    if (string.IsNullOrEmpty(v) || v.IndexOf('%') < 0) return v ?? "";
    var sb = new StringBuilder();
    int i = 0;
    while (i < v.Length) {
      if (v[i] != '%') { sb.Append(v[i++]); continue; }
      int j = v.IndexOf('%', i + 1);
      if (j < 0) { sb.Append(v.Substring(i)); break; }
      var name = v.Substring(i + 1, j - i - 1);
      if (name.Length == 0) { sb.Append('%'); i = j + 1; continue; }   // "%%" ⇒ 一个字面 %
      var val = lookup(name);
      if (val == null) { warn?.Add("环境变量引用未解析（原样保留）：%" + name + "%"); sb.Append(v.Substring(i, j - i + 1)); }
      else sb.Append(val);
      i = j + 1;
    }
    return sb.ToString();
  }
}
