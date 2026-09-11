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
// 监听 explorer 重启（TaskbarCreated 广播）→ TrayApp 延迟重注册图标重建右键路由
internal sealed class TaskbarWatcher : NativeWindow {
  static readonly int _msg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
  public event Action? ExplorerRestarted;
  public TaskbarWatcher(){ CreateHandle(new CreateParams()); }
  protected override void WndProc(ref Message m){ if(_msg!=0 && m.Msg==_msg) ExplorerRestarted?.Invoke(); base.WndProc(ref m); }
}
