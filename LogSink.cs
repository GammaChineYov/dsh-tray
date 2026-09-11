using System;
using System.Collections.Generic;
using System.Text;

namespace QwenTray;

// —— 有界、线程安全的行缓冲（2026-09-11 架构治理 S0，解 P1-1 + P1-2）——
//
// 原实现：Service.log 是**裸 StringBuilder**。
//   · 三个写入方 —— stdout 读回调 / **stderr 读回调**（BeginOutputReadLine 与 BeginErrorReadLine
//     是**两个**线程池回调，不是"同一个 IO 线程顺序触发"）/ Log() —— 与 UI 线程的读取方并发访问，零同步；
//     线程池侧的异常走 AppDomain.UnhandledException（**只通知、不拦截**）→ 直接终止进程。
//   · 只增不减 → 长驻托盘内存单调上涨。
//
// 本类的语义是**增补而非替换**，目的就是让调用点几乎不用改：
//   · Length 返回**累计写入字符数**（单调递增），可以继续当"增量游标"用；
//   · Read(start) 从该游标位置取文本；
//   · 超上限时丢弃**最早**的内容，于是游标自然"落后于窗口"，Read 会夹取(clamp)到窗口起点。
//     因此调用方原有的 `long L=log.Length; if(L>lastLen){ ...=log.Read(lastLen); lastLen=L; }`
//     模式无需改动即可安全降级 —— 丢的是历史，不是新日志。
public sealed class LogSink {
  readonly object _gate = new object();
  readonly Queue<string> _lines = new Queue<string>();
  readonly long _maxChars;
  long _retained;   // 窗口内当前字符数
  long _total;      // 累计写入字符数（游标基准）

  public LogSink(long maxChars = 256 * 1024) { _maxChars = maxChars < 4096 ? 4096 : maxChars; }

  // 刻意接受 null：调用方（进程读回调）本就可能给出 null，这里当作"无内容"忽略而非抛异常
  public void Append(string? s) { if (string.IsNullOrEmpty(s)) return; Add(s!); }
  public void AppendLine(string? s) { Add((s ?? "") + "\r\n"); }

  void Add(string s) {
    lock (_gate) {
      _lines.Enqueue(s);
      _retained += s.Length;
      _total += s.Length;
      // 至少保留一行：单行本身超上限时不清空（否则会把"那一行关键报错"丢掉）
      while (_retained > _maxChars && _lines.Count > 1) _retained -= _lines.Dequeue().Length;
    }
  }

  // 累计写入字符数（单调递增，可当游标）
  public long Length { get { lock (_gate) return _total; } }

  // 当前窗口内保留的字符数
  public long Retained { get { lock (_gate) return _retained; } }

  // 窗口起点在累计坐标系里的位置（= Length - Retained）
  public long WindowStart { get { lock (_gate) return _total - _retained; } }

  // 取 [start, Length) 的可读部分；start 已滑出窗口时自动夹取到窗口起点（不抛异常）
  public string Read(long start) {
    lock (_gate) {
      long baseOff = _total - _retained;
      if (start < baseOff) start = baseOff;
      if (start >= _total) return "";
      long skip = start - baseOff;
      var sb = new StringBuilder((int)Math.Min(_retained - skip, 1 << 20));
      long pos = 0;
      foreach (string s in _lines) {
        long end = pos + s.Length;
        if (end > skip) {
          int begin = (int)Math.Max(0, skip - pos);
          sb.Append(s, begin, s.Length - begin);
        }
        pos = end;
      }
      return sb.ToString();
    }
  }

  // 清空窗口内容但**不重置** _total：游标语义保持单调，避免调用方游标"倒退"后重复或丢日志
  public void Clear() { lock (_gate) { _lines.Clear(); _retained = 0; } }
}
