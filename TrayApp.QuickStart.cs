using System;
using System.Threading;
using System.Windows.Forms;

namespace QwenTray;

// —— 一级菜单项「未启动时点击 = 快捷启动」（2026-09-12）——
//
// 动机（交互成本 / 最省力法则）：一级项（DSH、各模型）在菜单里位置固定、抬手就到；
//   而「启动 DSH」「启动模型」藏在子菜单里 —— 要先展开、再视觉搜索、再瞄准那一项。
//   把"启动"提到一级项 ⇒ 省掉「搜索 + 瞄准」。注意这才是真实收益，不是"少一次点击"：
//   悬停本来就会展开子菜单，所以点击次数没变，变的是**手要移动多远、要瞄多准**。
//
// 判据（本功能的铁律 —— 必须与用户**看到**的一致）：
//   只有**子菜单里那一项当前可点**时才触发 ——
//     模型 = SvcStatus.Enable(starting,running,busy).start（即 m.start.Enabled）
//     DSH  = dshState==0（即 dshStartItem.Enabled）
//   ⇒ 圆点是红的（可启动）点一级项就启动；黄/绿/橙（启动中/已启动/端口被占）只走原行为（展开子菜单）。
//   ⚠️ 判据一律取**缓存的运行时状态**（Tick 里后台探针刷新的那个），绝不在 UI 线程现探端口 ——
//      SvcProbe.PortUp / DshUp 都是同步 HTTP/TCP 探测，在"点菜单那一刻"调会把 UI 冻住。
//
// 叠加式（不动既有语义）：**不改变任何菜单展开/关闭行为** —— 子菜单照常弹出、菜单该关还关。
//   本文件只"顺手把启动指令发出去"，所以 MenuKeepOpen 的三类判据**一个都不改**（一级项仍是③类）。
//   回执：**气泡（主回执）** + 日志 `[快捷启动] …`。
//   ⚠️ 别把"子菜单里的状态行"当成必然回执：一级项是③类，`Click` 触发时裁决器会补关菜单
//      （程序化路径实测如此）⇒ 菜单可能已经关了。真正的即时回执是气泡。
//
// 幂等：Click 与 MouseUp **两路都挂**（父项点它到底触发不触发 Click 依 WinForms 实现而异 ⇒ 两路兜底），
//   共用一个 1500ms 闸门 ⇒ 一次点击最多放行一次；连点也不会重复拉起进程。
//   start 侧还有第二层守卫：Start(svc) 自己会拒"已在运行/端口被占"、DshStart() 自己会拒"3080 已在听"。
public partial class TrayApp {

  /// <summary>已接线的快捷启动一级项数（诊断用；探针断言 = 1(DSH) + 每模型）。</summary>
  int quickStartWired;

  /// <summary>给一级项挂「未启动时点击 = 快捷启动」。
  /// canStart：判据（**必须与子菜单里那个「启动」项的 Enabled 同源**）；start：真启动（内部自行 Bg 异步）。
  /// ⚠️ 同一个一级项只可调用一次 —— 闸门是每个调用点的闭包局部变量，重复挂 = 两个闸门 = 可能双触发。</summary>
  void WireQuickStart(ToolStripMenuItem root,Func<bool> canStart,Action start,string what){
    long gate=0;   // Environment.TickCount64；Click / MouseUp 双路共用（局部 ⇒ 每个一级项各自独立）
    void Try(){
      long now=Environment.TickCount64;
      if(now-Interlocked.Read(ref gate)<1500) return;   // 同一击的第二路 / 连点 ⇒ 丢弃
      if(!canStart()) return;                           // 启动中 / 已启动 / 端口被占 ⇒ 不触发（只走原行为）
      Interlocked.Exchange(ref gate,now);
      try{ icon?.ShowBalloonTip(2500,"DSH托盘",what+" 正在启动…（进度见日志窗口）",ToolTipIcon.Info); }catch{}
      try{ logForm?.Append("[快捷启动] 点一级项「"+root.Text+"」⇒ "+what+"未启动，直接发启动\r\n"); }catch{}
      try{ start(); }catch(Exception ex){ ReportEx("QuickStart",ex); }
      // 父项**默认不补关**（见 MenuKeepOpen.WireOnce：点父项 = 展开子菜单，菜单留着）；但挂了快捷启动的
      // 父项，动作**真触发**了（= 用户其实是按了"启动"）⇒ 得由我们补一次关闭。
      // ⚠️ 必须**延后**（BeginInvoke）：同步 Close() 会连锁 Closed→Rebuild→Dispose 掉**正在处理这次点击的项**
      //    = 在自己脚下抽地板（原生崩溃成因）。这里拿的是该项所属的下拉（一级项的 Owner 就是根菜单）。
      try{
        if(root.Owner is ToolStripDropDown dd && dd.IsHandleCreated)
          dd.BeginInvoke(new Action(()=>{ try{ dd.Close(); }catch{} }));
      }catch{}
    }
    // 真鼠标路径：MouseUp 一定会来（悬停不产生 MouseUp ⇒ 不会误触发）。
    root.MouseUp+=(s,e)=>{ try{ if(((MouseEventArgs)e).Button==MouseButtons.Left) Try(); }catch{} };
    // 键盘/程序化路径：PerformClick / Enter 只有 Click（没有 MouseUp）⇒ 两路都要。
    root.Click  +=(s,e)=>{ try{ Try(); }catch{} };
    quickStartWired++;
  }
}
