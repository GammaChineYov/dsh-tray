using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;

namespace QwenTray;

// —— 菜单项悬停提示：**自绘**（位置自己算）2026-09-12 ——
//
// 🔴 纪律（唯一源：skill `tray-menu-keepopen` 铁律 8「悬停提示窗不得压光标」）：
//    悬停即显的窗口 **绝不允许压住光标**（也不允许压住被悬停的那一行），否则就是
//    "自己把自己吓回去"的振荡回路：
//      提示弹出 → 压住光标/那一行 → 底下元素丢 hover → 提示收起 → hover 恢复 → 提示再弹 → …（= 闪）
//    平台自带的 item tooltip 摆位在 ToolStrip 内部，只保证"不出屏"、**不保证"不压光标"**
//    （家族第一起事故就是本托盘的参数面板 GPU 勾选项，当时用"去掉 ToolTipText"绕开 —— 绕开≠修，
//      所以同一张菜单里"有的格位避让、有的不避让"，靠运气），因此：
//      ① 关掉平台渲染：每个下拉 **各自** 一个 `ShowItemToolTips=false`（父级设了不继承）；
//      ② 位置交 `TipPlace`（硬判据：不压 24px 光标安全区、不压锚点矩形；全废才兜底且确定）；
//      ③ 窗口 `WS_EX_TRANSPARENT`（见 RichTip.CreateParams）：哪怕兜底压住了光标也吃不到鼠标 ⇒ 不会振荡。
//    ⚠️ `ToolTipText` **照旧要设** —— 它从"渲染源"降级为**数据源**（本文件与自检都读它），别删、也别把开关改回 true。
public partial class TrayApp {
  RichTip? _tipWin;                                   // 自绘提示窗（懒建，全托盘共用一个）
  ToolStripItem? _tipItem;                            // 当前提示归属于哪一项（"变了才动"的依据）
  bool _tipMouseMoveWired;                            // 根菜单的 MouseMove 兜底只挂一次
  // 项级去重：每轮重建（最近会话）会换一批**全新项对象**，弱键表 ⇒ 旧项随 GC 回收，不跨 Build 泄漏。
  readonly ConditionalWeakTable<ToolStripItem, object> _tipWired = new();
  static readonly object TipWiredMark = new();
  int _tipsAttached;                                  // 最近一次 AttachMenuTips 挂过的下拉数（诊断面）

  // —— 诊断面（只给自检读；不参与运行时判定）——
  internal int TipDropDownAttached => _tipsAttached;
  internal int TipWiredItems { get; private set; }
  internal int TipShownCount { get; private set; }
  internal int TipHiddenCount { get; private set; }
  internal bool TipWindowVisible { get { try { return _tipWin != null && _tipWin.Visible; } catch { return false; } } }
  /// <summary>窗口**真的**可见（读 Win32，不是托管状态标记）—— 判"提示窗撤没撤下"用它，防假红。</summary>
  internal bool TipWindowReallyVisible { get { try { return _tipWin != null && _tipWin.IsHandleCreated && NativeMethods.IsWindowVisible(_tipWin.Handle); } catch { return false; } } }
  /// <summary>提示窗真实显示/收起计数（见 <see cref="RichTip.ShowCount"/>）；自检用它断言 Show/Hide 成对。</summary>
  internal int TipWinShowCount { get { try { return _tipWin?.ShowCount ?? 0; } catch { return 0; } } }
  internal int TipWinHideCount { get { try { return _tipWin?.HideCount ?? 0; } catch { return 0; } } }
  internal void ResetTipCounters() { TipShownCount = 0; TipHiddenCount = 0; }

