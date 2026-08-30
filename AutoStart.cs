using System;
using System.IO;
using System.Windows.Forms;

namespace QwenTray;

public static class AutoStart {
  static string ShortcutPath() {
    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DSH托盘.lnk");
  }
  public static bool Enabled() {
    try { return File.Exists(ShortcutPath()); } catch { return false; }
  }
  public static void Set(bool on) {
    try {
      string p = ShortcutPath();
      if (on) {
        var wsType = Type.GetTypeFromProgID("WScript.Shell");
        if (wsType == null) return;
        dynamic shell = Activator.CreateInstance(wsType);
        dynamic lnk = shell.CreateShortcut(p);
        lnk.TargetPath = Application.ExecutablePath;
        lnk.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
        string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whale-girl.ico");
        if (File.Exists(ico)) lnk.IconLocation = ico;
        lnk.Description = "DSH托盘";
        lnk.Save();
      } else {
        if (File.Exists(p)) File.Delete(p);
      }
    } catch { }
  }
}
