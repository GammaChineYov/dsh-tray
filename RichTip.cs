using System.Drawing;
using System.Drawing.Drawing2D;

namespace QwenTray;

/// <summary>浅色配色（贴在通知区旁边，跟系统面板色调一致才不刺眼）。</summary>
static class Theme
{
    public static readonly Color TipBg = Color.FromArgb(0xFC, 0xFC, 0xFE);
    public static readonly Color TipBorder = Color.FromArgb(0xC9, 0xCE, 0xD8);
    public static readonly Color Text = Color.FromArgb(0x1F, 0x23, 0x28);
    public static readonly Color Dim = Color.FromArgb(0x6B, 0x72, 0x80);
    public static readonly Color Hair = Color.FromArgb(0xE4, 0xE7, 0xEC);
    public static readonly Color Accent = Color.FromArgb(0x1F, 0x6F, 0xEB);
}

sealed class TipLine
{
    /// <summary>左侧状态符号；非空时会画一个该色的实心圆点。</summary>
    public int? GlyphArgb;
    public string Text = "";
    public Color Color = Theme.Text;
    public bool Bold;
    public bool Separator;
    public int Indent;

    public static TipLine Sep() => new() { Separator = true };
    public static TipLine Head(string t) => new() { Text = t, Bold = true, Color = Theme.Text };
    public static TipLine Dim2(string t) => new() { Text = t, Color = Theme.Dim, Indent = 6 };
    public static TipLine Item(int argb, string t) => new() { GlyphArgb = argb, Text = t };
}

/// <summary>
/// 把一段**纯文本**提示折成富提示的行 —— 菜单项的 <c>ToolTipText</c> 本来是多行文本，
/// 现在改由**我们自己**的提示窗绘制（平台自带那套的位置不可控 ⇒ 会压光标 ⇒ 自振荡，见 TipPlace），
/// 所以需要一个"同一份文本、换个渲染器"的入口。首行加粗（那行是"这条是什么"），其余原样分列。
/// <c>ToolTipText</c> 因此从"渲染源"降级为"数据源"（自检与自绘都读它，别删）。
/// </summary>
static class TipLines
{
    public static List<TipLine> FromText(string? text)
    {
        var lines = new List<TipLine>();
        bool first = true;
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var t = raw.TrimEnd();
            if (t.Length == 0) continue;
            lines.Add(first ? TipLine.Head(t) : new TipLine { Text = t });
            first = false;
        }
        return lines;
    }
}

/// <summary>
/// 自绘富 tooltip。
/// 🔴 为什么悬停提示必须**换载体**（纪律唯一源 = skill `tray-menu-keepopen` 铁律 8）：
/// 平台自带的 item tooltip（<c>ToolStripItem.ToolTipText</c> + <c>ShowItemToolTips=true</c>）位置在
/// ToolStrip 控件内部算，我们改不了；它只保证"不出屏"，**不保证"不压光标"** ⇒ 压住光标 / 被悬停那一行
/// 就会让底下元素丢 hover ⇒ 提示自己收起 ⇒ hover 恢复 ⇒ 再弹 = **闪（自振荡）**。
/// 所以平台渲染一律关掉（<c>ShowItemToolTips=false</c>），位置由 <see cref="TipPlace"/> 自己算。
/// </summary>
sealed class RichTip : Form
{
    const int Pad = 10;
    const int Gap = 4;
    const int GlyphW = 17;
    const int MinW = 240;
    const int MaxW = 560;

    readonly List<TipLine> _lines = new();
    readonly Font _f = Mk(9f, FontStyle.Regular);
    readonly Font _fb = Mk(9f, FontStyle.Bold);

    static Font Mk(float size, FontStyle style)
    {
        try { return new Font("Microsoft YaHei UI", size, style); }
        catch { try { return new Font(FontFamily.GenericSansSerif, size, style); } catch { return SystemFonts.DefaultFont; } }
    }

