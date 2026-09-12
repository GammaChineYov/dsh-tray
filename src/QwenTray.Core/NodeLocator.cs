using System;
using System.IO;

namespace QwenTray;

// S5（2026-09-12）：node.exe 位置解析 —— 修「托盘启动 DSH 失败」的**根因**。
//
// 现象：托盘菜单「启动 DSH」弹「启动 DSH 失败: 系统找不到指定的文件」。
// 根因：配置里的 DshNodeExe 是**绝对路径**，而 WorkBuddy 托管的 node 版本目录会随平台更新
//   **轮转**（versions\22.22.2-2 → versions\22.22.2-3，旧目录被直接回收）。配置一旦指向被回收
//   的目录，DshStart 的 `new ProcessStartInfo(cfg.DshNodeExe)` + UseShellExecute=false 在
//   Process.Start 时直接抛 Win32Exception —— 托盘无权也无法自愈。
//   同一天同一个根因还打断了全局 `dsh` 启动器（C:\Users\Landrom\bin\dsh.cmd 硬编码同款路径）。
//
// 策略：**配置优先，失效则沿它的父链自愈** ——
//   ① configured 存在 → 原样用（尊重用户手填的任何 node）；
//   ② 否则把 configured 当作 `<root>\versions\<ver>\node.exe`，去 `<root>\versions\` 下：
//      先读 WorkBuddy 自己维护的 `current` 指针，再退到「该目录下 mtime 最新且确实含 node.exe」的版本目录；
//   ③ 都找不到 → 原值返回（让上层照旧报错，错误信息不变）。
//
// 只读、**不写回配置**：自愈但绝不改用户文件（本项目的配置文件是手工维护、不进 git 的）。
public static class NodeLocator {

  public static string Resolve(string? configured) { return Resolve(configured, out _); }

  public static string Resolve(string? configured, out string note) {
    note = "";
    if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured!;

    // configured = <root>\versions\<ver>\node.exe
    //   dir          = <root>\versions\<ver>
    //   versionsRoot = <root>\versions
    string? dir = null;
    try { if (!string.IsNullOrEmpty(configured)) dir = Path.GetDirectoryName(configured!); } catch { }
    string? versionsRoot = null;
    try { if (!string.IsNullOrEmpty(dir)) versionsRoot = Path.GetDirectoryName(dir!); } catch { }

    if (!string.IsNullOrEmpty(versionsRoot) && Directory.Exists(versionsRoot!)) {
      // ① WorkBuddy 维护的 current 指针（版本轮转后它指向当前目录）
      try {
        string curFile = Path.Combine(versionsRoot!, "current");
        if (File.Exists(curFile)) {
          string ver = (File.ReadAllText(curFile) ?? "").Trim();
          if (ver.Length > 0) {
            string cand = Path.Combine(versionsRoot!, ver, "node.exe");
            if (File.Exists(cand)) {
              note = "配置路径已失效，改用 versions\\current = " + ver;
              return cand;
            }
          }
        }
      } catch { }

      // ② 没有可用 current：取 versions\ 下 mtime 最新、且确实含 node.exe 的目录
      try {
        string? best = null; DateTime bestT = DateTime.MinValue;
        foreach (string d in Directory.GetDirectories(versionsRoot!)) {
          if (!File.Exists(Path.Combine(d, "node.exe"))) continue;
          DateTime t;
          try { t = Directory.GetLastWriteTimeUtc(d); } catch { continue; }
          if (best == null || t > bestT) { best = d; bestT = t; }
        }
        if (best != null) {
          note = "配置路径已失效，改用最新版本目录 " + Path.GetFileName(best);
          return Path.Combine(best, "node.exe");
        }
      } catch { }
    }

    note = "配置路径不存在，且同级 versions 目录下未找到可用 node.exe（保持原值，启动将报错）";
    return configured ?? "";
  }
}