  /// <summary>
  /// 递归给**整棵树**挂自绘提示：每个下拉关掉平台渲染（各自的开关！），每个项挂 MouseEnter/MouseLeave。
  /// 调用时机与 <see cref="MenuKeepOpen.Hook"/> 相同（构造末尾 + 每次动态重建之后）——**幂等**：
  /// 下拉级由 `_tipMouseMoveWired` 守根菜单、项级由弱键表 `_tipWired` 去重 ⇒ 重复调用不会重复订阅。
  /// </summary>
  internal void AttachMenuTips() {
    if (menu == null) return;
    int dds = 0, items = 0;

    void WireItem(ToolStripItem it) {
      if (it is ToolStripSeparator) return;
      items++;
      _tipWired.GetValue(it, _ => {
        it.MouseEnter += (s, e) => { _tipItem = it; ShowItemTip(it); };
        it.MouseLeave += (s, e) => { if (ReferenceEquals(_tipItem, it)) _tipItem = null; HideMenuTip(); };
        return TipWiredMark;
      });
    }

    void Walk(ToolStripDropDown dd) {
      dds++;
      // 🔴 平台渲染关掉（**每个下拉各自一个开关**，父级设了不会继承 ⇒ 必须逐个走）。
      dd.ShowItemToolTips = false;
      foreach (ToolStripItem it in dd.Items) {
        WireItem(it);
        // ⚠️ 判"有没有子菜单"必须看 DropDownItems.Count —— `ToolStripMenuItem` 本身就继承自
        //    `ToolStripDropDownItem`，写成类型判断会把**所有**项都当成有子菜单（自检实测踩过）。
        if (it is ToolStripDropDownItem { DropDownItems.Count: > 0 } ddi) Walk(ddi.DropDown);
      }
    }

    Walk(menu);
    _tipsAttached = dds;
    TipWiredItems = items;

    if (!_tipMouseMoveWired) {
      _tipMouseMoveWired = true;
      // 兜底触发：MouseEnter 由 ToolStrip 内部派发，万一某情形漏发，提示会**静默不出现**（不报错）。
      // 只在**当前项变了**时动作（每像素都重摆会让提示跟着抖 = 另一种闪）。
      menu.MouseMove += (s, e) => {
        var hit = menu.GetItemAt(e.Location);
        if (ReferenceEquals(hit, _tipItem)) return;
        if (hit == null) { _tipItem = null; HideMenuTip(); return; }
        _tipItem = hit; ShowItemTip(hit);
      };
      // 寿命：菜单一关，提示必须跟着走 —— 否则它就是桌面上一个没人认领的悬浮窗（钉子）。
      menu.Closed += (s, e) => { _tipItem = null; HideMenuTip(); };
    }
  }

  /// <summary>
  /// 某一项该不该有提示、提示什么、放哪一侧。抽成纯函数给自检断言 ——
  /// **锚点不在里面**：锚点要真实窗口（菜单没 Show 过就算不出来），而"算不出锚点就宁可不显"
  /// 本身也是一条要守的规矩（见 <see cref="ShowItemTip"/>）。
  /// </summary>
  internal static (string Text, bool PreferLeft)? TipPlanFor(ToolStripItem it) {
    if (it is ToolStripSeparator) return null;
    var text = it.ToolTipText;
    if (string.IsNullOrWhiteSpace(text)) return null;
    // ⚠️ 判据是 **有没有子项**，不是"是不是 ToolStripDropDownItem"（见 AttachMenuTips 里的同款注释）。
    bool hasSub = it is ToolStripDropDownItem { DropDownItems.Count: > 0 };
    // 有子菜单的项 ⇒ 提示放左侧：右侧要留给子菜单（放右边会被子菜单盖住）。
    return (text, hasSub);
  }

  /// <summary>该项在**屏幕坐标**里的矩形 —— 摆位判据②（"不压被悬停的那一行"）要的就是它。</summary>
  static Rectangle TipAnchorRect(ToolStripItem it) {
    var owner = it.Owner;
    if (owner == null || !owner.IsHandleCreated) return Rectangle.Empty;
    try { return owner.RectangleToScreen(it.Bounds); } catch { return Rectangle.Empty; }
  }

