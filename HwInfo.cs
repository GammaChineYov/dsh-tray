using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace QwenTray;

// HWiNFO64 共享内存读取（需 HWiNFO64 运行中且开启 Shared Memory Support，Sensors 窗口打开）
// 布局：HWiNFO v8 Global\HWiNFO_SENS_SM2，参考 namazso gist 0c37be5a53863954c8c8279f66cfb1cc
public static class HwInfo {
  const string ShmName = @"Global\HWiNFO_SENS_SM2";
  const uint Magic = 0x53695748; // 'HiWS'

  public static bool Available() { try { using (var m = MemoryMappedFile.OpenExisting(ShmName, MemoryMappedFileRights.Read)) return true; } catch { return false; } }

  // CPU 温度（℃）。优先级：Package / Die(Tctl) / Core Average / Core Max / 其它 CPU 读数；HWiNFO 未运行或未开共享内存返回 NaN
  public static double CpuTemp() {
    try {
      using (var mmf = MemoryMappedFile.OpenExisting(ShmName, MemoryMappedFileRights.Read))
      using (var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read)) {
        long cap = acc.Capacity;
        if (cap < 48) return double.NaN;
        if (acc.ReadUInt32(0) != Magic) return double.NaN;
        long lastUpdate = acc.ReadInt64(12);
        uint sensorOff = acc.ReadUInt32(20), sensorSize = acc.ReadUInt32(24), sensorCount = acc.ReadUInt32(28);
        uint entryOff  = acc.ReadUInt32(32), entrySize  = acc.ReadUInt32(36), entryCount  = acc.ReadUInt32(40);
        if (sensorCount == 0 || sensorCount > 4096 || entryCount == 0 || entryCount > 65536) return double.NaN;
        if (sensorSize < 264 || entrySize < 316) return double.NaN;
        if ((long)entryOff + (long)entrySize * entryCount > cap) return double.NaN;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - lastUpdate) > 15) return double.NaN; // 数据停止刷新（HWiNFO 传感器窗口关闭等）→ 不显示陈旧值
        var sensorNames = new List<string>();
        for (int i = 0; i < (int)sensorCount; i++) sensorNames.Add(ReadStr(acc, sensorOff + (long)i * sensorSize + 8, 128));
        double best = double.NaN; int bestScore = -1;
        for (int i = 0; i < (int)entryCount; i++) {
          long p = entryOff + (long)i * entrySize;
          if (acc.ReadUInt32(p) != 1) continue; // SensorType.Temperature
          uint sIdx = acc.ReadUInt32(p + 4);
          if (sIdx >= sensorCount) continue;
          string sensor = sensorNames[(int)sIdx];
          if (sensor.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) < 0 && sensor.IndexOf("Processor", StringComparison.OrdinalIgnoreCase) < 0) continue;
          double v = acc.ReadDouble(p + 0x11C);
          if (double.IsNaN(v) || v < 0 || v > 120) continue;
          int score = Score(ReadStr(acc, p + 12, 128));
          if (score > bestScore) { bestScore = score; best = v; }
        }
        return best;
      }
    } catch { return double.NaN; }
  }

  static int Score(string name) {
    string n = (name ?? "").ToLowerInvariant();
    if (n.Contains("package")) return 100;
    if (n.Contains("die") || n.Contains("tctl") || n.Contains("t-die")) return 90;
    if (n.Contains("average")) return 60;
    if (n.Contains("max")) return 50;
    if (n.Contains("cpu")) return 30;
    if (n.Contains("core")) return 20;
    return 0;
  }

  static string ReadStr(UnmanagedMemoryAccessor acc, long off, int maxLen) {
    try {
      var buf = new byte[maxLen];
      int n = acc.ReadArray(off, buf, 0, maxLen);
      int len = 0; while (len < n && buf[len] != 0) len++;
      return Encoding.UTF8.GetString(buf, 0, len);
    } catch { return ""; }
  }
}
