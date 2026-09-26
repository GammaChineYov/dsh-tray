namespace QwenTray;

/// <summary>
/// 托盘下拉菜单「点了关不关」的唯一裁决器（纪律唯一源：~/.workbuddy/skills/tray-menu-keepopen）。
/// 三件套：① Closing 遇 ItemClicked 一律先拦（菜单先别关）；② AppFocusChange 只在"500ms 内真按过本菜单项"
/// 时才拦（否则一律拦 = 钉子）；③ 被拦下的③类项由我们 BeginInvoke **延后**补关。
///
/// 🔴 为什么必须是"先拦后判"（2026-09-12 实拍复盘，代价是用户一句"那我就不用它了"）：
///   WinForms 默认语义是"点中任一项 ⇒ 整条下拉链关掉"。对"执行一个命令"是对的，
///   但「批量勾选 / 连点启停」不是一组命令 —— 它是一次连续操作。默认语义下用户必须把
///   "移到托盘→右键→移到子菜单→悬停→移到项→点击"整套重来 N 遍（实测 30 动作 / 5 次跨屏移动）。
///   模板 cmd-tray 的勾选项是"一条一开关的设置项"，它这么写没错 —— 我们抄了机制、没注意语义变了。
///
/// 🔬 实测时序（决定了实现只能长这样）：**Closing 先于 Click**。点勾选项 =
///   子菜单 Closing(ItemClicked) → 根菜单 Closing(AppFocusChange) → 项的 Click。
///   所以"在 Click/CheckedChanged 里记标记、再到 Closing 里查标记"这条路**根本走不通**（第一版就这么废的）。
///   真鼠标又和假路径相反：真左键 = 根菜单 Closing(AppFocusChange) **先** → 子菜单 Closing(ItemClicked)
///   ⇒ 只拦 ItemClicked 已经太晚（根菜单已关 + 留下**孤儿子菜单窗口**）。两个原因都要拦；
///   但一律拦 AppFocusChange = 造钉子 ⇒ 闸门 = "刚刚（500ms 内）真的按下过本菜单里的项"
///   （MouseDown 必然早于两次 Closing；点别处 / Alt+Tab / Esc 没有这个新鲜印记 ⇒ 照关）。
///
/// 类判据（①②类不关 / ③类关）由 IsState 决定，**两个落点**：
///   路径 A 整段保持 —— DeclareWholeState(dd)：该下拉里每一项都是①②类（纯设置/控制面板）；
///   路径 B 指定项保持 —— DeclareStateItems(items)：混排下拉里显式点名（dshMenu、每模型二级菜单、cmd-tray 指令项…）；
///   兜底 CheckOnClick —— 勾选项天然是①类（decision-tray 的复选项、cmd-tray 的两个开关项自带此标记）。
/// ⚠️ 未声明的项 **fail-safe 当③类（点它必关）** ⇒ 漏声明只会"多点一次右键"，**绝不会变成钉子**。
/// ⚠️ 动态插入的项（dsh 的「最近会话」）在**创建处**用 WireNew(...) 就地补挂（拿得到新项、时机最早）；
///   它所在的下拉同样被 Hook(menu) 递归覆盖 ⇒ 两条路都通、互为兜底（Hook 会递归到 dshMenu.DropDown）。
/// ✅ Hook **可安全重复调用**：下拉级（_attached）与项级（_wired）**均为弱键幂等表** ⇒
///   调用方可以在**每次菜单重建之后**安全地重复调用 Hook —— 旧树随 GC 回收、不被本类吊住，
///   同一棵树上已挂过的项也不会重复订阅（项级幂等是无界事件订阅的安全兜底）。
/// ❌ 已否决：ToolStripDropDown.AutoClose = false（状态项确实不关了，但**切到别的窗口菜单还赖在屏幕上** =
///   钉子，比原缺陷更糟）；"Click 里记标记 → Closing 里查"（Closing 先到）；同步 root.Close()（抽地板→原生崩溃）。
/// </summary>
sealed class MenuKeepOpen
{
    /// <summary>"刚刚点了本菜单里的项"的新鲜度闸门（毫秒）。见类注释最后一条实测。</summary>
    public const int ClickGateMs = 500;

