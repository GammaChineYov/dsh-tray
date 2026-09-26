namespace QwenTray;

/// <summary>
/// 菜单落点护栏：把菜单整体挪进**工作区**，别让它伸进任务栏。
///
/// 🔑 为什么需要它（2026-09-12 本家族在 cmd-tray 上先出症状「鼠标移上去后『退出』被任务栏盖住」，
/// 三托盘同一套 `NotifyIcon + ContextMenuStrip` ⇒ 同一缺陷，故一并上护栏）：
/// 通知区图标**长在任务栏里**，任务栏在**工作区之外**（本机实测：屏 2560×1440、工作区底 1392、
/// 任务栏占 1392~1440 那 48px）。`NotifyIcon` 那条路把菜单**右下角直接贴在光标**上、
/// **完全不做工作区钳位**（在 cmd-tray 上当场量过：光标 (2540,1429) ⇒ 菜单 (2227,1011)-(2540,1429)，
/// 侵入 37px）⇒ 菜单**最后一行**落在任务栏条里被吃掉。菜单越高越好不了：
/// 侵入量恒等于"光标比工作区底低多少"。
///
/// 与 <see cref="TipPlace"/> 同构：**几何裁决是纯函数**（可无窗口断言），窗口动作用 IO 薄壳包住。
/// 分工很硬：本文件只负责"挪进工作区"，**不做方向翻转、不做子菜单避让** ——
/// 那些是 ToolStrip 原生语义，动了就是另一个缺陷（"用新问题解决旧问题"，本仓踩过）。
/// </summary>
static class MenuPlace
{
    /// <summary>纯函数：把 <paramref name="menu"/> 挪进 <paramref name="wa"/> 所需的**最小位移**。
    /// 不需要挪 ⇒ <see cref="Point.Empty"/>（**不许无谓挪动**：位置一变，用户的肌肉记忆就废了）。</summary>
    public static Point Offset(Rectangle menu, Rectangle wa)
    {
        if (wa.Width <= 0 || wa.Height <= 0) return Point.Empty;

        int dx = 0, dy = 0;
        // ① 先贴底/贴右 —— 托盘在右下角，菜单天生是往下方/右方伸出去的
        if (menu.Bottom > wa.Bottom) dy = wa.Bottom - menu.Bottom;
        if (menu.Right > wa.Right) dx = wa.Right - menu.Right;
        // ② 再保证不越出左上。菜单比工作区还高/宽时两条约束不可能同时满足，
        //    这里以"看得见开头"为准（原生也是这个取向）：宁可截底，不可截顶。
        if (menu.Top + dy < wa.Top) dy = wa.Top - menu.Top;
        if (menu.Left + dx < wa.Left) dx = wa.Left - menu.Left;
        return new Point(dx, dy);
    }

    /// <summary>给一棵下拉挂上"落点护栏"。同一棵下拉重复调用是幂等的 —— 挂两次会把它真的挪两下。</summary>
    public static void Guard(ToolStripDropDown dd)
    {
        if (Guarded.TryGetValue(dd, out _)) return;
        Guarded.Add(dd, Marker);
        dd.Opened += (_, _) => Apply(dd);
        // 🔴 只在 `Opened` 上贴一次胶布**不够**：`OnTick()` 每秒都调 `_menu.RefreshTexts()`，
        //    菜单**开着**时改文字会让 ToolStrip 重新量尺；实测框架会**按原始锚点重新落位**
        //    （= 把光标点重新当右下角）⇒ 把刚才的修正顶掉。所以尺寸一变就再修一次。
        //    ⚠️ 不挂 `LocationChanged`：那是框架自己的落位动作，跟着它跑会变成两边互相顶（抖动）。
        //       尺寸变化是"重排"的可靠信号，够用且不打架。
        dd.SizeChanged += (_, _) => { if (dd.Visible) Apply(dd); };
    }

