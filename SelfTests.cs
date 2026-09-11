using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QwenTray;

// —— 日志缓冲自检（2026-09-11 架构治理 S0）——
// 纯数据层：不建 TrayApp、不碰真实服务、不占单实例锁。对应报告 §7 S0 验收第 2 条。
public static class LogSinkProbe {
  public static string Run() {
    var sb = new StringBuilder();
    int pass = 0, fail = 0;
    Action<bool, string> ck = (ok, what) => { if (ok) pass++; else fail++; sb.AppendLine("  " + (ok ? "PASS" : "FAIL") + "  " + what); };

    sb.AppendLine("LOGSINK PROBE  (有界 + 线程安全 + 游标夹取)");

    // 1) 并发写入：8 线程 × 20000 行 = 16 万次（旧实现是裸 StringBuilder，此处会随机抛/丢字符）
    const int Threads = 8, PerThread = 20000, Cap = 64 * 1024;
    var sink = new LogSink(Cap);
    var written = new long[1];
    Exception? ex = null;
    var tasks = new List<Task>();
    for (int i = 0; i < Threads; i++) {
      int id = i;
      tasks.Add(Task.Run(() => {
        try {
          for (int k = 0; k < PerThread; k++) {
            string line = "[" + id + "] line " + k + "\r\n";
            sink.Append(line);
            Interlocked.Add(ref written[0], line.Length);
          }
        } catch (Exception e) { Interlocked.CompareExchange(ref ex, e, null); }
      }));
    }
    Task.WaitAll(tasks.ToArray());
    ck(ex == null, "并发写入 " + Threads + "×" + PerThread + " 行无异常" + (ex != null ? "（" + ex.Message + "）" : ""));
    ck(sink.Length == written[0], "Length 守恒 = 累计写入字符数（" + sink.Length + " vs " + written[0] + "）");
    ck(sink.Retained <= Cap, "窗口有界：Retained=" + sink.Retained + " ≤ 上限 " + Cap);
    ck(sink.Length > sink.Retained, "确实发生了丢弃（Length 单调 > 窗口内容）");

    // 2) 游标夹取：用一个早已滑出窗口的旧游标，必须返回窗口内内容而不是抛异常/返回空
    string s1 = sink.Read(0);
    ck(s1.Length > 0 && s1.Length <= sink.Retained, "旧游标(0)被夹取到窗口起点：返回 " + s1.Length + " 字符且不抛异常");

    // 3) 增量读：从当前 Length 起应恰好拿到新增内容
    long cursor = sink.Length;
    sink.Append("TAIL-A\r\n");
    ck(sink.Read(cursor).Contains("TAIL-A"), "增量读（从 Length 游标起）拿到新增内容");
    ck(sink.Read(sink.Length) == "", "游标 = Length 时返回空串");

    // 4) 单行超上限：至少保留一行，不清空
    var tiny = new LogSink(4096);
    tiny.Append(new string('y', 9000));
    ck(tiny.Length == 9000 && tiny.Retained == 9000, "单行超上限仍完整保留（Retained=" + tiny.Retained + "）");

    // 5) 空输入安全
    var n = new LogSink(8192);
    n.Append(null);
    n.AppendLine(null);
    ck(n.Length == 2, "Append(null) 安全忽略、AppendLine(null) 记为空行（Length=" + n.Length + "）");

    // 6) 单调性：Clear 之后游标语义不回退（避免调用方游标倒退后重复/丢日志）
    long before = n.Length;
    n.Clear();
    ck(n.Length >= before && n.Read(before) == "", "Clear 不重置 Length（" + n.Length + " ≥ " + before + "），旧游标读空");

    sb.AppendLine("RESULT " + (fail == 0 ? "PASS" : "FAIL") + "  pass=" + pass + " fail=" + fail);
    return sb.ToString();
  }
}
