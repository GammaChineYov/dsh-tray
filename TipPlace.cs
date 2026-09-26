using System.Drawing;

namespace QwenTray;

/// <summary>
/// 悬停提示窗的**摆位算法**（纯函数 ⇒ 可被自检扫断言）。
///
/// 🔴 为什么位置必须自己算（纪律唯一源 = skill `tray-menu-keepopen` 铁律 8「悬停提示窗不得压光标」）：
/// 提示窗压住**光标**或**被悬停的那一项**的那一瞬间，底下的元素就"丢了 hover"⇒ 提示自己收起
/// ⇒ hover 恢复 ⇒ 提示再弹 —— 这是个**自振荡**回路。用户看到的就是"tooltip 在闪/抖"。
/// 平台自带的自动翻边只保证"不出屏"，**不保证"不压光标"**；实测同一张菜单里
/// 有的格位避让、有的不避让（家族第一起事故 = 本托盘的参数面板 GPU 勾选项："GPU0 避让、GPU1 不避让"，
/// 当时用"去掉 ToolTipText"绕开 —— **绕开 ≠ 修**，行为依赖格位、靠运气），
/// 所以必须把位置提升为**硬判据**：
/// <list type="number">
/// <item>算出来的矩形不得与<b>光标安全区</b>相交（安全区 ≈ 一个托盘图标，取 24px）</item>
/// <item>算出来的矩形不得与<b>锚点</b>（被悬停的菜单项）相交</item>
/// </list>
///
/// ⚠️ 判据 ① 是"不许压光标"，不是"不许压那个点" —— 只判一个点会漏掉"提示的边缘切到光标"这种
/// 半覆盖情形（半覆盖同样会让底下元素丢 hover）。
/// </summary>
static class TipPlace
{
    /// <summary>与锚点 / 光标安全区的间距。</summary>
    public const int Gap = 8;

    /// <summary>光标安全区边长（≈ 一个托盘图标的可点范围）。</summary>
    public const int CursorGuard = 24;

    /// <summary>把光标换成"最小可点目标"的矩形 —— 判据①比的就是它。</summary>
    public static Rectangle CursorGuardRect(Point cursor) =>
        new(cursor.X - CursorGuard / 2, cursor.Y - CursorGuard / 2, CursorGuard, CursorGuard);

    /// <summary>硬判据本身，抽出来是为了让自检能对它做**负控**（证明它会判违规，而不是恒真/恒假）。</summary>
    public static bool Violates(Rectangle r, Rectangle cursorGuard, Rectangle anchor) =>
        r.IntersectsWith(cursorGuard) || r.IntersectsWith(anchor);

    /// <summary>
    /// 给提示窗选一个位置。<paramref name="preferLeft"/> = 优先放在锚点左侧
    /// （有子菜单的项要用它：子菜单从右侧展开，提示得让开）。
    /// </summary>
    /// <param name="cursor">当前光标（判据①的基准）。</param>
    /// <param name="size">提示窗尺寸（**测量之后**的）。</param>
    /// <param name="wa">允许摆放的工作区（一般取光标所在屏的 WorkingArea）。</param>
    /// <param name="anchor">被悬停的目标矩形；只知光标时传 <see cref="CursorGuardRect"/>。</param>
    public static Point Place(Point cursor, Size size, Rectangle wa, Rectangle anchor, bool preferLeft = false)
    {
        var guard = CursorGuardRect(cursor);

        // ① 先按偏好顺序试遍候选位：合格就返回（合格 = 不压光标、不压锚点）
        foreach (var cand in Candidates(cursor, size, anchor, preferLeft))
        {
            var p = Clamp(cand, size, wa);
            if (!Violates(new Rectangle(p, size), guard, anchor)) return p;
        }

        // ② 全部候选都违规 ⇒ 只可能是"屏幕装不下这个提示"或"锚点把可用区占满了"。
        //    此时退回"与光标/锚点相交面积最小"的那个（确定性：并列取先到的）。
        //    ⚠️ 兜底之所以还能接受：窗口本身是 WS_EX_TRANSPARENT（见 RichTip.CreateParams）——
        //       即使几何上压住了光标，它也**吃不到鼠标消息** ⇒ 底下元素不会丢 hover ⇒ 不会自振荡。
        Point best = default;
        long bestArea = -1;
        foreach (var cand in Candidates(cursor, size, anchor, preferLeft))
        {
            var p = Clamp(cand, size, wa);
            var r = new Rectangle(p, size);
            long a = Area(Rectangle.Intersect(r, guard)) + Area(Rectangle.Intersect(r, anchor));
            if (bestArea < 0 || a < bestArea) { bestArea = a; best = p; }
        }
        return best;
    }

    /// <summary>候选位：偏好侧 → 另一侧 → 锚点上方 → 锚点下方 → 光标侧（两个方向）。</summary>
    static IEnumerable<Point> Candidates(Point cursor, Size size, Rectangle anchor, bool preferLeft)
    {
        int near = preferLeft ? anchor.Left - size.Width - Gap : anchor.Right + Gap;
        int far = preferLeft ? anchor.Right + Gap : anchor.Left - size.Width - Gap;

        yield return new Point(near, anchor.Top);
        yield return new Point(far, anchor.Top);
        yield return new Point(anchor.Left, anchor.Top - size.Height - Gap);
        yield return new Point(anchor.Left, anchor.Bottom + Gap);
        yield return new Point(preferLeft ? cursor.X - size.Width - 14 : cursor.X + 14, cursor.Y - size.Height / 2);
        yield return new Point(preferLeft ? cursor.X + 14 : cursor.X - size.Width - 14, cursor.Y - size.Height / 2);
    }

    /// <summary>钳进工作区（只负责"不出屏"）。提示比工作区还大时退到左上角，别再往负方向跑。</summary>
    static Point Clamp(Point p, Size size, Rectangle wa) => new(
        Math.Max(wa.Left, Math.Min(p.X, Math.Max(wa.Left, wa.Right - size.Width))),
        Math.Max(wa.Top, Math.Min(p.Y, Math.Max(wa.Top, wa.Bottom - size.Height))));

    static long Area(Rectangle r) => r.Width <= 0 || r.Height <= 0 ? 0 : (long)r.Width * r.Height;
}
