using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace QwenTray;

// dsh 插件清单与安全模式补丁中心。
// 插件四来源：① profile package.json 的 dsh.profile.bundles（官方 @deepseek-ai/* + 外部）
//             ② `bin.js --profile web --dump-config` 合成树（权威 entry id 与 disabled 态，分节头标 bundle 归属）
//             ③ cordis.patch.yml 用户层（insert/id/disabled 条目）
//             ④ super-injector 运行时注入（~/.dsh/super-injector/registry.json，dump 看不到！禁用=注册表手术）
// 安全模式 = 生成 --patch overlay（- id: X / disabled: true），零侵入用户配置；官方白名单 = dsh-base + dsh-web-app
// 两个 bundle 贡献的 entry + 用户 patch 层里的 @deepseek-ai/* 条目。

public class PluginEntryInfo {
  public string Id = "";
  public string Name = "";          // entry 的 package name（可能与 bundle 不同，如 archify→@deepseek-ai/dsh-skill-filesystem）
  public string Bundle = "";        // 贡献它的 bundle（分节头 , patched by 之前的部分）
  public bool DisabledEffective;    // dump 树里 disabled: true
  public bool DisabledDynamic;      // disabled: !!js 动态表达式（如 better-sidebar）
  public bool Official;             // 白名单成员（安全模式永不禁用）
}
public class DevPluginInfo {
  public string Name = "";          // registry 里的包名，如 @dsh-external/dsh-topic-relay
  public string Dir = "";
  public string At = "";
  public bool DisabledByTray;       // 在托盘禁用清单里（registry 手术移出）
}
public class PluginInventory {
  public List<PluginEntryInfo> Entries = new();
  public List<DevPluginInfo> DevPlugins = new();
  public bool DumpOk;               // dump-config 是否成功（失败则 entry 列表只有静态推导）
  public string DumpError = "";
  public List<string> Notes = new();
}

