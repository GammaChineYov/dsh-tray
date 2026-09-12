namespace QwenTray.Tests;

// S5（2026-09-12）：LogSink —— 有界、线程安全的行缓冲（S0 解 P1-1/P1-2 的产物）。
//
// 为什么 S5 才补它的单测：它此前只被 `--selftest-logsink` 覆盖，而那个探针**不在** `dotnet test`
// 链上 —— 也就是说"日志缓冲坏了"这件事，只有在有人手动跑托盘自检时才会被发现。它现在进了
// QwenTray.Core（纯逻辑、无 UI 依赖），所以可以直接钉进测试链。
//
// 下面每条断言都对应 LogSink.cs 里一条**写明的**语义契约（不是我从实现倒推的）：
//   · Length 单调累计（可当游标）          · 超上限丢**最早**的，且**至少留一行**
//   · Read(start) 对滑出窗口的游标**夹取**而非抛异常  · Clear 不重置 Length
public class LogSinkTests {
  // 构造函数把上限夹到 >= 4096（不然"只留一行"会让窗口失去意义）
  const long MinCap = 4096;

  [Fact] public void Append_NullOrEmpty_IsIgnored_NotThrown() {
    var s = new LogSink();
    s.Append(null);
    s.Append("");
    Assert.Equal(0, s.Length);
    Assert.Equal(0, s.Retained);
  }

  [Fact] public void AppendLine_Null_WritesJustNewline() {
    // 进程 stdout/stderr 读回调本就会给 null，调用方不该为此加判断
    var s = new LogSink();
    s.AppendLine(null);
    Assert.Equal(2, s.Length);          // 只剩 "\r\n"
    Assert.Equal("\r\n", s.Read(0));
  }

  [Fact] public void Length_IsMonotonicTotal_EvenAfterEviction() {
    var s = new LogSink(MinCap);
    long expect = 0;
    for (int i = 0; i < 500; i++) { s.AppendLine(new string('a', 100)); expect += 102; }
    Assert.Equal(expect, s.Length);     // 被丢掉的也算 —— 否则游标会"倒退"造成重复/丢日志
    Assert.True(s.Retained < s.Length); // 确实发生了丢弃
  }

  [Fact] public void Window_IsBounded_ByMaxChars() {
    // 负向意义：拿掉 Add() 里那句 while 出队，本断言立刻红 —— 这正是"内存单调上涨"的病
    var s = new LogSink(MinCap);
    for (int i = 0; i < 500; i++) s.AppendLine(new string('b', 200));
    Assert.True(s.Retained <= MinCap, $"窗口应被夹在上限内，实测 {s.Retained} > {MinCap}");
    Assert.True(s.Length > MinCap * 4, "本用例必须真的写超上限，否则断言空转");
  }

  [Fact] public void SingleOversizedLine_IsStillRetained() {
    // "至少留一行"：单行本身超上限时不清空 —— 否则会把那一行关键报错丢掉
    var s = new LogSink(MinCap);
    s.AppendLine(new string('c', 10000));
    Assert.Equal(10002, s.Retained);
    Assert.True(s.Retained > MinCap);
  }

  [Fact] public void Read_FromValidCursor_ReturnsOnlyIncrement() {
    var s = new LogSink();
    s.Append("abc");
    Assert.Equal("abc", s.Read(0));
    s.Append("def");
    Assert.Equal("def", s.Read(3));     // 增量取：老内容不重复吐
    Assert.Equal("", s.Read(6));        // 游标恰在末尾
  }

  [Fact] public void Read_StaleCursor_IsClamped_NoThrow() {
    var s = new LogSink(MinCap);
    for (int i = 0; i < 200; i++) s.AppendLine(new string('d', 100));
    string tail = s.Read(0);            // 0 早已滑出窗口 → 夹到窗口起点
    Assert.False(string.IsNullOrEmpty(tail));
    Assert.Equal(s.WindowStart, s.Length - s.Retained);
    Assert.Equal(s.Retained, tail.Length);
    Assert.Equal("", s.Read(s.Length + 999));   // 游标越界（未来位置）→ 空串，不抛
  }

  [Fact] public void Clear_KeepsLengthAndCursorSemantics() {
    var s = new LogSink();
    s.Append("abc");
    long before = s.Length;
    s.Clear();
    Assert.Equal(before, s.Length);   // 刻意不重置：否则调用方游标"倒退"后重复或丢日志
    Assert.Equal(0, s.Retained);
    Assert.Equal("", s.Read(0));
    s.Append("xy");
    Assert.Equal(5, s.Length);
    Assert.Equal("xy", s.Read(3));
  }

  [Fact] public void ConcurrentWrites_RowCountAndLengthAreConserved() {
    // "三写一读零同步"是 S0 修的竞态（stdout/stderr 是两个回调线程 + UI 读）——
    // 这里用 4 写 1 读复现该形态：只断言不抛异常且账目守恒
    var s = new LogSink(64 * 1024);
    const int Threads = 4, Per = 2000;
    var writers = new List<Thread>();
    for (int t = 0; t < Threads; t++)
      writers.Add(new Thread(() => { for (int i = 0; i < Per; i++) s.AppendLine("x"); }));
    var reader = new Thread(() => { for (int i = 0; i < 5000; i++) { long L = s.Length; if (L > 0) s.Read(L - 1); } });
    foreach (var w in writers) w.Start();
    reader.Start();
    foreach (var w in writers) w.Join();
    reader.Join();
    Assert.Equal((long)Threads * Per * 3, s.Length);   // "x\r\n" = 3 字符
  }
}