    // 下拉级去重（Attach 幂等）：**弱键**表 + 自维护计数（CWT 没有 Count）。
    //   为什么必须弱键：调用方每轮重建菜单会 new 出一批**全新下拉**，若用普通 HashSet 去重，
    //   会把上一轮的旧下拉一直吊住（并经 OwnerItem 反引整棵旧子树）⇒ 跨 Build 内存泄漏、阻止回收。
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolStripDropDown, object> _attached = new();
    int _attachedCount;                                        // 首次成功挂载时 ++；外部语义与旧 HashSet.Count 一致（历史累计挂过的下拉数）
    // 项级去重（WireItem 幂等）：同样**弱键**表 —— 键（被订阅的项）被 GC 回收时表项自动消失，不强引用旧项。
    //   为什么必须弱键：调用方每轮重建菜单会换一批**全新项对象**，若用普通 HashSet<ToolStripItem>
    //   去重，会把上一轮的旧项一直吊住（连同其原生句柄）⇒ 跨 Build 泄漏、阻止回收。
    // 为什么不用 Tag 承载标记：部分调用方的 Tag 已被业务数据占用 ⇒ 判据不能落在 Tag 上（三份拷贝须同形）。
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolStripItem, object> _wired = new();
    static readonly object WiredMark = new();                  // 两张弱键表共用的占位值（只判断"在不在"，不看值）
    readonly HashSet<ToolStripDropDown> _wholeState = new();   // 路径 A：整段都是①②类的下拉
    HashSet<ToolStripItem> _state = new();                     // 路径 B：显式点名的①②类项（每次 Declare 整体替换）
    DateTime _lastAct = DateTime.MinValue;
    // 【②的语义闸门】最近一次**左键按下**落在哪个项上；松手（MouseUp）即清。
    // 🔴 为什么光有时间不够（2026-09-12 用户实报「点空白处菜单不消失」）：
    //    旧的 ② 只看"500ms 内 _lastAct 有没有被刷过"，而 `Touch()` 挂在 `mi.MouseDown` 上 ——
    //    ① 右键/中键按在项上（含"在项上再点一次右键"这种习惯动作）也会刷新它；
    //    ② 它**不区分**"刚碰过本菜单项"与"点的是菜单外" ⇒ 点菜单外的空白时会被误拦 ⇒ 菜单成了钉子。
    //    收紧后：点菜单外的空白**没有按下印记**（MouseUp 已清）⇒ 一律照关。
    ToolStripItem? _pressed;

    // —— 诊断面：让自检能断言"机制真的挂上了"（不参与运行时判定，零逻辑影响）——
    public int AttachedDropdowns => _attachedCount;    // 挂到 Closing 的下拉数（历史累计；CWT 无 Count ⇒ 自维护）
    public int StateItemCount   => _state.Count;       // 显式①②类项数
    public int WiredItems       { get; private set; }  // 自最近一次 Hook 起遍历到的项数
    public int ItemSubscriptions{ get; private set; }  // 累计**真实发生过**的项级订阅数（幂等 ⇒ 重复 Hook 不再增长）

    // ——————————————— 纯函数（自检直接打这三条，不弹菜单）———————————————
    /// <summary>①类裁决：ItemClicked 一律拦；AppFocusChange / Keyboard 等一律不拦。</summary>
    public static bool ShouldCancel(ToolStripDropDownCloseReason reason)
        => reason == ToolStripDropDownCloseReason.ItemClicked;

    /// <summary>②类裁决：AppFocusChange 只在"刚刚点过本菜单"时才拦（否则会把菜单变成钉子）。</summary>
    public static bool ShouldCancelFocusChange(double msSinceClick) => msSinceClick < ClickGateMs;

    /// <summary>合并闸门（`OnClosing` 真正用的那一条）：**要不要把这次关闭拦下来**。
    /// <para>① <c>ItemClicked</c>（点了本菜单里的项）⇒ 一律先拦：①②类项要保持开着，③类项由我们补关。</para>
    /// <para>② <c>AppFocusChange</c>（点了别处 / Alt+Tab / 切窗口）⇒ **只有"刚刚真的在本菜单项上按下过左键"才拦**
    /// （真左键点菜单项时，根菜单收到的正是它 ⇒ 不拦就会留下孤儿子菜单）。</para>
    /// <para>🔴 点菜单外的空白 ⇒ 没有按下印记（MouseUp 已清）⇒ <b>照关</b>。这是"菜单绝不能变成钉子"的机械保证，
    /// 也是 2026-09-12「点空白处不自动隐藏」的修复点 —— <b>别退回"只看时间"的写法</b>。</para>
    /// <para>其它 reason（Keyboard / AppClicked / CloseCalled…）一律不拦。</para></summary>
    public static bool ShouldCancelClose(ToolStripDropDownCloseReason reason, bool hasPressMark, double msSincePress)
        => ShouldCancel(reason)                                                            // ①
           || (reason == ToolStripDropDownCloseReason.AppFocusChange                       // ②
               && hasPressMark && ShouldCancelFocusChange(msSincePress));

