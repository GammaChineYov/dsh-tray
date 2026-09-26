using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QwenTray;

// ============================================================================
// S5-3（2026-09-12）：菜单**文本合成**从 TrayApp 搬进 Core。
//
// 搬的是「把状态与参数渲染成人看的一串字」这一层，零 WinForms：
//   4 个档位标签（KV / 缓存内存 / MTP / 参数组）
//   文件名与宽度截断（Mid）/ 时长（Dur）
//   二级菜单「运行时配置」6 行（CfgLines）
//   运行状态悬浮全量（StatusTip）
//
// 为什么菜单**装配**没跟着搬：Core 工程 UseWindowsForms=false，ToolStripMenuItem 根本装不进来
// （编译器直接 CS0234，不是约定）。装配留在 TrayApp.Services.cs，本文件只负责出文本。
// 这与 S5-1/S5-2 是同一刀法：**可判定的纯逻辑进 Core，UI 装配留在原位**。
//
// 为什么值得（治理报告 §11.3 的判据「拆分要看它解锁了什么下游动作」）：这几个函数原先只是
// TrayApp 的私有 static，只有 `--selftest-svcmenu` 能间接枚举到，而那个探针**不在 dotnet test 链上**。
// 搬进 Core 后它们被 SvcLinesTests 直接钉住 —— 同样一刀在 S5-2 上已经把「状态圆点/启停可用性」
// 从"只能手跑自检"变成 `dotnet test` 可查。
//
// 顺手清掉一个哑参数：原 `SvcCfgLines(Service svc, bool running, LaunchResult b)` 的 `b`
// **函数体从未读过**（第一行自己算 EffectiveGpus，不碰 b.args）—— 搬的同时删掉，别把它带进 Core。
// ============================================================================

// 渲染一次菜单所需的**全部输入快照**。
// Core 不能引用根工程的 `Service`（那是带 Process 句柄 / WinForms 侧状态的运行时袋），
// 所以由调用方先把 `Service` + 当前托盘参数拍成这个视图，再交给 SvcLines。
// 字段顺序 = 菜单里出现的顺序，便于对照。
public sealed class SvcView {
  // —— 服务事实（拍自 Service / ServiceSpec）——
  public string Model = "";
  public bool UseMmproj;
  public string Mmproj = "";
  public int Port;
  public string Provider = "";      // 空 = 显示 llama-local
  public int Batch, Ubatch;
  public bool Starting, Running, PortBusy;
  public int Pid;                   // 0 = 取不到（未运行 / 进程句柄丢失）→ 不打印 pid 行
  public DateTime? StartedAt;       // null = 取不到 → 不打印「启动 / 已运行」
  public string RamGb = "-";        // Service.RamGb：取不到时就是 "-"
  public int RunCtx;                // /props 实测 n_ctx（0 = 未探测）
  public int RunVision = -1;        // /props modalities.vision：-1=未探测 0=不支持 1=支持
  // —— 托盘参数（拍自 TrayApp 的菜单字段，即 dsh-tray.cfg）——
  public GpuSelection Gpu = new GpuSelection();
  public int GpuCount;
  public int SplitMode;             // 0=按层 layer 1=张量并行 tensor
  public int TsGpu1;                // 张量并行时 GPU1 占比 %
  public int KvMode;                // 0=默认 1=q8_0 2=f16
  public int CtxVal;
  public int CacheRam;              // MiB；0=禁用 -1=无限制
  public int MtpLevel;              // 0=无 1..4=MTPn
  public int ParamMode;             // 0=通用思考 1=编码思考 2=Instruct
  public bool BindAll;              // true=--host 0.0.0.0
  // —— per-service 覆盖（2026-09-20，拍自 LaunchPlan）——
  // 这四项只在 ArgsCustom=true 时才有意义：那时上面那批**托盘参数**（ctx/KV/切分/MTP/缓存内存…）
  // 对这条服务**全都不成立** —— 它们描述的是内置模板，而这条服务根本没走模板。
  public bool ArgsCustom;
  public string Exe = "";           // 生效的可执行文件名（自定义覆盖后）
  public string CustomArgs = "";    // 自定义参数串原文
  public string EnvLine = "";       // 生效的额外环境变量（"K=V | K=V"）
}

