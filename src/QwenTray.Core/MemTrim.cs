using System;

namespace QwenTray;

// ============================================================================
// 2026-09-13：llama-server 工作集「主动回收」的三条触发路径 + 判定内核。
//
// 背景（实测结论，唯一源见 skill `dsh-tray-dev/references/llama-memory-triage.md`）：
//   27B 双卡张量并行加载后，llama-server 进程会持有约 33.5 GiB 的**私有写合并提交**
//   （522 个恰好 64 MiB 的 protect=0x404 块）。其中约 17.2 GiB 是 gguf 权重的 mmap 页 ——
//   它是**文件缓存**，不是真实占用：EmptyWorkingSet 之后立即释放 17.86 GiB，
//   且实测 **推理速度不变（49.8→49.9 tok/s）、工作集不涨回**。用户系统要跑多个会话 + Unity，
//   不能让人 "不确定何时松手" ⇒ 由我们主动、可预期地回收。
//
// 三条路径（用户 2026-09-13 拍板）：手动 / 空闲自动 / **模型就绪后自动一次**。
//   本文件只做**判定**与**文本**（纯函数 ⇒ MemTrimTests 穷举），真正的 P/Invoke 在 WinMem.cs，
//   菜单装配留在 TrayApp（Core 工程 UseWindowsForms=false，装不进 UI）。
// ============================================================================

/// <summary>回收触发路径。</summary>
public enum TrimKind {
  /// <summary>用户在菜单里点「立即回收」/ CLI --trim。人的意图 ⇒ 恒放行。</summary>
  Manual,
  /// <summary>llama-server 从「启动中」转为就绪后自动跑一次（加载刚结束，工作集最大）。</summary>
  OnReady,
  /// <summary>空闲节拍自动回收：距上次回收超过间隔、且当前没有任何请求在处理。</summary>
  Idle,
}

public static class MemTrim {
  /// <summary>空闲自动回收默认间隔（秒）。30 分钟 —— 比"每几分钟"更接近"离岗后收拾"的语义。</summary>
  public const int DefaultIdleIntervalSec = 1800;
  /// <summary>间隔下限（秒）：防止用户配成 5 秒把进程翻成换页风暴。</summary>
  public const int MinIdleIntervalSec = 60;
  /// <summary>间隔上限（秒）：24h（等于"基本不自动跑"，留手动与 OnReady 两条路径）。</summary>
  public const int MaxIdleIntervalSec = 86400;

  /// <summary>空闲自动回收的菜单档位（秒）。</summary>
  public static readonly int[] IdleIntervalOptions = { 300, 900, 1800, 3600, 7200, 86400 };

  /// <summary>
  /// 回收台账（ndjson）。与三级缓存的 cache-usage.ndjson 同目录、不同文件 ——
  /// 一个记"缓存命中/落盘"，一个记"内存回收了多少"，便于分别判断扩容与回收策略。
  /// </summary>
  public const string DefaultLogPath = @"E:\kv_cache\mem-trim.ndjson";

  /// <summary>档位标签（分钟/小时）。越界值回落到「同上限」语义的描述，不抛。</summary>
  public static string IntervalLabel(int sec) {
    if (sec >= MaxIdleIntervalSec) return "仅手动";
    if (sec >= 3600) { int h = sec / 3600; return h + "小时"; }
    int m = Math.Max(1, sec / 60);
    return m + "分钟";
  }

  /// <summary>把任意输入夹进 [Min, Max]。菜单外的 cfg 值（手改文件）也走这里，绝不让非法值直接生效。</summary>
  public static int ClampInterval(int sec) {
    if (sec < MinIdleIntervalSec) return MinIdleIntervalSec;
    if (sec > MaxIdleIntervalSec) return MaxIdleIntervalSec;
    return sec;
  }

