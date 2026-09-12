using System;
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
      v.Running
        ? ("    实测 = ctx " + (v.RunCtx > 0 ? (v.RunCtx / 1024) + "K" : (v.CtxVal / 1024) + "K(未探测)") + " · 视觉 " + VisionText(v.RunVision))
        : "    实测 = （未运行 → 启动后自动探测 /props）"
    };
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
    if (v.Running && v.RunCtx > 0) sb.AppendLine("服务端实测: ctx " + (v.RunCtx / 1024) + "K · 视觉 " + VisionText(v.RunVision));
    return sb.ToString().TrimEnd();
  }
}