    /// <summary>Show 时**不抢焦点** —— 否则会把用户正开着的右键菜单关掉。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW（不出现在 Alt+Tab）
            // 🔑 WS_EX_TRANSPARENT：对鼠标**全透明**（命中测试直接穿透）。
            //    这是"提示窗绝不自振荡"的**机械保证** —— 位置算法（TipPlace）负责"别压光标"，
            //    万一还是压住了（屏幕太小 / 锚点占满），只要它吃不到鼠标消息，
            //    底下的菜单项就不会丢 hover ⇒ 不会触发"收起→hover 恢复→再弹"的振荡回路。
            //    ⚠️ 别为了"让提示能点/能选中文字"把它去掉 —— 那会把自振荡放回来。
            cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT
            return cp;
        }
    }

    public RichTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.TipBg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    const int WM_MOUSEACTIVATE = 0x0021;
    const int MA_NOACTIVATE = 3;

    protected override void WndProc(ref Message m)
    {
        // 双保险：即使窗口被点到也不要激活
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    public void SetLines(IEnumerable<TipLine> lines)
    {
        _lines.Clear();
        _lines.AddRange(lines);
        ReMeasure();
        Invalidate();
    }

    void ReMeasure()
    {
        int w = MinW;
        foreach (var l in _lines)
        {
            if (l.Separator) continue;
            var f = l.Bold ? _fb : _f;
            int tw = TextRenderer.MeasureText(l.Text, f,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            w = Math.Max(w, Pad * 2 + l.Indent + (l.GlyphArgb.HasValue ? GlyphW : 0) + tw + 12);
        }

        int h = Pad * 2;
        foreach (var l in _lines) h += l.Separator ? 9 : RowH;
        var sz = new Size(Math.Min(w, MaxW), h);
        if (ClientSize != sz) ClientSize = sz;
    }

    int RowH => Math.Max(19, _f.Height + Gap);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.TipBg);
        using (var pen = new Pen(Theme.TipBorder)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        int y = Pad;
        foreach (var l in _lines)
        {
            if (l.Separator)
            {
                using var p = new Pen(Theme.Hair);
                g.DrawLine(p, Pad, y + 4, Width - Pad, y + 4);
                y += 9;
                continue;
            }

            int h = RowH;
            int x = Pad + l.Indent;
            if (l.GlyphArgb is { } argb)
            {
                using var gb = new SolidBrush(Color.FromArgb(argb));
                g.FillEllipse(gb, x + 3, y + (h - 11) / 2f, 11f, 11f);
                x += GlyphW;
            }
            var f = l.Bold ? _fb : _f;
            var rect = new Rectangle(x, y, Math.Max(10, Width - Pad - x), h);
            TextRenderer.DrawText(g, l.Text, f, rect, l.Color,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            y += h;
        }
    }

    /// <summary>真实显示的累计次数（只在**真的**由不可见变可见时 +1 ⇒ 与 <see cref="HideCount"/> 成对）。</summary>
    public int ShowCount { get; private set; }
    /// <summary>真实收起的累计次数（只在**真的**由可见变不可见时 +1 ⇒ 已收起再收起不重复计数）。</summary>
    public int HideCount { get; private set; }

    /// <summary>
    /// 贴在光标旁显示（**通知区图标那条路**：只知道光标、不知道菜单，**没有 owner**）。
    /// <para>⚠️ 这条路的 owner 恒为 null ⇒ 按"不还激活"处理；它仍**绝不抢焦点** ——
    /// <see cref="ShowWithoutActivation"/>=true + ExStyle 里的 WS_EX_NOACTIVATE 是底座，
    /// 少了它们，提示窗会直接把用户正开着的别的程序顶掉。</para>
    /// 位置一律走 <see cref="TipPlace"/>：**绝不压光标** —— 压住就会自振荡（见 TipPlace 注释）。
    /// </summary>
    public void ShowAt(Point cursor) => ShowNear(TipPlace.CursorGuardRect(cursor), preferLeft: false, owner: null);

    /// <summary>
    /// 贴着某个**锚点矩形**显示（菜单项那条路：锚点是那一行的屏幕矩形）。
    /// </summary>
    /// <param name="anchor">被悬停目标的屏幕矩形。</param>
    /// <param name="preferLeft">有子菜单的项传 true（子菜单从右侧展开，提示让到左侧）。</param>
    /// <param name="owner">
    /// 🔴 菜单项说明必须传它的**菜单**（<c>ToolStripDropDown</c> 实现 <see cref="IWin32Window"/>，正好可当 owner）——
    /// 这个句柄就是 <c>Show</c> 之后要把**线程激活**还回去的对象，缺了它菜单会被自己判成失焦而当场关闭。
    /// <para>为什么是硬要求（2026-09-12 实测取证，同源 `decision-tray/RichTip.cs`）：说明窗口一 <c>Show</c>，
    /// **线程激活窗口**就没了（<c>GetForegroundWindow</c> 毫发无损 —— 丢的是线程激活，不是前台），
    /// 正在显示的 <c>ContextMenuStrip</c> 随即收到 <c>WM_ACTIVATE(WA_INACTIVE)</c> ⇒ 按 <c>AppFocusChange</c>
    /// **自关**。用户实感就是「右键后动一下鼠标菜单就消失」。</para>
    /// <para>🧪 已被实测否掉的三条错解：① 加 <c>WS_EX_NOACTIVATE</c> / <c>SW_SHOWNOACTIVATE</c> —— 实测窗口
    /// ExStyle 里 NOACTIVATE 本来就是 1，照样丢激活；② 传 owner 只做 <c>Show(owner)</c> —— 窗口仍是新顶层
    /// 窗口，激活照样丢；③ **把 owner 交给 <c>Show(owner)</c>** —— 还有第二个坑：<c>Form</c> 会把非 Form 的
    /// owner 存进内部字段，菜单重建/Dispose 之后再 <c>SetLines</c>（→ CreateParams）会去访问**已 Dispose 的
    /// 菜单句柄** ⇒ 抛 <c>ObjectDisposedException</c>（被 catch 吞掉 = 提示**静默不显示**；本仓自检 D5 实测撞上）。
    /// 所以这里**裸 <c>Show()</c>**，只把 owner 当"把激活还给谁"的句柄用。**真正起作用的是 Show 之后
    /// 把线程激活显式还回去**（见下）。</para>
    /// <para>⚠️ 绝不用 <c>AutoClose = false</c> 来"修"这个现象 —— 那会让菜单切到别的窗口还赖着不走（钉子），
    /// 比原缺陷更糟。见 `MenuKeepOpen.cs` 的否决记录。</para>
    /// </param>
    public void ShowNear(Rectangle anchor, bool preferLeft, IWin32Window? owner = null)
    {
        try
        {
            var cursor = Cursor.Position;
            var wa = Screen.FromPoint(cursor).WorkingArea;
            var pt = TipPlace.Place(cursor, new Size(Width, Height), wa, anchor, preferLeft);

            if (Location != pt) Location = pt;
            if (!Visible)
            {
                var ownerHwnd = owner != null ? owner.Handle : IntPtr.Zero;
                Show();                     // 裸 Show：刻意**不**把 owner 交给 Form（见上方错解 ③）
                ShowCount++;
                // 🔑 这一句是本次缺陷的**真修**：把线程激活还给菜单。缺了它 = 菜单被自己判成失焦
                //    而当场关闭（decision-tray 实测：还 = 菜单 15/15 存活；不还 = 0/15 全灭）。必须紧跟
                //    Show、在消息泵再跑之前调用 —— 晚了菜单已经收过 WA_INACTIVE 并关掉了。
                if (ownerHwnd != IntPtr.Zero) NativeMethods.SetActiveWindow(ownerHwnd);
            }
        }
        catch { }
    }

    public void HideTip()
    {
        // 只在真的由可见变不可见时计数 ⇒ Show/Hide 计数成对；已收起再收起不会重复计数。
        try { if (Visible) { Hide(); HideCount++; } } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _f.Dispose(); _fb.Dispose(); }
        base.Dispose(disposing);
    }
}