public static class SvcLines {

  // —— 文件名 / 截断 / 时长 ——
  public static string FileName(string? p) { try { return Path.GetFileName(p ?? ""); } catch { return p ?? ""; } }
  // 菜单宽度受模型文件名影响：过长的名字保留头尾（尾部含量化档位，优先保留尾部）
  public static string Mid(string? s, int max) {
    if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
    int keep = max - 1, head = keep * 2 / 3, tail = keep - head;
    return s.Substring(0, head) + "…" + s.Substring(s.Length - tail);
  }
  public static string Dur(TimeSpan t) {
    if (t.TotalDays >= 1) return (int)t.TotalDays + "d" + (t.Hours > 0 ? t.Hours + "h" : "");
    if (t.TotalHours >= 1) return (int)t.TotalHours + "h" + t.Minutes + "m";
    if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + "m" + t.Seconds + "s";
    return Math.Max(0, (int)t.TotalSeconds) + "s";
  }

  // —— 4 个档位标签：菜单标题与「运行时配置」两处共用，改一处必须两处一致 ——
  // 越界一律回落到"最保真的那一档"（f16 / Instruct / MTPn），不做静默截断
  public static string KvLabel(int m) { return m == 0 ? "默认" : m == 1 ? "8bit q8_0" : "16bit f16"; }
  public static string CacheRamLabel(int mb) {
    return mb == 0 ? "禁用" : mb < 0 ? "无限制" : (mb >= 1024 && mb % 1024 == 0) ? (mb / 1024) + "GB" : mb + "M";
  }
  public static string MtpLabel(int m) { return m == 0 ? "无" : m == 1 ? "MTP" : "MTP" + m; }
  public static string ParamLabel(int m) { return m == 0 ? "通用思考" : m == 1 ? "编码思考" : "Instruct"; }

  // 二级菜单「运行时配置」6 行。**元素个数固定 6** —— 菜单侧按这个数建行池，
  // 只改 Text/Visible、绝不增删元素（否则会触发历史那个「幽灵菜单/布局错乱」）。
  // 多出的行在菜单里永远不显示，所以这里的长度就是契约。
  public static string[] CfgLines(SvcView v) {
    // 自定义参数的服务**没有**托盘参数可言（见 SvcView 的 ArgsCustom 注释）：继续渲染 GPU/切分/
    // KV/MTP/缓存内存 五行等于把不存在的配置当事实报出来。走单独一版，只报真实存在的东西。
    if (v.ArgsCustom) return CustomCfgLines(v);
    var eff = LaunchArgs.EffectiveGpus(v.Gpu, v.GpuCount);
    // ⚠️ 多卡这行的 `string.Join("+", eff)` 得到的是 "0+1"（eff 是 List<int>）⇒ 渲染成 "GPU0+1"，
    //    而一级菜单 GPU 项走的是 GpuSelection.ShortLabel() ⇒ "GPU0+GPU1"。两处**本来就不一致**
    //    （既有缺陷，非本次搬运引入）。S5 从这里起的规矩是"纯搬运不改用户可见文本"，所以如实保留：
    //    改它要单独决定 + 单独走托盘人工回归，不能混进重构提交。SvcLinesTests 把这个现状钉住了。
    string gpuLine = eff.Count == 0 ? "CPU（-ngl 0，无 GPU 加速）"
      : eff.Count == 1 ? ("GPU" + eff[0] + " · -ngl 99 · --split-mode none")
      : ("GPU" + string.Join("+", eff) + " · -ngl 99 · --split-mode " + (v.SplitMode == 0 ? "layer" : "tensor -ts " + (100 - v.TsGpu1) + "," + v.TsGpu1));
    string mm = v.UseMmproj ? ("mmproj = " + FileName(v.Mmproj)) : "无 mmproj";
    return new string[]{
      "    模型 = " + Mid(FileName(v.Model), 42) + " · " + mm,
      "    " + gpuLine,
      "    上下文 = " + (v.CtxVal / 1024) + "K · KV = " + KvLabel(v.KvMode) + " · 批 = " + v.Batch + "/" + v.Ubatch + " · 缓存内存 = " + CacheRamLabel(v.CacheRam),
      "    MTP = " + MtpLabel(v.MtpLevel) + " · 参数组 = " + ParamLabel(v.ParamMode) + " · flash-attn = " + (eff.Count == 0 ? "关" : "on"),
      "    监听 = " + (v.BindAll ? "0.0.0.0" : "127.0.0.1") + ":" + v.Port + " · reasoning = deepseek · jinja",
      ProbeLine(v, true)
    };
  }