    /// <summary>把一棵下拉树**整棵**挂上护栏（根 + 所有子菜单）。
    /// 子菜单同样会被框架按原始锚点落位 ⇒ 靠屏幕底部的项展开时，子菜单下半截也会伸进任务栏。</summary>
    public static void GuardAll(ToolStripDropDown root)
    {
        Guard(root);
        foreach (ToolStripItem it in root.Items)
            if (it is ToolStripDropDownItem { DropDownItems.Count: > 0 } ddi) GuardAll(ddi.DropDown);
    }

    // 用弱表而不是 `HashSet`：`CmdMenu` 在结构变更（注册/卸载指令）时整棵重建菜单，
    // 强引用会把每次重建的旧菜单永久钉在内存里 —— 那就是泄漏。弱表随对象回收自动清，无需摘钩。
    static readonly object Marker = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolStripDropDown, object> Guarded = new();

    /// <summary>把下拉挪进**它自己所在那块屏**的工作区。返回是否真的挪了（自检与取证都读它）。
    /// ⚠️ 屏按**菜单矩形**选而不是按光标选：生产路径（NotifyIcon 自己 Show）里两者同屏，
    /// 但按矩形选是**确定性的** —— 自检不用去搬用户的光标就能拿到同一个答案。</summary>
    internal static bool Apply(ToolStripDropDown dd)
    {
        if (_applying) return false;                 // 重入闸：SetWindowPos 引发的消息不许再进一次
        if (!dd.IsHandleCreated) return false;
        if (!NativeMethods.GetWindowRect(dd.Handle, out var r)) return false;

        var menu = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        var wa = Screen.FromRectangle(menu).WorkingArea;
        var d = Offset(menu, wa);
        if (d.X == 0 && d.Y == 0) return false;

        _applying = true;
        try
        {
            bool ok = NativeMethods.SetWindowPos(dd.Handle, IntPtr.Zero, menu.Left + d.X, menu.Top + d.Y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            if (ok) Applied++;
            return ok;
        }
        finally { _applying = false; }
    }

    [ThreadStatic] static bool _applying;

    /// <summary>整棵树（根 + 全部子菜单）是否都挂上了 —— 自检断言用（漏挂 = 静默退回缺陷）。</summary>
    public static bool AllGuarded(ToolStripDropDown root)
    {
        if (!IsGuarded(root)) return false;
        foreach (ToolStripItem it in root.Items)
            if (it is ToolStripDropDownItem { DropDownItems.Count: > 0 } ddi && !AllGuarded(ddi.DropDown)) return false;
        return true;
    }

    /// <summary>诊断面（只给自检读）：这棵下拉**真的挂上护栏了吗**。
    /// 存在的理由：护栏是"接线型"修复（挂事件即隐身），一旦将来有人重构菜单构建流程忘了挂，
    /// 行为会**静默退回缺陷**而自检仍全绿 —— 所以每个仓都要有一条"挂上了"的机械断言。</summary>
    public static bool IsGuarded(ToolStripDropDown dd) => Guarded.TryGetValue(dd, out _);

    /// <summary>诊断面（只给自检读）：真的挪过几次。
    /// 用途是**抓抖动**：护栏要是和框架的"重排"打起来（我挪回去、它挪出来）就会无限涨。
    /// 不参与运行时判定，零逻辑影响。</summary>
    internal static int Applied;

    /// <summary>纯函数：给"自己弹菜单"的路径算一个**已钳进工作区**的锚点。
    /// 原生路径（NotifyIcon 自己 Show）改不了锚点，只能靠 <see cref="Apply"/> 事后挪；
    /// 我们自己的 Show 则直接从这个锚点弹，省掉一次可见的移动。</summary>
    public static Point ClampAnchor(Point cursor, Rectangle wa)
    {
        int x = cursor.X, y = cursor.Y;
        // 只收"光标伸进了任务栏/屏外"这一种情况；正常位置一律不动
        if (y > wa.Bottom - 1) y = wa.Bottom - 1;
        if (x > wa.Right - 1) x = wa.Right - 1;
        if (y < wa.Top) y = wa.Top;
        if (x < wa.Left) x = wa.Left;
        return new Point(x, y);
    }
}
