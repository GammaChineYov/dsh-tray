using System;
using System.Collections.Generic;
using System.Text;

namespace QwenTray;

// ============================================================================
// 启动预热的命令行入口（DSHTray.exe --warmup [port]）。
//
// 与 MemTrimCli 同款理由：预热最有价值的时刻往往是**先于人**的（模型刚被托盘/计划任务拉起、
// 还没有人在机器前），所以它必须有一条不建 TrayApp、能进批处理/计划任务的路径。
// 它同时也是"自动预热不起作用"时的**判据装置** —— 手动跑一次就知道是没条目、没空槽，
// 还是绑定校验把条目挡了（后者会在 --json 的 skips 里给原因）。
// ============================================================================
public static class WarmupCli {
  /// <summary>
  /// 对目标端口各做一次预热。port &gt; 0 = 只处理该端口；&lt;= 0 = 配置里所有启用的服务。
  /// 返回"给人看的多行回执"，末行固定 RESULT OK/NOOP（给脚本判）。
  /// </summary>
  public static string Run(int port, int top = 0) {
    var targets = new List<(string name, int port)>();
    if (port > 0) {
      targets.Add(("port " + port, port));
    } else {
      try { foreach (var s in Config.Load().Services) if (s.Enabled) targets.Add((s.Name, s.Port)); } catch { }
    }

    var sb = new StringBuilder();
    sb.AppendLine("KV WARMUP @ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    if (targets.Count == 0) {
      sb.AppendLine("  没有可用目标（给 --warmup <port>，或确认配置里有启用的服务）");
      sb.AppendLine("RESULT NOOP");
      return sb.ToString();
    }

    int done = 0;
    foreach (var (name, p) in targets) {
      if (!SvcProbe.HealthUp(p)) { sb.AppendLine("  " + name + " (" + p + ") 未运行 —— 跳过"); continue; }
      var r = Warmup.Run(p, top);
      sb.AppendLine("  " + name + " (" + p + ")  " + r.line);
      if (r.ok) done++;
      if (!r.ok && r.raw.Length > 0) {
        // 只回放 kvctl 的一行 JSON（排障要的是 skips 的原因，不是整段流水日志）
        foreach (var l in r.raw.Replace("\r\n", "\n").Split('\n'))
          if (l.TrimStart().StartsWith("{")) { sb.AppendLine("      " + l.Trim()); break; }
      }
    }
    sb.AppendLine("RESULT " + (done > 0 ? "OK" : "NOOP") + " (成功预热 " + done + " 个)");
    return sb.ToString();
  }
}
