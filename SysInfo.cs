using System;
using System.Management;
using System.Runtime.InteropServices;

namespace QwenTray;

public static class SysInfo {
  // ---- CPU 使用率（GetSystemTimes 两次采样差值）----
  [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }
  [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);
  static long _prevIdle, _prevKernel, _prevUser; static bool _first = true; static double _cpu = -1;
  static double ToMs(FILETIME f) { return ((ulong)f.dwHighDateTime << 32 | f.dwLowDateTime) / 10000.0; }
  public static double CpuPercent() {
    FILETIME i, k, u;
    if (!GetSystemTimes(out i, out k, out u)) return _cpu < 0 ? 0 : _cpu;
    long idle = (long)ToMs(i), kernel = (long)ToMs(k), user = (long)ToMs(u);
    if (_first) { _prevIdle = idle; _prevKernel = kernel; _prevUser = user; _first = false; return 0; }
    long dIdle = idle - _prevIdle, dKernel = kernel - _prevKernel, dUser = user - _prevUser;
    _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
    double total = dKernel + dUser; // kernel 含 idle
    if (total <= 0) return _cpu < 0 ? 0 : _cpu;
    _cpu = 100.0 * (1.0 - dIdle / total);
    return _cpu;
  }

  // ---- 内存占用（GlobalMemoryStatusEx）----
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
  private struct MEMORYSTATUSEX {
    public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys;
    public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
  }
  [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
  public static (double usedPct, double totalGB) Mem() {
    var ms = new MEMORYSTATUSEX(); ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
    if (GlobalMemoryStatusEx(ref ms) && ms.ullTotalPhys > 0) {
      return (100.0 * (1.0 - (double)ms.ullAvailPhys / ms.ullTotalPhys), ms.ullTotalPhys / 1073741824.0);
    }
    return (0, 0);
  }

  // ---- CPU 温度（WMI ThermalZoneInformation，返回值=摄氏度*10；无则 NaN）----
  public static double CpuTemp() {
    try {
      using (var s = new ManagementObjectSearcher("SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation")) {
        foreach (var o in s.Get()) {
          var t = o["Temperature"];
          if (t != null) { double v; if (double.TryParse(t.ToString(), out v)) return v / 10.0; }
        }
      }
    } catch { }
    return double.NaN;
  }
}
