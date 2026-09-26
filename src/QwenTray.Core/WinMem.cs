using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace QwenTray;

// ============================================================================
// 2026-09-13：工作集主动回收的 Win32 落地（判定在 MemTrim.cs，这里只管"动手"）。
//
// 为什么不用 Process.Handle：
//   托盘的 Service.proc 有两种来源 —— 自己 Process.Start 起的（句柄全权限），
//   以及 **Adopt() 按端口 netstat 接管的外部实例**（Process.GetProcessById 拿到的句柄权限不全）。
//   直接拿它喂 EmptyWorkingSet 在接管路径上会静默失败（返回 false，GetLastError=5）。
//   所以这里一律走 OpenProcess 申请**刚好够用**的两个权限。
//
// EmptyWorkingSet 需要 PROCESS_QUERY_INFORMATION|PROCESS_SET_QUOTA（K32EmptyWorkingSet 内部走
// NtSetInformationProcess(ProcessWorkingSetInformation)）—— 申请 PROCESS_ALL_ACCESS 反而更易被拒。
// ============================================================================

public static class WinMem {
  const uint PROCESS_QUERY_INFORMATION = 0x0400;
  const uint PROCESS_SET_QUOTA         = 0x0100;

  [DllImport("kernel32.dll", SetLastError = true)]
  static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool CloseHandle(IntPtr hObject);

  // psapi.dll（Win7+ 也导出在 kernel32 的 K32EmptyWorkingSet；psapi 路径在本机两个 build 上都实测可用）
  [DllImport("psapi.dll", SetLastError = true)]
  static extern bool EmptyWorkingSet(IntPtr hProcess);

  /// <summary>读进程当前工作集（字节）。取不到返回 0（不抛）。</summary>
  public static long WorkingSetBytes(int pid) {
    if (pid <= 0) return 0;
    try {
      using var p = Process.GetProcessById(pid);
      p.Refresh();
      return p.WorkingSet64;
    } catch { return 0; }
  }

  /// <summary>
  /// 把 pid 的工作集页推回 standby/pagefile（EmptyWorkingSet）。
  /// <para>语义：这不是"释放内存"，是把**可再取的页**（权重 mmap、文件缓存）从工作集移出；
  /// 物理页仍在 standby 列表里，下次访问按需重新缺页 —— 实测速度无损（见 MemTrim.cs 头部）。</para>
  /// <para>⚠️ 对正在推理的进程调用会造成缺页抖动 ⇒ 调用方必须先过 <see cref="MemTrim.AllowIdle"/>。</para>
  /// </summary>
  /// <returns>true = 调用成功（工作集读数见 out 参数）；false = 拿不到进程句柄或调用失败。</returns>
  public static bool TrimWorkingSet(int pid, out long beforeBytes, out long afterBytes) {
    beforeBytes = 0; afterBytes = 0;
    if (pid <= 0) return false;
    beforeBytes = WorkingSetBytes(pid);
    IntPtr h = IntPtr.Zero;
    try {
      h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pid);
      if (h == IntPtr.Zero) return false;
      if (!EmptyWorkingSet(h)) return false;
    } catch {
      return false;
    } finally {
      if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } }
    }
    // 工作集是异步收缩的：立刻读往往还是旧值（实测差 1 帧），给内核 ~120ms 把页摘干净再读。
    Thread.Sleep(120);
    afterBytes = WorkingSetBytes(pid);
    return true;
  }

  /// <summary>按 TCP 监听端口找 llama-server 的 pid（接管路径与 CLI --trim 用）。找不到返回 0。</summary>
  public static int PidByPort(int port) {
    try {
      var psi = new ProcessStartInfo("netstat", "-ano") {
        UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
      };
      using var p = Process.Start(psi);
      if (p == null) return 0;
      string o = p.StandardOutput.ReadToEnd();
      p.WaitForExit(3000);
      string needle = ":" + port;
      foreach (var raw in o.Split('\n')) {
        var line = raw.TrimEnd('\r');
        if (line.Length == 0) continue;
        if (line.IndexOf(needle, StringComparison.Ordinal) < 0) continue;
        if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) continue;
        if (int.TryParse(parts[parts.Length - 1], out int pid) && pid > 0) return pid;
      }
    } catch { }
    return 0;
  }
}