  /// <summary>把某一项的提示画到它旁边（内容仍取自 <c>ToolTipText</c>：数据源不变）。</summary>
  void ShowItemTip(ToolStripItem it) {
    try {
      if (TipPlanFor(it) is not { } plan) { HideMenuTip(); return; }
      var anchor = TipAnchorRect(it);
      // 锚点算不出来（菜单还没真正显示 / 句柄没建）⇒ **宁可不显示，也不瞎摆一个位置**：
      // 瞎摆正是"压住光标 ⇒ 自振荡"的来源，宁可这次没有。
      if (anchor.Width <= 0 || anchor.Height <= 0) { HideMenuTip(); return; }
      // 🔑 owner 必传 = 该项所属的那个下拉（`ToolStripDropDown` 实现 IWin32Window，正好可当 owner）。
      //    无 owner 地 Show 一个顶层窗口，即使带 WS_EX_NOACTIVATE，也足以让正在显示的菜单把自己判成
      //    "失焦" ⇒ 自关 ⇒ 用户实感的"右键后动一下鼠标菜单就消失"。见 RichTip.ShowNear 的详注。
      ShowMenuTip(plan.Text, anchor, plan.PreferLeft, it.Owner as ToolStripDropDown);
    } catch { /* 提示画不出来不该影响点菜单 */ }
  }

  /// <summary>
  /// 显示一项提示。<paramref name="owner"/> = 它所属的那个下拉 —— **生产路径恒传**（见 <see cref="ShowItemTip"/>）。
  /// <para>⚠️ <c>owner=null</c> 只允许出现在两处：① 通知区图标那条路（只有光标、没有菜单，见
  /// <see cref="RichTip.ShowAt"/>）；② 自检的**负控变体**（证明"还激活"这条修复不是空转）。
  /// 负控不传 owner ⇒ 没有可还的对象 ⇒ 菜单会被自己判成失焦而关掉（decision-tray 实测 0/15 复现）。</para>
  /// </summary>
  void ShowMenuTip(string text, Rectangle anchor, bool preferLeft, ToolStripDropDown? owner) {
    _tipWin ??= new RichTip();
    _tipWin.SetLines(TipLines.FromText(text));
    _tipWin.ShowNear(anchor, preferLeft, owner);
    TipShownCount++;
  }

  void HideMenuTip() {
    TipHiddenCount++;
    _tipItem = null;
    try { _tipWin?.HideTip(); } catch { }
  }

  /// <summary>诊断面：走一遍"某项被悬停"的真实入口（自检环境里没法真悬停）。</summary>
  internal void RaiseItemTip(ToolStripItem it) { _tipItem = it; ShowItemTip(it); }