    /// <summary>③类裁决：被拦下的非状态项必须由我们负责补关（漏了就是"点完赖着不走"）。</summary>
    public static bool ShouldCloseOnClick(bool isState) => !isState;

    // ——————————————— 声明面（构造/装配时调用）———————————————
    /// <summary>路径 A：这些下拉里每一项都是①②类 ⇒ 整段保持。</summary>
    public void DeclareWholeState(params ToolStripDropDown[] dds)
    { foreach (var d in dds) _wholeState.Add(d); }

    /// <summary>路径 B：混排下拉里"哪些项是①②类"。**整体替换**，因此可在每次重建菜单时重新声明（不累积旧对象）。</summary>
    public void DeclareStateItems(IEnumerable<ToolStripItem> items)
    { _state = new HashSet<ToolStripItem>(items ?? Array.Empty<ToolStripItem>()); }

    /// <summary>①②类判定（路径 A → 路径 B → CheckOnClick 兜底）。public 供自检逐项核对。</summary>
    public bool IsState(ToolStripDropDown owner, ToolStripMenuItem mi)
        => _wholeState.Contains(owner) || _state.Contains(mi) || mi.CheckOnClick;

    // ——————————————— 挂载面 ———————————————
    /// <summary>给**一棵新建的树**挂裁决（根 + 递归所有子菜单 + 每个项）。
    /// 调用时机与树的生命周期无关：静态树可只调一次，每轮重建出**全新树**的调用方则在每次重建后各调一次。
    /// **幂等**：下拉级（_attached）+ 项级（_wired）双重弱键去重 ⇒ 对同一棵树重复调用也不会重复订阅。</summary>
    public void Hook(ToolStripDropDown root)
    {
        WiredItems = 0;
        Attach(root);
        Wire(root, root, root.Items);
    }

    /// <summary>动态插入的项：在**创建处**就地补挂（见 §三）。owner = 它所在的那个下拉。</summary>
    public void WireNew(ToolStripDropDown root, ToolStripDropDown owner, ToolStripItem item)
    { if (item is ToolStripMenuItem mi) WireItem(root, owner, mi); }

    void Attach(ToolStripDropDown dd)
    {
        // 同一个 dropdown 只挂一次：重复订阅会让 Closing 被裁决多次。
        // GetValue 只在"键不存在"时回调一次 ⇒ 订阅与计数恰好一次；弱键 ⇒ 旧下拉不被吊住。
        _attached.GetValue(dd, _ => { dd.Closing += OnClosing; _attachedCount++; return WiredMark; });
    }

    void Wire(ToolStripDropDown root, ToolStripDropDown owner, ToolStripItemCollection col)
    {
        foreach (ToolStripItem it in col)
        {
            if (it is not ToolStripMenuItem mi) continue;   // ToolStripSeparator / ToolStripControlHost 跳过
            WireItem(root, owner, mi);
            if (!mi.HasDropDownItems) continue;
            Attach(mi.DropDown);
            Wire(root, mi.DropDown, mi.DropDownItems);
        }
    }

    void WireItem(ToolStripDropDown root, ToolStripDropDown owner, ToolStripMenuItem mi)
    {
        WiredItems++;
        // 项级幂等（P1 根治）：同一项只订阅一次。Hook 会被反复调用（每次菜单重建之后都会走到这里），
        // 不去重就是"每周期给全树每项 +1 条处理器"的无界增长 ⇒ 长跑托盘点击逐次变慢。
        // 单步原子（P2 收紧）：GetValue 只在"键不存在"时回调一次 ⇒ 订阅与计数恰好一次，
        // 也不会像 TryGetValue + Add 两步那样在并发下撞上"重复键 Add 抛 ArgumentException"。
        // _wired 是弱键表：旧项随 GC 回收，不跨 Build 泄漏。
        // 前置条件：本类只由 UI 线程调用（Hook/WireNew 的调用方都在消息泵线程上）。
        _wired.GetValue(mi, _ => WireOnce(root, owner, mi));
    }

