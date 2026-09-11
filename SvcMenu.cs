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
  // —— 每模型二级菜单（2026-09-11）：一级项 = <状态圆点><名称> (端口)；二级 = 会话入口 + 模型启停 + 运行状态 + 运行时配置 ——
  // 元素全部固定（只改 Text/Image/Visible，绝不结构重建）→ 菜单显示期间更新也安全，不会触发历史那个「幽灵菜单/布局错乱」。
internal class SvcMenu {
    public Service svc=null!;
    public ToolStripMenuItem root=null!;                 // 一级项（带状态圆点 Image）
    public ToolStripMenuItem status=null!;               // 运行状态行（禁用项，ToolTip 给全量信息）
    public ToolStripMenuItem openDsh=null!,openBuiltin=null!;
    public ToolStripMenuItem start=null!,restart=null!,stop=null!;
    public ToolStripMenuItem cfgHeader=null!,envHeader=null!,cppHeader=null!;
    public ToolStripMenuItem envCuda=null!,envAllreduce=null!;
    public List<ToolStripMenuItem> cfgLines=new();       // 运行时配置行池（按行数显示/隐藏）
    public string sig="";                                // 变更签名：未变化则跳过重绘
  }
