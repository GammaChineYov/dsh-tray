using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace QwenTray;

// S1（2026-09-11）拆自 Program.cs（原 TrayApp 私有嵌套类 → 顶层 internal）：零逻辑改动，仅位移。
  // —— 每模型「一级项 + 承载它的那组菜单元素」（2026-09-11 的二级菜单形制）——
  // 元素全部固定（只改 Text/Image/Visible，绝不结构重建）→ 菜单显示期间更新也安全，不会触发历史那个「幽灵菜单/布局错乱」。
  //
  // ⚠️ 2026-09-26：**每模型的二级菜单已整体取消**（用户指令：功能上移到一级菜单的「当前模型」操作组）。
  //    下面除 svc / root / sig 之外的字段就此**成为遗留字段**（结构还在、永不被赋值、无读取点）——
  //    保留而不是删，是因为"每模型二级菜单"这个形态未来若复活（例如按需展开）就能直接接回去；
  //    留一句说明比留十个 null! 更不容易被误接。要用的话请先在 TrayApp.Services.cs 里重新装配。
  internal class SvcMenu {
    public Service svc=null!;                            // 它服务的那个模型（唯一真源）
    public ToolStripMenuItem root=null!;                 // 一级项（带状态圆点 Image；点击=选中本模型）
    public string sig="";                                // 变更签名：未变化则跳过重绘
    [Obsolete("遗留字段：每模型二级菜单已于 2026-09-26 取消，本字段不再被装配/读取")] public ToolStripMenuItem status=null!;
    [Obsolete("遗留字段：同上")] public ToolStripMenuItem openDsh=null!,openBuiltin=null!;
    [Obsolete("遗留字段：同上")] public ToolStripMenuItem start=null!,restart=null!,stop=null!;
    [Obsolete("遗留字段：同上")] public ToolStripMenuItem cfgHeader=null!,envHeader=null!,cppHeader=null!;
    [Obsolete("遗留字段：同上")] public ToolStripMenuItem envCuda=null!,envAllreduce=null!;
    [Obsolete("遗留字段：同上")] public List<ToolStripMenuItem> cfgLines=new();
  }