    /// <summary>真正挂事件（每项全生命周期恰好一次）。返回占位值供 GetValue 存表。</summary>
    object WireOnce(ToolStripDropDown root, ToolStripDropDown owner, ToolStripMenuItem mi)
    {
        ItemSubscriptions++;
        // 留印记：真鼠标按下必然早于两次 Closing（这是 AppFocusChange 那道的闸门）。
        // ⚠️ 只认**左键**：右键/中键按在项上不该刷新印记 —— 否则用户随后点菜单外的空白会被闸门误拦
        //    （2026-09-12 用户实报「点空白处菜单不消失」，就是这条 + 下面 MouseUp 的缺失一起造成的）。
        mi.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _pressed = mi; Touch(); } };
        // 松手即清：点完一项（或只是点了一下没选中）之后，印记立刻作废 ⇒ 再点菜单外的空白一律照关。
        // 真左键时序里 Closing 早于 MouseUp（MouseDown → 根 Closing(AppFocusChange) → 子 Closing(ItemClicked)
        // → Click → MouseUp），所以清空**不会**削弱"点项时根菜单该被拦住"这条。
        mi.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) _pressed = null; };
        // 程序化 PerformClick / 键盘激活没有 MouseDown ⇒ ①②类项在 Click 上再补一次印记。
        if (IsState(owner, mi)) mi.Click += (_, _) => { _pressed = mi; Touch(); };
        // ①②类：点完**什么都不做** ⇒ 菜单保持开着（这就是整个类存在的理由）。
        // ③类：它本该"点完就关"，但关闭被我们拦下了 ⇒ 由我们负责补关。
        // 🔴 **父项（带子菜单）默认不补关**（2026-09-12 实测结论，别让后人改回去）：
        //   程序化实测父项 `PerformClick` **会触发 Click**（探针隔离菜单计数）⇒ 原先它也落到下面那条③类补关，
        //   于是"点一级项 = 整条菜单关"（用户实感"点了就消失"）。但父项的语义是**导航到子菜单**：
        //   点击的意思就是"展开"，菜单应当留着；对照实测：不挂本机制时探针里父项 `PerformClick` 后 `IsVisible` 仍 True。
        //   ⚠️ 挂了快捷启动（一级项未启动时点它=启动）的父项是例外：动作**真触发**时由**调用方**自己补一次
        //      **延后**关闭（`BeginInvoke`；绝不同步 Close()——同步关会 Dispose 掉正在处理点击的那个项）。
        else if (mi.HasDropDownItems) { /* 父项：点击 = 展开子菜单，菜单留着；**不补关** */ }
        else                    mi.Click += (_, _) => CloseSoon(root);
        return WiredMark;
    }

    void OnClosing(object? s, ToolStripDropDownClosingEventArgs e)
    {
        double ms = (DateTime.Now - _lastAct).TotalMilliseconds;
        bool cancel = ShouldCancelClose(e.CloseReason, _pressed != null, ms);
        if (cancel) e.Cancel = true;
        Trace(s as ToolStripDropDown, e.CloseReason, cancel, ms);
    }

    /// <summary>取证日志（默认开，`DSHTRAY_MENULOG=0` 关）：每次 Closing 落一行到 <c>%TEMP%\dshtray-menulog.txt</c>。
    /// 为什么要有它：「菜单在真托盘里不关 / 该关不关」这类症状**只能在常驻进程 + 真鼠标下复现**
    /// （程序化 PerformClick 的 Closing 时序与真左键相反），不先拿到 <c>CloseReason</c> 与"拦没拦"，
    /// 任何改法都是猜。低频（每次菜单关闭一行），开销可忽略；>512KB 自滚动重开。</summary>
    void Trace(ToolStripDropDown? dd, ToolStripDropDownCloseReason reason, bool cancel, double ms)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("DSHTRAY_MENULOG") == "0") return;
            var p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dshtray-menulog.txt");
            var fi = new System.IO.FileInfo(p);
            if (fi.Exists && fi.Length > 512 * 1024) { try { fi.Delete(); } catch { } }
            System.IO.File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff")
                + " | " + reason + (cancel ? "  CANCELLED" : "  closed")
                + " | msSincePress=" + (int)ms
                + " | pressed=" + (_pressed?.Text ?? "-")
                + " | dd=0x" + (dd != null && dd.IsHandleCreated ? dd.Handle.ToInt64().ToString("X") : "-")
                + Environment.NewLine);
        }
        catch { }
    }

    void Touch() => _lastAct = DateTime.Now;

    /// <summary>延后关闭：**绝不**在项的 Click 处理器里同步 Close()。同步关会连锁触发
    /// Closed → Rebuild → Dispose 掉正在处理点击的这个项 = 在自己脚下抽地板（原生崩溃成因）。</summary>
    static void CloseSoon(ToolStripDropDown root)
    {
        try { if (root.IsHandleCreated) { root.BeginInvoke(new Action(() => { try { root.Close(); } catch { } })); return; } }
        catch { }
        try { root.Close(); } catch { }
    }
}
