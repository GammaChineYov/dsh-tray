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

// S1（2026-09-11）拆自 Program.cs：零逻辑改动，仅位移。
internal static class NativeMethods {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int RegisterWindowMessage(string lpString);

  /// <summary>GWL_EXSTYLE：读扩展样式。自检（--selftest-tips 的 D3）用它证"提示窗**真的**带 WS_EX_TRANSPARENT"。</summary>
  public const int GWL_EXSTYLE = -20;

  /// <summary>读窗口扩展样式。⚠️ 64 位下扩展样式仍是 32 位值，用 GetWindowLongW 读是安全的
  /// （不要为它去套 GetWindowLongPtr：那会把 32/64 位两套签名搅在一起）。</summary>
  [DllImport("user32.dll", EntryPoint="GetWindowLongW")]
  public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

  /// <summary>读**本线程**当前的激活窗口（与系统前台无关）。判"说明窗有没有把菜单的线程激活抢走"用它。</summary>
  [DllImport("user32.dll")]
  public static extern IntPtr GetActiveWindow();

  /// <summary>把**线程激活窗口**设为 hWnd（只影响本线程，**不动系统前台** —— 与 SetForegroundWindow 无关）。
  /// <para>🔴 用途（2026-09-12「右键后动一下鼠标菜单就消失」的真修，签名照搬 decision-tray/Native.cs）：
  /// 自绘说明窗口一 <c>Show</c>，**线程激活**就丢了（<c>GetForegroundWindow</c> 毫发无损 ⇒ 丢的不是前台），
  /// 正在显示的 <c>ContextMenuStrip</c> 随即收到 <c>WM_ACTIVATE(WA_INACTIVE)</c> ⇒ 按 <c>AppFocusChange</c>
  /// **自关**。所以说明窗 <c>Show</c> 之后必须紧跟一句 <c>SetActiveWindow(菜单句柄)</c> 把激活还回去。</para></summary>
  [DllImport("user32.dll")]
  public static extern IntPtr SetActiveWindow(IntPtr hWnd);

  /// <summary>窗口**真的**可见（含祖先）。判"菜单还活不活"用它 —— 托管侧 <c>ToolStripDropDown.Visible</c>
  /// 只是状态标记，实测在 Show 之后仍可能读到 false ⇒ 拿它当判据会造**假红**（decision-tray 的实测结论）。</summary>
  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool IsWindowVisible(IntPtr hWnd);

  // ── 菜单落点护栏用（2026-09-12；见 MenuPlace.cs）──
  // 通知区图标长在任务栏里（任务栏在**工作区之外**），而 `NotifyIcon` 那条路把菜单**右下角直接贴在光标**上、
  // 不做工作区钳位 ⇒ 菜单最后一行被任务栏吃掉。`GetWindowRect` 取真矩形，`SetWindowPos` 挪回来。
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left, Top, Right, Bottom; }

  /// <summary>取窗口**真实**矩形。⚠️ 别用 `ToolStripDropDown.Bounds`（给的是未调整前的位置，偏差 ~95px）。</summary>
  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

  /// <summary>移动窗口（不改尺寸 / 不改 Z 序 / 不夺激活）。</summary>
  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

  public const uint SWP_NOSIZE = 0x0001;
  public const uint SWP_NOZORDER = 0x0004;
  public const uint SWP_NOACTIVATE = 0x0010;
}