  // 自定义参数版（2026-09-20）：恒 6 行（契约与上面一致），只报**真实成立**的四件事 ——
  // 模型、生效的 exe、自定义参数串、生效的环境变量。托盘参数（ctx/KV/切分/MTP/缓存内存）一律不出现。
  static string[] CustomCfgLines(SvcView v) {
    const int W = 58;                              // 每行参数的可读宽度（含缩进后与其余行同长）
    string mm = v.UseMmproj ? ("mmproj = " + FileName(v.Mmproj)) : "无 mmproj";
    string a = v.CustomArgs ?? "";
    int n = LaunchPlan.SplitArgs(a).Count;
    string a1 = Clip(a, W);
    string a2 = a.Length > W ? Clip(a.Substring(W), W) : "";
    return new string[]{
      "    模型 = " + Mid(FileName(v.Model), 42) + " · " + mm,
      "    exe = " + (v.Exe.Length > 0 ? v.Exe : "（全局）") + " · 自定义参数 " + n + " 项（非内置模板）",
      "    参数 = " + a1,
      a2.Length > 0 ? ("    参数(续) = " + a2) : "    （参数串已显示完全，共 " + a.Length + " 字符）",
      "    环境 = " + (v.EnvLine.Length > 0 ? Clip(v.EnvLine, W) : "（无）"),
      ProbeLine(v, false)
    };
  }

  // 截断到宽度（超了用 "…" 收尾）。与 Mid 的区别：Mid 保头保尾，这里只保头 —— 参数串是**有序**的，
  // 从中间挖掉一段比"截掉尾巴"更容易让人读错（误以为缺的是中间某个开关）。
  static string Clip(string s, int width) { return s.Length <= width ? s : s.Substring(0, width - 1) + "…"; }

  // 「运行时配置」第 6 行。menuCtxFallback：托盘参数里的 ctx 能不能当"没探到时的回退显示"——
  // 内置模板下可以（那正是启动时用的 ctx）；自定义参数下**不可以**（-c 藏在参数串里，托盘不知道它是多少）。
  static string ProbeLine(SvcView v, bool menuCtxFallback) {
    if (!v.Running) return "    实测 = （未运行 → 启动后自动探测 /props）";
    string ctx = v.RunCtx > 0 ? (v.RunCtx / 1024) + "K"
               : menuCtxFallback ? (v.CtxVal / 1024) + "K(未探测)" : "未探测";
    return "    实测 = ctx " + ctx + " · 视觉 " + VisionText(v.RunVision);
  }

  // 「运行时配置」第 6 行 与 StatusTip 末行共用：-1=未探测 / 0=不支持 / 其余=支持
  static string VisionText(int runVision) { return runVision < 0 ? "未探测" : runVision == 1 ? "支持" : "不支持"; }

