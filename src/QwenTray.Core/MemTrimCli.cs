using System;
using System.Collections.Generic;
using System.Text;

namespace QwenTray;

// ============================================================================
// 2026-09-13：工作集回收的命令行入口（DSHTray.exe --trim [port]）。
//
// 为什么要有一个不建 TrayApp 的入口：托盘菜单只在**人坐在机器前**时可用，而回收最有价值的时刻往往是
// "跑完一批活、要腾内存给 Unity"这类**脚本化**场景（批处理 / 计划任务 / 另一个 agent 会话）。
// 它同时是自检里"L1 可自动跑"的那一半 —— 不需要真实桌面会话。
//
// 刻意不进 Core 的唯一边界：不读托盘菜单、不碰 UI；只用 Config/SvcProbe/WinMem 三个 Core 件。
// ============================================================================
public static class MemTrimCli {
  /// <summary>
  /// 执行一次回收并把"给人看的一行行回执"拼成文本。
  /// <para><paramref name="port"/> &gt; 0 = 只处理该端口；&lt;= 0 = 处理 dsh-tray-config.json 里所有启用的服务。</para>
  /// </summary>
  public static string Run(int port) {
    var targets = new List<(string name, int port)>();
    if (port > 0) {
      targets.Add(("port " + port, port));
    } else {
      try {
        foreach (var s in Config.Load().Services) if (s.Enabled) targets.Add((s.Name, s.Port));
      } catch { }
    }

    var sb = new StringBuilder();
    sb.AppendLine("MEM TRIM @ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    if (targets.Count == 0) {
      sb.AppendLine("  没有可用目标（给 --trim <port>，或确认配置里有启用的服务）");
      sb.AppendLine("RESULT NOOP");
      return sb.ToString();
    }

    int done = 0;
    foreach (var (name, p) in targets) {
      bool up = SvcProbe.HealthUp(p);
      int pid = WinMem.PidByPort(p);
      if (!up || pid <= 0) {
        sb.AppendLine("  " + name + " (" + p + ") 未运行（health=" + (up ? "up" : "down") + " pid=" + pid + "）—— 跳过");
        continue;
      }
      bool busy = SvcProbe.Busy(p);
      long b = 0, a = 0;
      bool ok = WinMem.TrimWorkingSet(pid, out b, out a);
      sb.AppendLine("  " + MemTrim.ResultLine(TrimKind.Manual, name + " (" + p + ")", ok, b, a)
                    + "  [pid " + pid + (busy ? " · ⚠️ 有请求在处理，本次回收可能造成一次缺页" : "") + "]");
      if (ok) done++;
    }
    sb.AppendLine("RESULT " + (done > 0 ? "OK" : "NOOP") + " (成功回收 " + done + " 个)");
    return sb.ToString();
  }
}
