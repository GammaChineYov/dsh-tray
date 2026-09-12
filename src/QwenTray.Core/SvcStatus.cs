using System;
using System.Diagnostics;

namespace QwenTray;

// S5（2026-09-12）：从 `TrayApp` 的 partial 里升格出来的**服务状态判定与探活** —— 无状态、无 UI 依赖。
//
// 为什么单独立一个类型而不是留在 `TrayApp.Services.cs` 里：
//   `SvcState` / `SvcDot` / `SvcEnable` 原本就是 `static`（写的时候就是照"与 UI 解耦"设计的），
//   但因为是 `TrayApp` 的私有成员，只有 `--selftest-svcmenu` 能枚举它们 —— 那个探针**不在**
//   `dotnet test` 链上。搬进 Core 后，四态映射有了穷举断言的单测（见 SvcStatusTests）。
//
// 语义一字未改（逐字抄自原实现）：状态文本 / 圆点色名 / 三个启停项的可用性。
// 注意这是**纯数据映射**，颜色名与中文文本都属于"数据"而非 UI —— Core 的围栏只禁 System.Windows.Forms。
public static class SvcStatus {

  // 状态行文本（含「运行中(未托管)」：端口上有 llama 在服务但不是本托盘拉起的，可「停止模型」按端口回收）
  public static string State(bool starting, bool running, bool busy) {
    return starting ? "启动中" : running ? "运行中" : busy ? "运行中(未托管)" : "未运行";
  }

  // 一级项状态圆点色名（黄=启动中 绿=运行中 橙=运行中(未托管) 红=未运行）
  public static string Dot(bool starting, bool running, bool busy) {
    return starting ? "yellow" : running ? "green" : busy ? "orange" : "red";
  }

  // 三个启停菜单项的可用性：
  //   start   仅在"未跑也没被占"时可点
  //   stop    运行中 / 启动中 / 被外部占用 都可点（后两种要能中断）
  //   restart 恒可点（由用户决定是否值得）
  public static (bool start, bool stop, bool restart) Enable(bool starting, bool running, bool busy) {
    return (!running && !starting && !busy, running || starting || busy, true);
  }
}

// S5（2026-09-12）：端口探活与按端口回收 —— 同样是纯静态、与 UI 无关的进程层操作。
// 两个探针的差别是**语义不同**，不是重复代码：
//   PortUp   只要 HTTP 有响应就算"端口上有东西"（含异常页、其它进程）
//   HealthUp 还要求响应体含 "ok"（llama-server 的 /health 契约）
// 「停止模型」与「启动前占用检查」用前者（宁可保守），就绪判定用后者。
public static class SvcProbe {

  // 端口上有 HTTP 服务（不论是不是 llama）—— 启动前占用检查、停止后等端口释放都用它
  public static bool PortUp(int port) {
    try { using (var wc = new System.Net.WebClient()) { wc.DownloadString("http://127.0.0.1:" + port + "/health"); return true; } } catch { }
    return false;
  }

  // llama-server /health 就绪探测（{"status":"ok"}）：false = 未就绪 / 进程未起
  public static bool HealthUp(int port) {
    try { using (var wc = new System.Net.WebClient()) { wc.Encoding = System.Text.Encoding.UTF8; return wc.DownloadString("http://127.0.0.1:" + port + "/health").Contains("ok"); } } catch { }
    return false;
  }

  // 按端口收 llama-server：停止/重启的兜底（proc 为空、或该进程由外部/上一实例启动）。
  // ⚠️ 只杀进程名含 llama 的，避免误伤端口上恰好存在的其它服务。
  public static int KillByPort(int port) {
    try {
      var psi = new ProcessStartInfo("netstat", "-ano") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
      var p = Process.Start(psi); if (p == null) return 0;
      string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(3000);
      foreach (var line in o.Split('\n')) {
        if (!line.Contains(":" + port) || !line.Contains("LISTENING")) continue;
        var parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        int pid; if (parts.Length == 0 || !int.TryParse(parts[parts.Length - 1], out pid)) return 0;
        try { var pr = Process.GetProcessById(pid); if (pr.ProcessName.ToLowerInvariant().Contains("llama")) { pr.Kill(); return pid; } } catch { }
        return 0;
      }
    } catch { }
    return 0;
  }
}