  // 一级项与状态行的悬浮全量（多行）。用户点不到菜单里的进程细节时，这里是唯一出口。
  public static string StatusTip(SvcView v) {
    var sb = new StringBuilder();
    sb.AppendLine("状态: " + (v.Starting ? "启动中" : v.Running ? "运行中" : (v.PortBusy ? "运行中(未托管，可「停止模型」按端口回收)" : "未运行")));
    if (v.Running) {
      // 两句**都**取到才打印 pid 行：原实现把 pid 与 StartTime 放在同一个 try 里，
      // 任一取不到（进程刚退出/权限不足）整行都不输出 —— 这里保持同一语义，别拆成两行。
      if (v.Pid > 0 && v.StartedAt.HasValue)
        sb.AppendLine("pid: " + v.Pid + "   启动: " + v.StartedAt.Value.ToString("MM-dd HH:mm:ss") + "   已运行: " + Dur(DateTime.Now - v.StartedAt.Value));
      sb.AppendLine("内存: " + v.RamGb);
    }
    sb.AppendLine("模型: " + v.Model);
    if (v.UseMmproj) sb.AppendLine("mmproj: " + v.Mmproj);
    sb.AppendLine("端口: " + v.Port + "   provider: " + (string.IsNullOrEmpty(v.Provider) ? "llama-local" : v.Provider));
    // 自定义参数的服务：菜单 6 行只放得下截断版（见 CustomCfgLines），完整串在这里 —— 此处
    // 是「点不到菜单细节时的唯一出口」，所以不截断。
    if (v.ArgsCustom) {
      sb.AppendLine("exe: " + (v.Exe.Length > 0 ? v.Exe : "（全局）") + "   （自定义参数，非内置模板）");
      sb.AppendLine("参数: " + v.CustomArgs);
      sb.AppendLine("环境: " + (v.EnvLine.Length > 0 ? v.EnvLine : "（无）"));
    }
    if (v.Running && v.RunCtx > 0) sb.AppendLine("服务端实测: ctx " + (v.RunCtx / 1024) + "K · 视觉 " + VisionText(v.RunVision));
    // 一级项快捷启动的可发现性（2026-09-12）：一级项的 ToolTip 由本函数渲染 ⇒ 提示必须落在这里。
    // 语义与 TrayApp.QuickStart 的判据严格对应（未运行=红点=可启动）。
    sb.AppendLine("提示: 未运行（红点）时直接点本一级项 = 快捷启动，不必展开子菜单。");
    return sb.ToString().TrimEnd();
  }

  // ==========================================================================
  // 通知区图标悬浮 tooltip（`icon.Text`）—— 行合成 + **长度钳位**（2026-09-13）
  //
  // ⚠️ 为什么钳位不是"多此一举的防御"而是必需：`NotifyIcon.Text` 落进 Win32 `szTip`，上限 **127 字符**。
  //    .NET 8 超限**直接抛 ArgumentException**（System.Windows.Forms.dll 里有 TextTooLong 资源串，已核），
  //    而唯一调用点是 `try{ icon.Text=tipNow; }catch{}` ⇒ 超限被静默吞掉、**保留上一次的旧文本**，
  //    表现出来就是"tooltip 卡住不刷新"这种极难定位的症状（不报错、不写日志）。
  //    原实现三服务全开时已 ~169 字符（必然超限），所以本函数是**顺手修掉一个既有隐患**，不是新引入的复杂度。
  //
  // 策略：按行装填，装不下就把末行截断加 "…"，**绝不整段丢弃**（宁缺尾行，也不要"看着不动"）。
  // ==========================================================================
  public const int TipMaxChars = 127;

  public static string ClampTip(IEnumerable<string> lines, int max = TipMaxChars) {
    var kept = new List<string>();
    int used = 0;                                   // 已占字符数（含行间 '\n'）
    foreach (var raw in lines) {
      var ln = raw ?? "";
      if (used + ln.Length <= max) { kept.Add(ln); used += ln.Length + 1; continue; }
      int room = max - used - 1;                    // 末尾留 1 格给省略号
      if (room > 0) kept.Add(ln.Substring(0, Math.Min(ln.Length, room)) + "…");
      break;
    }
    return string.Join("\n", kept);
  }

  // 单模型一行的悬浮 tooltip 骨架：名称(端口):状态 RAM x.xxG [KV 用量/总量] 缓存 用量/上限
  // KV 总量为 0 = 还没探到（/props 未就绪）⇒ 整段 KV 不显示，而不是显示 "0/0"。
  // 缓存上限为 0 = llama 未以 -lv 4 启动（该行是 trace 级）⇒ 显示「无」，**绝不拿 cfg 的设定值冒充实测用量**。
  public static string SvcTipLine(string shortName, int port, string state, string ramGb,
                                  int kvUsed, int kvTotal, int cacheUsedMib, int cacheLimitMib) {
    var s = shortName + "(" + port + "):" + state + " RAM " + ramGb;
    if (kvTotal > 0) s += " KV " + kvUsed + "/" + kvTotal;
    s += " 缓存 " + (cacheLimitMib > 0 ? cacheUsedMib + "M/" + cacheLimitMib + "M" : "无");
    return s;
  }
}
