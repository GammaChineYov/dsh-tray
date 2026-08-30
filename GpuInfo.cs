using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace QwenTray {
public class Gpu {
  public int Index;
  public string PciBus = "";   // e.g. "01:00.0"
  public string Name = "";
  public double TotalGiB;
  public double UsedGiB;
  public int UtilPct;
  public int TempC;
  public bool Ok;              // 发现成功且有显存
  public string Line { get { return string.Format("GPU{0}: {1:0.0}/{2:0.0}G {3}% {4}\u00B0C", Index, UsedGiB, TotalGiB, UtilPct, TempC); } }
}

public static class GpuInfo {
  // nvidia-smi 查询全部 GPU：index,pci.bus_id,name,memory.total,memory.used,utilization.gpu,temperature.gpu
  public static string QueryCsv() {
    var psi = new ProcessStartInfo("nvidia-smi",
      "--query-gpu=index,pci.bus_id,name,memory.total,memory.used,utilization.gpu,temperature.gpu --format=csv,noheader,nounits") {
      UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
    };
    var p = Process.Start(psi); string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(3000);
    return o;
  }

  public static List<Gpu> Discover() {
    var list = new List<Gpu>();
    try {
      string o = QueryCsv();
      foreach (var ln in o.Split('\n')) {
        var s = ln.Trim(); if (s.Length == 0) continue;
        var parts = s.Split(',');
        if (parts.Length < 7) continue;
        int idx; if (!int.TryParse(parts[0].Trim(), out idx)) continue;
        var g = new Gpu { Index = idx, PciBus = ShortPci(parts[1].Trim()), Name = parts[2].Trim() };
        double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out g.TotalGiB);
        double.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out g.UsedGiB);
        int.TryParse(parts[5].Trim(), out g.UtilPct);
        int.TryParse(parts[6].Trim(), out g.TempC);
        g.TotalGiB /= 1024.0; g.UsedGiB /= 1024.0;
        g.Ok = g.TotalGiB > 0;
        list.Add(g);
      }
    } catch { }
    return list;
  }

  // "00000000:01:00.0" -> "01:00.0"
  public static string ShortPci(string bus) {
    if (!string.IsNullOrEmpty(bus) && bus.StartsWith("00000000:")) return bus.Substring(9);
    return bus;
  }
}
}