  /// <summary>
  /// 空闲路径是否放行。
  /// <para>🔴 <paramref name="anyProcessing"/> 必须为 false：有请求在跑时回收工作集 = 把正要用的权重页
  /// 推去 standby，下一次推理立刻重新缺页 ⇒ 只换到抖动，换不到内存。这是本判定存在的唯一理由。</para>
  /// </summary>
  public static bool AllowIdle(bool idleEnabled, bool anyProcessing, long sinceLastTrimMs, int intervalSec) {
    if (!idleEnabled) return false;
    if (anyProcessing) return false;
    if (sinceLastTrimMs < 0) return true;          // 从未回收过（新进程）⇒ 允许
    return sinceLastTrimMs >= (long)ClampInterval(intervalSec) * 1000L;
  }

  /// <summary>
  /// 就绪路径是否放行。刚加载完的进程**不应该**有请求在处理（端口刚通），但外部客户端可能抢跑
  /// ⇒ 同样看 <paramref name="anyProcessing"/>，避免和首个请求打架。
  /// </summary>
  public static bool AllowOnReady(bool onReadyEnabled, bool anyProcessing) {
    return onReadyEnabled && !anyProcessing;
  }

  /// <summary>统一入口：三条路径合成一个判据（菜单可用性提示与真正执行共用，防"按钮说能点、点了不干"）。</summary>
  public static bool Allow(TrimKind kind, bool idleEnabled, bool onReadyEnabled, bool anyProcessing, long sinceLastTrimMs, int intervalSec) {
    switch (kind) {
      case TrimKind.Manual:  return true;                                                   // 人点了就是意图，不做二次否决
      case TrimKind.OnReady: return AllowOnReady(onReadyEnabled, anyProcessing);
      case TrimKind.Idle:    return AllowIdle(idleEnabled, anyProcessing, sinceLastTrimMs, intervalSec);
      default:               return false;
    }
  }

  public static string KindLabel(TrimKind k) {
    switch (k) {
      case TrimKind.Manual:  return "手动";
      case TrimKind.OnReady: return "启动后";
      case TrimKind.Idle:    return "空闲";
      default:               return "?";
    }
  }

  /// <summary>字节 → 人类可读（GiB/MiB/KiB）。用于"释放了 17.86 GiB"这类回执。</summary>
  public static string Bytes(long b) {
    double a = Math.Abs((double)b);
    if (a >= 1073741824.0) return (b / 1073741824.0).ToString("0.00") + " GiB";
    if (a >= 1048576.0)    return (b / 1048576.0).ToString("0.0") + " MiB";
    if (a >= 1024.0)       return (b / 1024.0).ToString("0.0") + " KiB";
    return b + " B";
  }

  /// <summary>
  /// 一次回收的回执行。**必须如实区分三种结果**，否则用户无法判断"到底是没回收还是回收不动"：
  ///   失败（拿不到进程） / 回收了但工作集没降（已是最小） / 真的降了。
  /// </summary>
  public static string ResultLine(TrimKind kind, string svcName, bool ok, long before, long after) {
    string head = "[" + KindLabel(kind) + "] " + svcName + " ";
    if (!ok) return head + "回收失败（进程不可访问或已退出）";
    long freed = before - after;
    if (before <= 0 || after <= 0) return head + "回收完成（工作集读数不可用）";
    if (freed <= 0) return head + "工作集已在最小（" + Bytes(after) + "），无页可回收";
    return head + "回收 " + Bytes(freed) + "（" + Bytes(before) + " → " + Bytes(after) + "）";
  }

  /// <summary>菜单/工具提示用的策略摘要（一句话把三条路径都讲清）。</summary>
  public static string PolicyText(bool onReadyEnabled, bool idleEnabled, int intervalSec) {
    return "启动后回收=" + (onReadyEnabled ? "开" : "关")
         + " | 空闲自动回收=" + (idleEnabled ? "开（每 " + IntervalLabel(ClampInterval(intervalSec)) + "）" : "关")
         + " | 手动=随时可用";
  }
}
