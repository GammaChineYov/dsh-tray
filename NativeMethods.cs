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
}