public static class PluginCenter {
  public static string PatchPath(){ return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dsh-tray-safemode.patch.yml"); }
  static string ProfilesWeb(AppConfig cfg){ return Path.Combine(DshHome(cfg), "profiles", "web"); }
  public static string DshHome(AppConfig cfg){ return string.IsNullOrEmpty(cfg.DshHomeDir) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh") : cfg.DshHomeDir; }
  static string RegistryPath(AppConfig cfg){ return Path.Combine(DshHome(cfg), "super-injector", "registry.json"); }
  static string RegistryDisabledPath(AppConfig cfg){ return Path.Combine(DshHome(cfg), "super-injector", "registry.tray-disabled.json"); }

  // —— dump-config 合成树解析（权威 entry 层）——
  public static PluginInventory Scan(AppConfig cfg, HashSet<string> disabledDev, bool withDump){
    var inv = new PluginInventory();
    if(withDump) ParseDump(cfg, inv);
    if(!inv.DumpOk) StaticEntries(cfg, inv);   // dump 失败：从 bundles 静态推导 entry id（id 猜错无害：patch 仅告警 entry not found）
    // 白名单判定：dsh-base / dsh-web-app 分节，或用户 patch 层里 name 以 @deepseek-ai/ 开头的条目
    foreach(var e in inv.Entries){
      string b = e.Bundle;
      bool officialBundle = b == "@deepseek-ai/dsh-base" || b == "@deepseek-ai/dsh-web-app";
      bool userPatchOfficial = b.EndsWith("cordis.patch.yml") && e.Name.StartsWith("@deepseek-ai/");
      e.Official = officialBundle || userPatchOfficial;
    }
    ScanDevPlugins(cfg, inv, disabledDev);
    return inv;
  }

  static void ParseDump(AppConfig cfg, PluginInventory inv){
    try{
      if(string.IsNullOrEmpty(cfg.DshNodeExe) || string.IsNullOrEmpty(cfg.DshCliBinJs)){ inv.DumpError = "未配置 DshNodeExe/DshCliBinJs"; return; }
      var psi = new ProcessStartInfo(cfg.DshNodeExe, "\"" + cfg.DshCliBinJs + "\" --profile web --dump-config"){
        WorkingDirectory = string.IsNullOrEmpty(cfg.DshWorkDir) ? Path.GetDirectoryName(cfg.DshCliBinJs) ?? "" : cfg.DshWorkDir,
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
      };
      var p = Process.Start(psi);
      if(p == null){ inv.DumpError = "dump-config 进程启动失败"; return; }
      string outp = p.StandardOutput.ReadToEnd();
      p.StandardError.ReadToEnd();
      if(!p.WaitForExit(60000)){ try{ p.Kill(); }catch{} inv.DumpError = "dump-config 超时(60s)"; return; }
      if(p.ExitCode != 0){ inv.DumpError = "dump-config exit=" + p.ExitCode; return; }
      string bundle = "";
      PluginEntryInfo? cur = null;
      var byId = new Dictionary<string, PluginEntryInfo>();
      foreach(var rawLine in outp.Split('\n')){
        var line = rawLine.TrimEnd('\r');
        if(line.StartsWith("# == ")){
          bundle = line.Substring(5).Trim();
          int ci = bundle.IndexOf(", patched by");
          if(ci > 0) bundle = bundle.Substring(0, ci).Trim();
          cur = null; continue;
        }
        if(line.StartsWith("- id: ")){
          var id = line.Substring(6).Trim();
          if(!byId.TryGetValue(id, out cur)){
            cur = new PluginEntryInfo{ Id = id, Bundle = bundle };
            byId[id] = cur; inv.Entries.Add(cur);
          }
          continue;
        }
        if(cur == null) continue;
        var t = line.Trim();
        if(t.StartsWith("name: ") && cur.Name.Length == 0) cur.Name = t.Substring(6).Trim().Trim('\'', '"');
        else if(t == "disabled: true") cur.DisabledEffective = true;
        else if(t.StartsWith("disabled: !!js")) cur.DisabledDynamic = true;
      }
      inv.DumpOk = inv.Entries.Count > 0;
      if(!inv.DumpOk) inv.DumpError = "dump-config 输出无 entry";
    }catch(Exception ex){ inv.DumpError = ex.Message; }
  }

  // dump 失败兜底：读 package.json bundles，再读每个 bundle 包目录自带的 cordis.patch.yml
  // （bundle 层的 entry id 由作者在该文件声明，如 better-sidebar/dsh-super-injector，无统一推导规则）；
  // 包目录=profiles/web/node_modules/<bundle>（link: 依赖为 junction，可直接读）。缺失时回退朴素 id。
  static void StaticEntries(AppConfig cfg, PluginInventory inv){
    try{
      var pkg = Path.Combine(ProfilesWeb(cfg), "package.json");
      if(!File.Exists(pkg)) return;
      using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
      if(!doc.RootElement.TryGetProperty("dsh", out var d) || !d.TryGetProperty("profile", out var pr) || !pr.TryGetProperty("bundles", out var bs)) return;
      int fromPatch = 0;
      foreach(var b in bs.EnumerateArray()){
        var name = b.GetString() ?? "";
        if(name.Length == 0) continue;
        var ids = ReadBundlePatchIds(Path.Combine(ProfilesWeb(cfg), "node_modules", name.Replace('/', Path.DirectorySeparatorChar), "cordis.patch.yml"));
        if(ids.Count == 0) ids.Add(name.TrimStart('@').Replace('/', '-').Replace('@', '-'));
        foreach(var id in ids){
          if(inv.Entries.Any(e => e.Id == id)) continue;
          inv.Entries.Add(new PluginEntryInfo{ Id = id, Name = name, Bundle = name });
          fromPatch++;
        }
      }
      inv.Notes.Add("dump-config 不可用（" + inv.DumpError + "），entry 列表来自 bundle 自带 cordis.patch.yml 静态解析（" + fromPatch + " 条）");
    }catch(Exception ex){ inv.Notes.Add("静态推导失败: " + ex.Message); }
  }

  // 解析 bundle 自带 cordis.patch.yml 的 insert/id 条目 id（行级扫描，忽略注释）
  static List<string> ReadBundlePatchIds(string patchFile){
    var ids = new List<string>();
    try{
      if(!File.Exists(patchFile)) return ids;
      foreach(var raw in File.ReadLines(patchFile)){
        var l = raw.TrimEnd();
        var t = l.Trim();
        if(t.StartsWith("#")) continue;
        if(t.StartsWith("- id: ")){ var id = t.Substring(6).Trim().Trim('\'', '"'); if(id.Length > 0 && !ids.Contains(id)) ids.Add(id); }
      }
    }catch{}
    return ids;
  }

  // —— super-injector 注入插件（dump 看不到的运行时注入层）——
  static void ScanDevPlugins(AppConfig cfg, PluginInventory inv, HashSet<string> disabledDev){
    foreach(var item in ReadRegistry(RegistryPath(cfg))) inv.DevPlugins.Add(new DevPluginInfo{ Name = item.name, Dir = item.dir, At = item.at, DisabledByTray = false });
    foreach(var item in ReadRegistry(RegistryDisabledPath(cfg))) inv.DevPlugins.Add(new DevPluginInfo{ Name = item.name, Dir = item.dir, At = item.at, DisabledByTray = true });
    foreach(var d in inv.DevPlugins) if(disabledDev.Contains(d.Name)) d.DisabledByTray = true;
  }

  static List<(string name, string dir, string at)> ReadRegistry(string path){
    var list = new List<(string, string, string)>();
    try{
      if(!File.Exists(path)) return list;
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      if(doc.RootElement.ValueKind != JsonValueKind.Array) return list;
      foreach(var it in doc.RootElement.EnumerateArray()){
        string n = it.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "";
        string d = it.TryGetProperty("dir", out var de) ? de.GetString() ?? "" : "";
        string a = it.TryGetProperty("at", out var ae) ? ae.GetString() ?? "" : "";
        if(n.Length > 0) list.Add((n, d, a));
      }
    }catch{}
    return list;
  }

  // registry 手术：把 disabledDev 里的条目从 registry.json 移到 registry.tray-disabled.json（启用=移回）。
  // 注入器按 registry 重新注入 → 移出即禁用（重启 DSH 后生效）。首次手术备份 registry.json.bak-tray。
  public static string ApplyDevDisabled(AppConfig cfg, HashSet<string> disabledDev){
    try{
      string regPath = RegistryPath(cfg), disPath = RegistryDisabledPath(cfg);
      var active = ReadRegistry(regPath);
      var disabled = ReadRegistry(disPath);
      var moveOut = active.Where(x => disabledDev.Contains(x.name)).ToList();
      var moveIn = disabled.Where(x => !disabledDev.Contains(x.name)).ToList();
      if(moveOut.Count == 0 && moveIn.Count == 0) return "注入插件清单无变化";
      if(File.Exists(regPath) && !File.Exists(regPath + ".bak-tray")) File.Copy(regPath, regPath + ".bak-tray");
      var newActive = active.Where(x => !disabledDev.Contains(x.name)).Concat(moveIn).ToList();
      var newDisabled = disabled.Where(x => disabledDev.Contains(x.name)).Concat(moveOut)
        .GroupBy(x => x.name).Select(g => g.First()).ToList();
      WriteRegistry(regPath, newActive);
      WriteRegistry(disPath, newDisabled);
      return "注入插件注册表已更新：禁用 " + newDisabled.Count + " 个（重启 DSH 后生效；备份 " + regPath + ".bak-tray）";
    }catch(Exception ex){ return "registry 手术失败: " + ex.Message; }
  }

  static void WriteRegistry(string path, List<(string name, string dir, string at)> items){
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var arr = items.Select(x => new Dictionary<string, string>{ ["dir"] = x.dir, ["name"] = x.name, ["at"] = x.at }).ToList();
    File.WriteAllText(path, JsonSerializer.Serialize(arr, new JsonSerializerOptions{ WriteIndented = true }));
  }

  // —— 生成 --patch overlay 文本；返回 ("", 0) 表示无需 patch ——
  public static (string yaml, int count) BuildPatchYaml(PluginInventory inv, bool safeMode, HashSet<string> disabledEntries){
    var ids = new List<string>();
    if(safeMode) ids.AddRange(inv.Entries.Where(e => !e.Official && !e.DisabledEffective).Select(e => e.Id));
    ids.AddRange(inv.Entries.Where(e => disabledEntries.Contains(e.Id) && !e.DisabledEffective).Select(e => e.Id));
    ids = ids.Distinct().ToList();
    if(ids.Count == 0) return ("", 0);
    var sb = new StringBuilder();
    sb.Append("# DSH tray generated overlay - do not edit (regenerated each start)\n");
    if(safeMode) sb.Append("# safe-mode: whitelist = @deepseek-ai/dsh-base + dsh-web-app + user official inserts\n");
    foreach(var id in ids) sb.Append("- id: ").Append(id).Append("\n  disabled: true\n");
    return (sb.ToString(), ids.Count);
  }

  // 把 overlay 写到固定路径；empty 时删除旧文件（避免误用残留 patch）
  public static string WritePatchFile(string yaml){
    string p = PatchPath();
    try{
      if(yaml.Length == 0){ if(File.Exists(p)) File.Delete(p); return ""; }
      File.WriteAllText(p, yaml);
      return p;
    }catch{ return ""; }
  }
}
