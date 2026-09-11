using System;
using System.Threading;
using System.Windows.Forms;

namespace QwenTray;

// —— UI 线程调度器（2026-09-11 架构治理 S0）——
//
// 修的是什么（报告 P0-1，两处同型缺陷）：
//   原实现把"是否需要 marshal"的判据放在**业务控件**上 ——
//     · TrayApp.Ui()       判 logForm.InvokeRequired
//     · LogForm.AppendTo() 判 box / dshBox.InvokeRequired
//   WinForms 的 Control.InvokeRequired 在控件**句柄尚未创建**时**恒返回 false**，于是 else 分支
//   直接在调用线程（线程池）上执行 UI 操作，并**把该控件的句柄永久绑定到那个线程池线程**；
//   此后所有 BeginInvoke 都被投递到一个没有消息泵的线程 → **静默失效**
//   （日志窗口始终空白、菜单圆点不刷新、缓存字典被后台线程裸改）。
//   logForm 只在 TrayApp 构造里 new 出来、从不 Show，所以它**必然**没有句柄 —— 这条路径不是"可能"，
//   而是"只要第一个动作是点菜单启动 DSH 就必然发生"。
//
// 为什么这样修：
//   正确锚点是**在 UI 线程上显式创建过句柄的隐藏控件**。它的句柄必然存在（见 Ready），
//   因此 InvokeRequired 的语义总是可信；它不承载任何业务，不会被业务代码意外 Dispose 或 Hide。
//   用 SynchronizationContext 也能工作，但自建锚点可以**断言**（Ready / AnchorThreadId），
//   而 SynchronizationContext 的内部 marshaling 控件是黑盒 —— 自检需要可断言。
public sealed class UiDispatcher : IDisposable {
  readonly Control anchor;
  int _reporting;

  public int AnchorThreadId { get; }

  // 锚点句柄已建立（正常应恒为 true；false 表示构造时不在真正的 UI 线程上）
  public bool Ready { get { try { return anchor.IsHandleCreated; } catch { return false; } } }

  // 当前线程是否需要 marshal（自检用；句柄未就绪时不代表"无需 marshal"，故必须先看 Ready）
  public bool RequiresMarshal { get { try { return anchor.InvokeRequired; } catch { return false; } } }

  // 当前就在锚定线程上
  public bool OnUiThread { get { return Environment.CurrentManagedThreadId == AnchorThreadId; } }

  // 调度失败或回调抛异常时的上报口（宿主注入；默认静默）
  public Action<string, Exception> ErrorHandler;

  public UiDispatcher() {
    AnchorThreadId = Environment.CurrentManagedThreadId;
    anchor = new Control();
    try { var h = anchor.Handle; } catch { }   // ← 关键：在**当前线程**（必须是 UI 线程）强制建句柄
  }

  public void Post(Action a) { Post("ui", a); }

  // 投递到 UI 线程执行。三条路径都不再静默吞异常：
  //   ①正常：BeginInvoke 入消息队列（调用方立即返回，委托由消息泵执行）
  //   ②本就在 UI 线程：直接执行
  //   ③锚点句柄异常缺失且不在 UI 线程：**放弃本次更新并如实上报**，而不是退回"在后台线程裸碰控件"
  //     （后者正是原实现的病根：看似降级成功，实则把句柄绑错线程、永久瘫痪后续更新）
  public void Post(string tag, Action a) {
    if (a == null) return;
    try {
      if (!anchor.IsHandleCreated) {
        if (OnUiThread) a();
        else Report(tag + ".anchor", new InvalidOperationException("UiDispatcher 锚点句柄未建立，UI 更新被丢弃"));
        return;
      }
      if (anchor.InvokeRequired) {
        anchor.BeginInvoke((MethodInvoker)(() => { try { a(); } catch (Exception ex) { Report(tag, ex); } }));
      } else {
        try { a(); } catch (Exception ex) { Report(tag, ex); }
      }
    } catch (Exception ex) { Report(tag + ".post", ex); }
  }

  void Report(string tag, Exception ex) {
    if (Interlocked.Exchange(ref _reporting, 1) == 1) return;   // 防"上报自身再抛"导致递归
    try { try { if (ErrorHandler != null) ErrorHandler(tag, ex); } catch { } }
    finally { Interlocked.Exchange(ref _reporting, 0); }
  }

  public void Dispose() { try { anchor.Dispose(); } catch { } }
}
