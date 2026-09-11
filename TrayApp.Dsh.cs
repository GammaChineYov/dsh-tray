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

// S1（2026-09-11）拆自 Program.cs（partial 3/5：DSH 进程 + 环境清洗 + 日志跟踪）：零逻辑改动，仅位移。
public partial class TrayApp {
  bool DshUp(){ try{ using(var c=new TcpClient()){ var t=c.ConnectAsync("127.0.0.1",3080); if(t.Wait(400)) return c.Connected; } }catch{} return false; } // 仅后台调用（400ms 超时）
  Bitmap Dot(Color c){ var b=new Bitmap(16,16); using(var g=Graphics.FromImage(b)){ g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using(var br=new SolidBrush(c)) g.FillEllipse(br,2,2,12,12); } return b; }
  void RefreshDshUi(){
    if(dshMenu==null) return;
    if(dotRed==null) dotRed=Dot(Color.Firebrick);
    if(dotYellow==null) dotYellow=Dot(Color.Goldenrod);
    if(dotGreen==null) dotGreen=Dot(Color.ForestGreen);
    dshMenu.Image = dshState==2?dotGreen : dshState==1?dotYellow : dotRed;
    if(dshStatusItem!=null) dshStatusItem.Text = dshState==2?"状态：运行中" : dshState==1?"状态：启动中" : "状态：未启动";
    if(dshStartItem!=null) dshStartItem.Enabled = dshState!=2 && dshState!=1;   // 运行/启动中禁止重复启动
    if(dshRestartItem!=null) dshRestartItem.Enabled = true;
    if(dshStopItem!=null) dshStopItem.Enabled = dshState!=0;                   // 未启动时停止无意义
  }
  void DshStart(){
    if(DshUp()){ logForm.Append("DSH 已在运行（端口 3080 监听中）\r\n"); return; }
    if(string.IsNullOrEmpty(cfg.DshNodeExe)||string.IsNullOrEmpty(cfg.DshCliBinJs)||string.IsNullOrEmpty(cfg.DshWorkDir)){
      logForm.Append("未配置 DSH 启动路径：请编辑 dsh-tray-config.json（DshNodeExe / DshCliBinJs / DshWorkDir）\r\n");
      try{ Process.Start(new ProcessStartInfo(Config.Path_){UseShellExecute=true}); }catch{}
      return;
    }
    HealProfileOrphanLock(true);   // 启动前清孤儿写锁，否则 composeProfile 必 2s 超时崩（见方法注释）
    string outLog=string.IsNullOrEmpty(cfg.DshOutLog)?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dsh-web-out.log"):cfg.DshOutLog;
    string errLog=string.IsNullOrEmpty(cfg.DshErrLog)?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dsh-web-err.log"):cfg.DshErrLog;
    dshState=1; dshStartMs=Environment.TickCount; dshTimeoutLogged=false; Ui(()=>RefreshDshUi());
    // —— 安全模式/自定义禁用：生成 --patch overlay（零侵入用户配置；web 子命令拒收父选项 → 带 patch 时改用 --profile web 形式）——
    string dshArgs="\""+cfg.DshCliBinJs+"\" web"; string patchNote=""; string envNote="";
    try{
      if(safeMode||disabledEntries.Count>0){
        var inv=PluginCenter.Scan(cfg,disabledDevPlugins,true);
        var built=PluginCenter.BuildPatchYaml(inv,safeMode,disabledEntries);
        string pf=PluginCenter.WritePatchFile(built.yaml);
        if(pf.Length>0){ dshArgs="\""+cfg.DshCliBinJs+"\" --profile web --patch \""+pf+"\""; patchNote=" | patch: 禁用 "+built.count+" 个 entry"+(safeMode?"（安全模式白名单）":""); }
        if(!inv.DumpOk) patchNote+=" | dump-config 不可用，entry 为静态推导";
      } else PluginCenter.WritePatchFile("");   // 无需 patch 时清掉历史残留 overlay
      if(disabledDevPlugins.Count>0) PluginCenter.ApplyDevDisabled(cfg,disabledDevPlugins);   // 幂等，保证注入插件禁用清单与 cfg 一致
    }catch(Exception ex){ patchNote=" | patch 生成失败: "+ex.Message; }
    try{
      var psi=new ProcessStartInfo(cfg.DshNodeExe){ WorkingDirectory=cfg.DshWorkDir, UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden, RedirectStandardOutput=true, RedirectStandardError=true, Arguments=dshArgs };
      envNote=ScrubWorkBuddyEnv(psi);   // 关键：不清会把 WorkBuddy 的 safe-delete 守卫带进 dsh → 删锁被拒 → 孤儿锁
      var p=Process.Start(psi);
      if(p==null){ logForm.Append(">>> 启动 DSH 失败（Process.Start 返回 null）\r\n"); dshState=0; Ui(()=>RefreshDshUi()); return; }
      // 逐行打戳：旧版裸写 e.Data → 整份 out/err 事后无法按时间定位故障
      p.OutputDataReceived+=(o,e)=>{ if(!string.IsNullOrEmpty(e.Data)){ try{ File.AppendAllText(outLog,Stamp1Line(e.Data)); }catch{} } };
      p.ErrorDataReceived+=(o,e)=>{ if(!string.IsNullOrEmpty(e.Data)){ try{ File.AppendAllText(errLog,Stamp1Line(e.Data)); }catch{} } };
      p.BeginOutputReadLine(); p.BeginErrorReadLine();
      logForm.Append(">>> 启动 DSH：\""+cfg.DshNodeExe+"\" \""+cfg.DshCliBinJs+"\" "+(dshArgs.Contains("--patch")?"--profile web --patch（安全模式/禁用清单）":"web")+"（工作目录 "+cfg.DshWorkDir+"），等待端口 3080 就绪 ..."+patchNote+(envNote.Length>0?" | 环境清洗: "+envNote:"")+"\r\n");
      logForm.Append("    stdout→"+outLog+"\r\n    stderr→"+errLog+"\r\n");
    }catch(Exception ex){ logForm.Append(">>> 启动 DSH 失败: "+ex.Message+"\r\n"); dshState=0; Ui(()=>RefreshDshUi()); Ui(()=>{ try{ MessageBox.Show("启动 DSH 失败: "+ex.Message); }catch{} }); }
  }
  bool DshKill(){
    bool killed=false;
    try{
      var psi=new ProcessStartInfo("netstat","-ano"){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true};
      var p=Process.Start(psi);
      if(p!=null){ string o=p.StandardOutput.ReadToEnd(); p.WaitForExit(3000);
        foreach(var line in o.Split('\n')) if(line.Contains(":3080")&&line.Contains("LISTENING")){ var parts=line.Split(new char[]{' '},StringSplitOptions.RemoveEmptyEntries); int pid; if(parts.Length>0&&int.TryParse(parts[parts.Length-1],out pid)){ try{ Process.GetProcessById(pid).Kill(); killed=true; }catch{} } break; } }
    }catch{}
    try{
      using(var searcher=new ManagementObjectSearcher("SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='node.exe'"))
      using(var coll=searcher.Get())
      foreach(ManagementBaseObject mo in coll){ try{ int pid=Convert.ToInt32(mo["ProcessId"]); string cl=Convert.ToString(mo["CommandLine"])??""; if(cl.Contains("bin.js")&&cl.Contains(" web")){ try{ Process.GetProcessById(pid).Kill(); killed=true; }catch{} } if(mo is IDisposable dd) dd.Dispose(); }catch{} }
    }catch{}
    return killed;
  }
  void DshStop(){
    bool killed=DshKill();
    logForm.Append(killed?"停止 DSH：已终止端口 3080 监听进程（node bin.js web）\r\n":"停止 DSH：未发现运行中的 DSH（端口 3080 无监听）\r\n");
    dshState=0; dshStartMs=0; Ui(()=>RefreshDshUi());
  }
  void DshRestart(){
    logForm.Append(">>> 重启 DSH ...\r\n");
    DshKill();
    for(int i=0;i<20;i++){ if(!DshUp()) break; System.Threading.Thread.Sleep(500); }  // 等待端口 3080 释放
    System.Threading.Thread.Sleep(800);
    DshStart();
  }
  // 后台执行（菜单点启停/重启不阻塞 UI 线程）；UI 更新一律经 Ui() 回 UI 线程（锚点=uiDisp，不再用 logForm 当锚点）
  void Bg(Action a){ if(a==null) return; try{ Task.Run(()=>{ try{ a(); }catch(Exception ex){ ReportEx("Bg",ex); } }); }catch(Exception ex){ ReportEx("Bg.schedule",ex); } }
  void Ui(Action a){ if(a==null) return; uiDisp.Post("Ui",a); }
  // 统一异常上报（S0-③）：后台任务与 UI 回调里的异常不再静默 —— 落 tray-ex.log（与全局 handler 同一个文件）+ 日志窗口可见
  void ReportEx(string tag,Exception ex){
    try{ File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tray-ex.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" "+tag+" "+(ex!=null?ex.ToString():"(null)")+"\r\n"); }catch{}
    if(Interlocked.Exchange(ref _reporting,1)==1) return;   // 防"上报自身再抛"递归
    try{ try{ logForm?.Append("[错误] "+tag+": "+(ex!=null?ex.Message:"?")+"\r\n"); }catch{} }finally{ Interlocked.Exchange(ref _reporting,0); }
  }
  // 统一日志窗口（2026-09-11）：托盘事件与 DSH 输出共用一个窗口的两个页签，菜单两个入口都开它
  LogForm MakeLogForm(){
    var f=new LogForm();
    f.LogDirProvider=()=>{ try{ string? d=Path.GetDirectoryName(DshOutPath()); return d??""; }catch{ return ""; } };
    f.ClearFilesAction=ClearDshLogFiles;
    try{
      var pp=new PerfPanel();
      pp.Store=perf?.Store;
      pp.BenchAction=(port,log)=> perf!=null ? perf.BenchAsync(port,log) : Task.FromResult<PerfBenchResult?>(null);
      pp.OpenDirAction=(dir)=>{ try{ if(!string.IsNullOrEmpty(dir)&&Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir){UseShellExecute=true}); }catch{} };
      pp.FlushAction=()=>{ try{ perf?.Flush(); }catch{} };
      f.AttachPerfPanel(pp);
    }catch{}
    return f;
  }
  void ShowLogWin(){ ShowLogWinAt(false); }
  void ShowLogWinPerf(){
    try{
      if(logForm==null||logForm.IsDisposed) logForm=MakeLogForm();
      logForm.Show(); if(logForm.WindowState==FormWindowState.Minimized) logForm.WindowState=FormWindowState.Normal;
      logForm.ShowPerf(); logForm.BringToFront(); logForm.Activate();
    }catch(Exception ex){ MessageBox.Show("打开模型性能面板失败: "+ex.Message); }
  }
  void ShowLogWinAt(bool dsh){
    try{
      if(logForm==null||logForm.IsDisposed) logForm=MakeLogForm();
      logForm.Show(); if(logForm.WindowState==FormWindowState.Minimized) logForm.WindowState=FormWindowState.Normal;
      logForm.ShowTab(dsh); logForm.BringToFront(); logForm.Activate();
    }catch(Exception ex){ MessageBox.Show("打开日志窗口失败: "+ex.Message); }
  }
  void OpenDir(string path,string label){ try{ if(Directory.Exists(path)){ Process.Start(new ProcessStartInfo(path){UseShellExecute=true}); } else logForm.Append(label+" 目录不存在："+path+"\r\n"); }catch(Exception ex){ logForm.Append("打开 "+label+" 失败: "+ex.Message+"\r\n"); } }
  string DshProgDir(){
    if(!string.IsNullOrEmpty(cfg.DshWorkDir)&&Directory.Exists(cfg.DshWorkDir)) return cfg.DshWorkDir;
    if(!string.IsNullOrEmpty(cfg.DshCliBinJs)){ try{ var f=new FileInfo(cfg.DshCliBinJs); var d=f.Directory?.Parent?.Parent?.Parent; if(d!=null&&d.Exists) return d.FullName; }catch{} }
    return AppDomain.CurrentDomain.BaseDirectory;
  }
  string DshHomeDir(){ if(!string.IsNullOrEmpty(cfg.DshHomeDir)) return cfg.DshHomeDir; return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"); }
  // —— 孤儿写锁 profiles\node_modules.lock（2026-09-10）——
  // dsh 的 composeProfile → healProfilesModuleFallback 每次启动都校验共享 module fallback
  // （$DSH_HOME/profiles/node_modules）与当前安装依赖闭包是否一致，不一致就重写软链，并用这个
  // lock 做跨进程互斥；而 atomic-write 的 withFileLock **按设计不回收陈旧锁**（源码注释原文：
  // "the contender never removes an existing lock... orphan recovery is an operator action"），
  // 只等 2 秒就放弃。任何一次启动在持锁期间被杀（托盘「停止/重启」用的就是 Kill），锁就永久
  // 留下 → 之后每次启动都崩：
  //   Error: atomic-write: timed out waiting for the writer lock at ...\profiles\node_modules.lock
  // 策略：只在锁内 pid 已不存在时删（陈旧/空内容也算孤儿）；pid 仍存活 → 保留，别抢正在 heal 的实例。
  string ProfileLockPath(){ return Path.Combine(DshHomeDir(),"profiles","node_modules.lock"); }
  string HealProfileOrphanLock(bool log){
    try{
      string lp=ProfileLockPath();
      if(!File.Exists(lp)){ if(log) logForm.Append("[lock] 无启动写锁文件（正常）\r\n"); return "absent"; }
      string raw=""; try{ raw=(File.ReadAllText(lp)??"").Trim(); }catch{}
      int pid; bool alive=false;
      if(raw.Length>0&&int.TryParse(raw,out pid)&&pid>0){ try{ using(var pr=Process.GetProcessById(pid)) alive=!pr.HasExited; }catch{ alive=false; } }
      if(alive){ string m="[lock] node_modules.lock 由存活进程 pid "+raw+" 持有 —— 保留（该实例正在重写模块软链，稍后自动释放）\r\n"; if(log) logForm.Append(m); return "held pid="+raw; }
      File.Delete(lp);
      string m2="[lock] 已清除孤儿写锁 node_modules.lock（持有者 pid "+(raw.Length>0?raw:"空")+" 已不存在）\r\n"; if(log) logForm.Append(m2);
      return "orphan-removed pid="+(raw.Length>0?raw:"empty");
    }catch(Exception ex){ string m="[lock] 写锁处理失败（继续启动）: "+ex.Message+"\r\n"; if(log) logForm.Append(m); return "error: "+ex.Message; }
  }
  static string Stamp1Line(string s){ return "["+DateTime.Now.ToString("HH:mm:ss")+"] "+s+Environment.NewLine; }
  // —— WorkBuddy(CodeBuddy) 环境清洗（2026-09-11，dsh「老是无缘无故崩」的真凶之一）——
  // 现象：dsh-web-err.log 里混着这样一条：
  //   Error: [safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED] {"count":50,"threshold":50,"scope":"turn","targets":["...\profiles\node_modules.lock"]}
  // 根因：托盘若在 WorkBuddy(CodeBuddy) 血统的进程里被拉起，DshStart 起的 node 子进程会**继承**
  //   NODE_OPTIONS（--require …\node-language-shim.cjs）。该 shim 会挂上 safe-delete 钩子，钩子里有一个
  //   "按 turn 统计、阈值 50 次删除"的批量守卫；而判定 turn 用的 CODEBUDDY_TOOL_CALL_ID 在进程启动时
  //   就冻死了 —— dsh 是长驻进程，turn 永不滚动 → 计数只涨不归零，删到第 50 次后**该进程内所有删除被
  //   永久拒绝**。中招的正是 composeProfile → healProfilesModuleFallback 释放 profiles\node_modules.lock
  //   的那个 rm：异常抛出 → 锁留在盘上 → 之后每次启动 atomic-write 等锁 2s 超时崩（err 日志里紧随其后的
  //   4 条 "timed out waiting for the writer lock" 就是它造成的连锁）。
  // 处置：dsh 不需要 WorkBuddy 的沙箱钩子 —— 摘掉 CODEBUDDY_*/CLAUDE_SESSION_ID，并按 token 剔除
  //   NODE_OPTIONS 里指向 WorkBuddy/CodeBuddy shim 的 --require；**但保留其它 token**（如 --use-system-ca，
  //   企业网 TLS 需要），另置 CODEBUDDY_SAFE_DELETE_ENABLED=0 作双保险。
  static string ScrubWorkBuddyEnv(ProcessStartInfo psi){
    var removed=new List<string>();
    try{
      string? oldNodeOpt=psi.EnvironmentVariables.ContainsKey("NODE_OPTIONS")?Convert.ToString(psi.EnvironmentVariables["NODE_OPTIONS"]):null;
      var keys=new List<string>();
      foreach(System.Collections.DictionaryEntry de in psi.EnvironmentVariables) keys.Add(Convert.ToString(de.Key)??"");
      foreach(var k in keys){
        if(k.Length==0||k=="NODE_OPTIONS") continue;   // NODE_OPTIONS 单独处理：只剔 token，不整条删
        string ku=k.ToUpperInvariant();
        if(ku.StartsWith("CODEBUDDY_")||ku=="CLAUDE_SESSION_ID"){ psi.EnvironmentVariables.Remove(k); removed.Add(k); }
      }
      if(!string.IsNullOrEmpty(oldNodeOpt)){
        string neu=StripWorkBuddyShim(oldNodeOpt);
        if(!string.Equals(neu,oldNodeOpt,StringComparison.Ordinal)) removed.Add("NODE_OPTIONS:shim");
        if(neu.Length==0) psi.EnvironmentVariables.Remove("NODE_OPTIONS"); else psi.EnvironmentVariables["NODE_OPTIONS"]=neu;
      }
      psi.EnvironmentVariables["CODEBUDDY_SAFE_DELETE_ENABLED"]="0";
    }catch{}
    return removed.Count==0?"无需清洗":"已摘除 "+string.Join("/",removed);
  }
  static string StripWorkBuddyShim(string v){
    if(string.IsNullOrEmpty(v)) return v??"";
    var rx=new System.Text.RegularExpressions.Regex(@"--require\s*=\s*(""[^""]*""|'[^']*'|\S+)");
    string outv=rx.Replace(v,delegate(System.Text.RegularExpressions.Match m){
      string inner=m.Groups[1].Value.Trim('"','\'');
      string low=inner.ToLowerInvariant();
      bool wb=low.Contains("workbuddy")||low.Contains("codebuddy")
             ||low.Contains("node-language-shim")||low.Contains("node-safe-delete-shim")
             ||low.Contains("node-brokered-fs-shim")||low.Contains("genie-safe-delete");
      return wb?"":m.Value;
    });
    return outv.Trim();
  }
  // 「清空日志文件」按钮：截断 out/err 各留一行分隔记录，并把跟踪位置归零（否则跟踪指针失效）
  void ClearDshLogFiles(){
    if(logForm==null||logForm.IsDisposed) return;
    var rep=new System.Text.StringBuilder();
    foreach(string p in new string[]{DshOutPath(),DshErrPath()}){
      try{
        long old=File.Exists(p)?new FileInfo(p).Length:0;
        File.WriteAllText(p,"=== "+DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" 由托盘清空（原 "+old+" 字节）===\r\n");
        rep.Append("    "+p+"  已清空（原 "+old+" 字节）");
      }catch(Exception ex){ rep.Append("    清空失败 "+p+" : "+ex.Message); }
    }
    lastOutPos=0; lastErrPos=0;
    logForm.ShowTab(true);
    logForm.ClearDshView();
    logForm.AppendDshStamp(">>> 已清空 DSH 日志文件（"+DateTime.Now.ToString("HH:mm:ss")+"）");
    logForm.AppendDshStamp(rep.ToString());
    logForm.AppendDshStamp("（此后 out/err 的新行都由托盘带 [HH:mm:ss] 写入）");
    logForm.Append(">>> 清空 DSH 日志文件 out/err（窗口与磁盘文件同步清空）\r\n");
  }
  string DshOutPath(){ return string.IsNullOrEmpty(cfg.DshOutLog)?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dsh-web-out.log"):cfg.DshOutLog; }
  string DshErrPath(){ return string.IsNullOrEmpty(cfg.DshErrLog)?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dsh-web-err.log"):cfg.DshErrLog; }
  public void OpenDshLog(){
    ShowLogWinAt(true);                       // 统一窗口：切到「DSH 输出」页签
    lastOutPos=0; lastErrPos=0;
    if(logForm==null||logForm.IsDisposed) return;
    logForm.ClearDshView();                   // 重开先清空：旧版每次都把整份文件再叠一遍，越开越多
    logForm.AppendDshStamp("── 载入 DSH 日志（dsh-web-out.log / dsh-web-err.log 实时跟踪）──");
    if(!File.Exists(DshOutPath())&&!File.Exists(DshErrPath())){
      logForm.AppendDshStamp("（暂无 DSH 日志文件）输出日志: "+DshOutPath());
      logForm.AppendDshStamp("错误日志: "+DshErrPath());
      logForm.AppendDshStamp("提示：DSH 由托盘启动/重启后会自动写入上述文件；若 DSH 由外部（控制台/脚本）启动，日志在它自己的启动方式里。");
      return;
    }
    DshTailLoad(true);
  }
  // 增量跟踪日志文件。两个要点：
  //  - len<pos（文件被「清空日志文件」截断或轮转）→ pos 必须回退归零，否则跟踪指针永远停在被截断前的
  //    EOF 上，之后再也读不到任何新行（静默失效，最难发现的一类 bug）；
  //  - initial 只读末尾 150KB 并标注"仅末尾"，避免几 MB 历史一次性灌进窗口。
  void TailLog(string path,string label,ref long pos,bool initial,LogForm win){
    try{
      var fi=new FileInfo(path); if(!fi.Exists) return;
      long len=fi.Length; if(len<pos) pos=0;
      long start = initial ? Math.Max(0,len-150000) : pos;
      if(len<=start){ pos=len; return; }
      using(var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){
        fs.Seek(start,SeekOrigin.Begin); var buf=new byte[len-start]; int rd=fs.Read(buf,0,buf.Length);
        string txt=System.Text.Encoding.UTF8.GetString(buf,0,rd);
        if(!string.IsNullOrEmpty(txt)){
          if(initial) win.AppendDshStamp("── "+label+"（"+(start>0?"仅末尾 "+(len-start)+" 字节":"全文 "+len+" 字节")+"）──");
          win.AppendDsh(txt);
        }
      }
      pos=len;
    }catch{}
  }
  void DshTailLoad(bool initial){
    if(logForm==null||logForm.IsDisposed) return;
    TailLog(DshOutPath(),"dsh-web-out.log",ref lastOutPos,initial,logForm);
    TailLog(DshErrPath(),"dsh-web-err.log",ref lastErrPos,initial,logForm);
  }
  public string DshLogSnapshot(){ if(logForm==null||logForm.IsDisposed) return "NO WINDOW"; try{ return logForm.Snapshot(); }catch(Exception ex){ return "snapshot err: "+ex.Message; } }
  // /props 的 build_info（如 b1-832fd6f）：升级 llama.cpp 会让同参数配置的性能变化，台账里记下它便于归因
  string ProbeBuildInfo(int port){
    try{
      using(var wc=new System.Net.WebClient()){ wc.Encoding=System.Text.Encoding.UTF8;
        string j=wc.DownloadString("http://127.0.0.1:"+port+"/props");
        using(var doc=System.Text.Json.JsonDocument.Parse(j)){
          if(doc.RootElement.TryGetProperty("build_info",out var b)) return b.GetString()??"";
        }
      }
    }catch{}
    return "";
  }
  void Log(Service svc,string s){ svc.log.Append(s); }
}