  // ————————————————— 自检：提示窗四判据（--selftest-tips）—————————————————
  //
  // 为什么非有这一节：这类 bug 的判据是**几何**，不是文案、也不是编译 —— "内容对不对"那类断言
  // 拦不住它（家族第一起事故就是"内容全对、位置压着鼠标在闪"）。所以必须落成机器可跑的判据：
  //   D1 平台渲染关闭（递归断言每个下拉 ShowItemToolTips==false）
  //   D2 几何扫描（真实托盘几何 + ≥2 万组 + 负控）
  //   D3 真实窗口 ExStyle 读回（不是断言"代码里写了 0x20"）
  //   D4 寿命（Show/Hide 计数配对，否则就是游离悬浮窗 = 钉子）
  internal string MenuTipsProbe() {
    var sb = new StringBuilder();
    sb.AppendLine("MENU TIPS PROBE (ascii-anchor; selftest-tips)");
    sb.AppendLine("discipline: ~/.workbuddy/skills/tray-menu-keepopen 铁律8 | impl: TipPlace.cs / RichTip.cs / TrayApp.Tips.cs");
    int pass = 0, fail = 0;
    void Check(string name, bool ok, string detail = "") {
      if (ok) pass++; else fail++;
      sb.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail.Length > 0 ? ("   | " + detail) : ""));
    }
    try {
      // ————————————— D1 平台渲染关闭 —————————————
      sb.AppendLine();
      sb.AppendLine("=== D1 平台渲染关闭：递归断言全树每个 ToolStripDropDown.ShowItemToolTips == false ===");
      var allDd = new List<ToolStripDropDown>();
      var offenders = new List<string>();
      void WalkDd(ToolStripDropDown dd, string path) {
        allDd.Add(dd);
        if (dd.ShowItemToolTips) offenders.Add(path);
        foreach (ToolStripItem it in dd.Items)
          if (it is ToolStripDropDownItem { DropDownItems.Count: > 0 } ddi) WalkDd(ddi.DropDown, path + "/" + (it.Text ?? ""));
      }
      WalkDd(menu, "root");
      // 2026-09-26：每模型的二级菜单已整体取消（一级模型行是叶子项）⇒ 期望不再 +svcMenus.Count。
      // 口径：根 + dshMenu + 8 个参数面板下拉 = 10（推理参数组/GPU/上下文/KV/缓存内存/切分/MTP/内存与缓存）。
      int expDd = 10;
      Check("D1a 全树下拉数 == 10（根 + dshMenu + 8 个参数面板；一级模型行=叶子项，无每模型二级）",
        allDd.Count == expDd, "实得=" + allDd.Count + " 期望=" + expDd);
      Check("D1b 每个下拉的 ShowItemToolTips 均为 false（各自开关，父级设了不继承）",
        offenders.Count == 0, "违规=" + offenders.Count + (offenders.Count > 0 ? ("  首个=" + offenders[0]) : "") + "  下拉总数=" + allDd.Count);
      Check("D1c 接线覆盖 == 树内下拉数（AttachMenuTips 一个都没漏）",
        _tipsAttached == allDd.Count, "attached=" + _tipsAttached + " 树内=" + allDd.Count);

      // ————————————— D2 几何扫描 —————————————
      sb.AppendLine();
      sb.AppendLine("=== D2 几何扫描：任何 光标/尺寸/锚点 组合都不压光标、不压被悬停那一行、不出屏 ===");
      // 真实托盘几何：通知区在右下角 ⇒ 菜单向上弹；就扫"通知区及其上方"这一片（翻边最常出错的地方）。
      var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1032);
      var waSmall = new Rectangle(0, 0, 320, 240);                 // 极端窄屏 ⇒ 走兜底分支
      var sizes = new[] { new Size(240, 60), new Size(560, 400), new Size(240, 900) };
      int x0 = wa.Right - 740, y0 = wa.Bottom - 22;
      int cases = 0, badCursor = 0, badAnchor = 0, offscreen = 0;
      for (int x = x0; x < wa.Right; x += 7)
        for (int y = y0; y < y0 + 70; y += 3) {
          var cursor = new Point(x, y);
          var guard = TipPlace.CursorGuardRect(cursor);
          foreach (var size in sizes)
            foreach (var anchor in new[] {
                       new Rectangle(cursor.X - 200, cursor.Y - 30, 190, 24),    // 菜单项：光标左边那一行
                       new Rectangle(cursor.X - 260, cursor.Y - 300, 250, 24),   // 菜单项：更靠上
                       guard,                                                    // 只知道光标（通知区图标）
                     }) {
              cases++;
              var r = new Rectangle(TipPlace.Place(cursor, size, wa, anchor), size);
              if (r.IntersectsWith(guard)) badCursor++;
              if (anchor != guard && r.IntersectsWith(anchor)) badAnchor++;
              if (r.Left < wa.Left || r.Top < wa.Top || r.Right > wa.Right || r.Bottom > wa.Bottom) offscreen++;
            }
        }
      Check("D2a 扫描组数 ≥ 20000（步进照 cmd-tray ⑧ 节：x 7px / y 3px × 3 尺寸 × 3 锚点）",
        cases >= 20000, "cases=" + cases);
      Check("D2b 任何组合都不与光标安全区（24px 方框）相交", badCursor == 0, cases + " 组，压光标 " + badCursor + " 组");
      Check("D2c 任何组合都不与被悬停项的矩形相交（压住它 ⇒ 它立刻丢 hover ⇒ 自振荡）",
        badAnchor == 0, cases + " 组，压锚点 " + badAnchor + " 组");
      Check("D2d 全都在工作区内（不出屏）", offscreen == 0, cases + " 组，出屏 " + offscreen + " 组");
      Check("D2e 几何取自真实屏幕工作区（通知区在右下角 ⇒ 菜单向上弹）", wa.Width > 0 && wa.Height > 0, "wa=" + wa);
      // 负控：证明判据**会**判违规（否则就是恒真的假断言）。
      var g = TipPlace.CursorGuardRect(new Point(wa.Right - 20, wa.Bottom - 10));
      Check("D2f 负控：故意摆到光标上 ⇒ Violates==true",
        TipPlace.Violates(new Rectangle(wa.Right - 30, wa.Bottom - 20, 240, 60), g, Rectangle.Empty));
      Check("D2g 负控：故意摆到锚点上 ⇒ Violates==true",
        TipPlace.Violates(new Rectangle(0, 0, 100, 20), new Rectangle(-500, -500, 24, 24), new Rectangle(0, 0, 100, 20)));
      Check("D2h 负控：正常摆位 ⇒ Violates==false",
        !TipPlace.Violates(new Rectangle(600, 300, 240, 60), g, Rectangle.Empty));
      // 兜底分支：提示比工作区还大 ⇒ 候选全废 ⇒ 必须"不崩 + 结果确定"（同输入同输出）。
      var b1 = TipPlace.Place(new Point(10, 10), new Size(900, 700), waSmall, Rectangle.Empty);
      var b2 = TipPlace.Place(new Point(10, 10), new Size(900, 700), waSmall, Rectangle.Empty);
      Check("D2i 兜底：提示比工作区还大时不崩且结果确定（同输入同输出）", b1 == b2, b1.ToString());

      // ————————————— D3 真实窗口属性（读回）—————————————
      sb.AppendLine();
      sb.AppendLine("=== D3 真实窗口属性：CreateHandle 后读回 ExStyle（不是断言'代码里写了 0x20'）===");
      using (var tip = new RichTip()) {
        _ = tip.Handle;                                   // 强制建窗口
        int ex = NativeMethods.GetWindowLong(tip.Handle, NativeMethods.GWL_EXSTYLE);
        string hex = "ExStyle=0x" + ex.ToString("X8");
        Check("D3a 含 WS_EX_TRANSPARENT (0x20)：对鼠标全透明 ⇒ 吃不到消息 ⇒ 不可能自振荡", (ex & 0x20) != 0, hex);
        Check("D3b 含 WS_EX_NOACTIVATE (0x08000000)：不抢焦点 ⇒ 不会关掉用户正开着的菜单", (ex & 0x08000000) != 0, hex);
        Check("D3c 含 WS_EX_TOOLWINDOW (0x80)：不占 Alt+Tab", (ex & 0x80) != 0, hex);
        // 拿**真实**提示尺寸过一遍判据（多行提示比手写偏移更容易压住光标，别让"参数取小了"看着没问题）。
        tip.SetLines(TipLines.FromText("第一条：说明\nz.exe --x\n状态：空闲\n注册：09-12 15:35（3 小时前）\n左键：运行　·　右键：管理"));
        int badReal = 0;
        for (int y = y0; y < y0 + 70; y += 3) {
          var cursor = new Point(wa.Right - 220, y);
          var r = new Rectangle(TipPlace.Place(cursor, new Size(tip.Width, tip.Height), wa,
            new Rectangle(cursor.X - 260, cursor.Y - 30, 250, 24)), new Size(tip.Width, tip.Height));
          if (r.IntersectsWith(TipPlace.CursorGuardRect(cursor))) badReal++;
        }
        Check("D3d 真实提示尺寸（5 行）在通知区各高度上都不压光标", badReal == 0,
          "提示 " + tip.Width + "×" + tip.Height + "，压光标 " + badReal + " 处");
      }

      // ————————————— D4 寿命 —————————————
      sb.AppendLine();
      sb.AppendLine("=== D4 寿命：悬停→显、菜单关闭/离开项→撤（Show/Hide 计数配对，否则 = 游离悬浮窗钉子）===");
      ResetTipCounters();
      bool shownVisible = false, afterCloseVisible = true;
      try {
        _tipWin ??= new RichTip();
        // owner:null —— 这条只验"显/隐计数配对"，没有菜单可还激活（dsh 无菜单的提示路 = ShowAt 同款）。
        ShowMenuTip("寿命探针：这行只用于自检", new Rectangle(wa.Right - 320, wa.Bottom - 140, 300, 24), false, owner: null);
        shownVisible = TipWindowVisible;
        HideMenuTip();                                    // "菜单关闭 / 鼠标离开项"走的就是这一条
        afterCloseVisible = TipWindowVisible;
      } catch { }
      Check("D4a 悬停时提示窗真的显示（证明 D4b/D4c 不是空转）", shownVisible, "Visible=" + shownVisible);
      Check("D4b 菜单关闭/离开项后提示窗已撤下（不是钉子）", !afterCloseVisible, "Visible=" + afterCloseVisible);
      Check("D4c Show/Hide 计数配对（ΔShown==1，ΔHidden≥1，净差==0）",
        TipShownCount == 1 && TipHiddenCount >= 1 && (TipShownCount - TipHiddenCount) == 0,
        "ΔShown=" + TipShownCount + " ΔHidden=" + TipHiddenCount + " 净=" + (TipShownCount - TipHiddenCount));
      // 负控：锚点算不出来（菜单没显示 / 句柄没建）⇒ 宁可不显示，也不瞎摆一个位置。
      int s1 = TipShownCount;
      RaiseItemTip(new ToolStripMenuItem("无宿主的项") { ToolTipText = "不该显示" });   // Owner==null ⇒ 锚点空
      Check("D4d 负控：锚点算不出来 ⇒ 宁可不显示（瞎摆正是振荡的来源）", TipShownCount == s1, "ΔShown=" + (TipShownCount - s1));
      // 接线侧：换渲染器**不丢信息** + 空内容不画空框 + preferLeft 判据（纯函数，不弹窗口）。
      var t = TipLines.FromText("第一行\n第二行\n\n第三行");
      Check("D4e 换渲染器：首行加粗、空行跳过、行数守恒",
        t.Count == 3 && t[0].Bold && t[1].Text == "第二行" && t[2].Text == "第三行");
      Check("D4f 换渲染器：内容为空 ⇒ 零行（不会画出一个空框）", TipLines.FromText("  \n \n").Count == 0);
      var leaf = new ToolStripMenuItem("假叶子项") { ToolTipText = "内容" };
      var parent = new ToolStripMenuItem("假父项") { ToolTipText = "内容" };
      parent.DropDownItems.Add(new ToolStripMenuItem("子"));
      Check("D4g 提示内容取自 ToolTipText（换渲染器不丢信息）",
        TipPlanFor(leaf) is { } pl && pl.Text == leaf.ToolTipText && !pl.PreferLeft,
        TipPlanFor(leaf) is { } pz2 ? ("preferLeft=" + pz2.PreferLeft) : "plan=null");
      Check("D4h 有子菜单的项：提示让到左侧（右侧留给子菜单）", TipPlanFor(parent) is { PreferLeft: true });
      Check("D4i 负向：没有提示内容的项 ⇒ 不显示（不回退成空框）", TipPlanFor(new ToolStripMenuItem("空的")) == null);
      Check("D4j 负控：判'有子菜单'不能用 `is ToolStripDropDownItem`（叶子项不得被判成有子菜单）",
        TipPlanFor(leaf) is { PreferLeft: false });

      // ————————————— D5 提示窗不夺激活（用户实报：右键后动一下鼠标菜单就消失）—————————————
      // 判据背景（2026-09-12 实测取证，同源 `decision-tray/RichTip.cs` 第 247~295 行）：自绘说明窗口一
      // `Show`，**线程激活**就丢了（`GetForegroundWindow` 毫发无损 ⇒ 丢的不是前台），正在显示的
      // `ContextMenuStrip` 随即收到 `WM_ACTIVATE(WA_INACTIVE)` ⇒ 按 `AppFocusChange` **自关**。
      // 真修 = `Show` 之后紧跟一句 `SetActiveWindow(菜单)`（decision-tray 实测：还 = 15/15 存活；不还 = 0/15 全灭）。
      // 本节的判据**不需真鼠标**：程序化 `菜单.Show()` + `DoEvents()`（A17b 已证明 DoEvents 足以派发
      // AppFocusChange 自关），再把"项上显示提示"按**生产路径**走一遍（`ShowMenuTip`）。
      sb.AppendLine();
      sb.AppendLine("=== D5 提示窗不夺激活：真菜单 + 项上提示 ⇒ 菜单必须存活（正控 N/N）+ 负控（不还激活 M/N）===");
      {
        const int N = 15;
        var probeTexts = new[] { "探针项①", "探针项②", "探针项③" };

        // 一轮 = 显示菜单 → **显式把菜单设为线程激活**（复刻真实右键的激活态）→ 在首项上显示提示
        //        → DoEvents → 判"菜单是否仍可见 / 提示是否真显示 / 菜单是否仍是线程激活" → 撤提示 → 关菜单。
        // 🔴 为什么必须显式 `SetActiveWindow(菜单)` 建立前置态：程序化 `菜单.Show()` **不会**把菜单设成
        //    线程激活窗口（第一版正因为缺这一步才假绿 —— 负控 14/15 存活）。真实右键时菜单**就是**
        //    激活窗口，所以"说明窗有没有把激活抢走"这个判据只有在激活态下才成立。
        // withOwner=true 走**生产路径**（owner=菜单 ⇒ Show 后 SetActiveWindow(菜单)）；
        // withOwner=false 是**负控变体**（owner=null ⇒ 不还激活），用来证明正控不是恒真。
        (int alive, int tipSeen, int actKept, int preOk, List<string> log) RunTipSurvival(bool withOwner) {
          int alive = 0, tipSeen = 0, actKept = 0, preOk = 0;
          var log = new List<string>();
          var owned = new List<ContextMenuStrip>();
          for (int k = 0; k < N; k++) {
            var iso = new ContextMenuStrip();
            foreach (var tx in probeTexts) iso.Items.Add(new ToolStripMenuItem(tx) { ToolTipText = "说明：" + tx });
            iso.ShowItemToolTips = false;
            owned.Add(iso);
            iso.Show(new Point(200, 160));
            Application.DoEvents();
            if (iso.IsHandleCreated) NativeMethods.SetActiveWindow(iso.Handle);   // ← 前置态：菜单=线程激活
            Application.DoEvents();
            bool menuUp0 = iso.IsHandleCreated && NativeMethods.IsWindowVisible(iso.Handle);
            bool preAct = iso.IsHandleCreated && NativeMethods.GetActiveWindow() == iso.Handle;
            var it0 = (ToolStripMenuItem)iso.Items[0];
            var anchor = iso.IsHandleCreated ? iso.RectangleToScreen(it0.Bounds) : Rectangle.Empty;
            ShowMenuTip("提示：不夺激活探针\n" + (withOwner ? "owner=菜单（生产路径）" : "owner=null（负控变体）"),
              anchor, false, withOwner ? iso : null);
            Application.DoEvents();
            bool menuUp1 = iso.IsHandleCreated && NativeMethods.IsWindowVisible(iso.Handle);
            bool tipUp = TipWindowVisible;
            bool actAfter = iso.IsHandleCreated && NativeMethods.GetActiveWindow() == iso.Handle;
            if (menuUp1) alive++;
            if (tipUp) tipSeen++;
            if (actAfter) actKept++;
            if (preAct) preOk++;
            log.Add("    #" + (k + 1).ToString("00") + " 前置(菜单=激活)=" + preAct + " 菜单显示=" + menuUp0
              + " · 提示后菜单存活=" + menuUp1 + " · 提示Visible=" + tipUp + " · 菜单仍是激活=" + actAfter);
            HideMenuTip();                                  // "离开项 / 关菜单"走的就是这一条
            Application.DoEvents();
            try { iso.Close(); } catch { }
            Application.DoEvents();
          }
          foreach (var m in owned) { try { m.Dispose(); } catch { } }
          Application.DoEvents();
          return (alive, tipSeen, actKept, preOk, log);
        }

        int showBefore = TipWinShowCount, hideBefore = TipWinHideCount;
        var pos = RunTipSurvival(true);
        var neg = RunTipSurvival(false);
        sb.AppendLine("  正控（还激活，生产路径 owner=菜单）：");
        foreach (var l in pos.log) sb.AppendLine(l);
        sb.AppendLine("  负控（不还激活，变体 owner=null）：");
        foreach (var l in neg.log) sb.AppendLine(l);
        sb.AppendLine("  [实测数字] 还激活：菜单 " + pos.alive + "/" + N + " 存活 · 激活保持 " + pos.actKept + "/" + N
          + "   ‖   不还激活：菜单 " + neg.alive + "/" + N + " 存活 · 激活保持 " + neg.actKept + "/" + N);
        sb.AppendLine("  [前置态] 菜单=线程激活 建立成功 " + pos.preOk + "/" + N + "（正控）· " + neg.preOk + "/" + N + "（负控）");
        sb.AppendLine("  [口径] 硬判据 = **线程激活保持**（15/15 vs 0/15，照 decision-tray 的 0/15 复现）。");
        sb.AppendLine("         菜单被自关这个**症状**在程序化路径下不复现（无前台/无捕获 ⇒ 不派发 WM_ACTIVATE 到队列），");
        sb.AppendLine("         真鼠标结论由常驻托盘的真实右键会话给出（同族 decision-tray 也把该条放到真鼠标 --hover-probe 留证）。");
        sb.AppendLine("         故负控的菜单存活数只作对照 [INFO] 印发，**不**当断言 —— 用恒不红的数当判据就是假判据。");
        Check("D5a 前置态成立：程序化菜单 + SetActiveWindow ⇒ 菜单确实是线程激活（否则本条判据无从谈起）",
          pos.preOk == N && neg.preOk == N, "正控=" + pos.preOk + "/" + N + " 负控=" + neg.preOk + "/" + N);
        Check("D5b 正控：还激活 ⇒ 菜单 N/N 全存活（N=" + N + "）", pos.alive == N, "还=" + pos.alive + "/" + N);
        Check("D5c 正控：提示本身真的显示了（既不能把菜单弄死，也不能提示不显示）",
          pos.tipSeen == N, "提示显示=" + pos.tipSeen + "/" + N);
        Check("D5d 正控：还激活 ⇒ 菜单仍是线程激活 N/N（机制判据，直连缺陷成因）",
          pos.actKept == N, "激活保持=" + pos.actKept + "/" + N);
        Check("D5e 负控（真做）：不还激活 ⇒ 线程激活大面积丢失（照 0/15 复现 ⇒ 证明 D5d 不是恒真）",
          neg.actKept <= N / 3, "不还激活保持=" + neg.actKept + "/" + N);
        Check("D5f 对照差：还激活的激活保持数 > 不还激活（两个数字都真跑出来）",
          pos.actKept > neg.actKept, "还=" + pos.actKept + " 不还=" + neg.actKept);
        sb.AppendLine("[INFO] 负控菜单存活对照 = " + neg.alive + "/" + N + "（正控 " + pos.alive + "/" + N + "）——症状路需真鼠标，见上口径");
        int showDelta = TipWinShowCount - showBefore, hideDelta = TipWinHideCount - hideBefore;
        Check("D5g 配对：Show/Hide 计数成对（ΔShow==ΔHide==2N，不留桌面游离窗）",
          showDelta == hideDelta && showDelta == 2 * N,
          "ΔShow=" + showDelta + " ΔHide=" + hideDelta + " 期望=" + (2 * N));
        Check("D5h 配对：整节跑完提示窗已撤下（不是钉子）", !TipWindowReallyVisible, "ReallyVisible=" + TipWindowReallyVisible);
      }
    } catch (Exception ex) {
      fail++; sb.AppendLine("[FAIL] PROBE EX: " + ex.Message + "\r\n" + ex.StackTrace);
    }
    sb.AppendLine();
    sb.AppendLine("SUMMARY pass=" + pass + " fail=" + fail);
    sb.AppendLine(fail == 0 ? "RESULT PASS" : "RESULT FAIL");
    return sb.ToString();
  }
}
