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

// S1（2026-09-11）拆自 Program.cs（partial 5/5：Adopt + Tick + 退出 + 自检探针）：零逻辑改动，仅位移。
public partial class TrayApp {
  void Adopt(Service svc){
    if(svc.Running) return;
    try{
      var psi=new ProcessStartInfo("netstat","-ano"){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true};
      var p=Process.Start(psi); string o=p.StandardOutput.ReadToEnd(); p.WaitForExit(2000);
      foreach(var line in o.Split('\n')){ if(line.Contains(":"+svc.Port) && line.Contains("LISTENING")){ var parts=line.Split(new char[]{' '},StringSplitOptions.RemoveEmptyEntries); int pid; if(int.TryParse(parts[parts.Length-1], out pid)){ try{ var pr=Process.GetProcessById(pid); if(pr.ProcessName.ToLower().Contains("llama")){ svc.proc=pr; Log(svc,">>> 接管外部 "+svc.Name+" (PID "+pid+")\r\n"); } }catch{} } break; } }
    }catch{}
  }
  void Tick(){
    // 最近会话低频后台刷新：与菜单生命周期完全解耦，避免每次右键/关闭触发全量扫描拖慢 UI；重建带 RebuildRecentMenu 可见守卫
    if(Environment.TickCount-recentRefreshMs>60000){ recentRefreshMs=Environment.TickCount; ScheduleRecentRefresh(); }
    if(!adopted){ adopted=true; Task.Run(()=>{ foreach(var s in services) Adopt(s); }); }
    // —— 模型状态巡检（1s 纯内存判断；每 2s 一次后台探活）——
    // 进程已退出但还挂着「启动中」→ 判定启动失败；「启动中」→ /health 就绪后清标志并记录耗时；运行中 → /props 实测 ctx/视觉
    foreach(var s in services){ bool ex=false; try{ if(s.proc!=null) ex=s.proc.HasExited; }catch{} if(ex&&s.Starting){ s.Starting=false; s.startMs=0; logForm.Append("["+s.Name+"] 进程已退出（启动失败？见日志窗口 / model-start.log）\r\n"); try{ perf?.OnFail(s.Spec,"exit"); }catch{} } }
    svcTick++;
    if(svcTick%2==0){
      bool anyStarting=services.Any(s=>s.Starting);
      if(anyStarting||svcTick%10==0){
        var snap=services.Select(x=>(svc:x,starting:x.Starting,running:x.Running)).ToList();
        Task.Run(()=>{
          foreach(var t in snap){
            if(t.starting){
              if(HealthUp(t.svc.Port)){ long ms=t.svc.startMs; t.svc.Starting=false; t.svc.startMs=0; t.svc.runCtx=0; t.svc.runVision=-1;
                var pr=ProbeLlamaProps(t.svc.Port); if(pr.Item1>0) t.svc.runCtx=pr.Item1; if(pr.Item2.HasValue) t.svc.runVision=pr.Item2.Value?1:0;
                long used=(ms>0?Math.Max(0,(Environment.TickCount-ms)/1000):0);
                logForm.Append("["+t.svc.Name+"] 已就绪（端口 "+t.svc.Port+"，耗时 "+used+"s）\r\n");
                try{ perf?.OnReady(t.svc.Spec, ms, pr.Item1, ProbeBuildInfo(t.svc.Port)); }catch{} }
            } else if(t.running){
              var pr=ProbeLlamaProps(t.svc.Port); if(pr.Item1>0) t.svc.runCtx=pr.Item1; if(pr.Item2.HasValue) t.svc.runVision=pr.Item2.Value?1:0;
            } else {
              // 未运行：端口是否被外部/上一实例的 llama 占用（占用 → 状态行标注、允许「停止模型」兜底回收，并尝试接管）
              bool busy=HealthUp(t.svc.Port);
              if(busy){ t.svc.PortBusy=true; if(!t.svc.Running) Adopt(t.svc); }
              else t.svc.PortBusy=false;
            }
          }
        });
      }
    }
    RefreshSvcMenus();   // 二级菜单：状态圆点 / 运行状态行 / 运行时配置（签名未变则跳过，零重绘）
    gpuTick++; 
    double cpu=SysInfo.CpuPercent();
    var mem=SysInfo.Mem();
    if(gpuTick%5==0){
      // nvidia-smi (GpuInfo.Discover) + WMI CPU温度 采样较慢，放到后台线程，避免阻塞 UI 线程（右键/菜单即时响应）
      Task.Run(()=>{
        try{
          var gl=GpuInfo.Discover(); gpus=gl; gpuTip=string.Join("\n",gl.Select(g=>g.Line));
          double t=HwInfo.CpuTemp(); if(double.IsNaN(t)) t=SysInfo.CpuTemp(); cpuTemp=double.IsNaN(t)?"":t.ToString("0")+"°C"; // CPU 温度：HWiNFO(真) 优先 → ACPI 变化值兜底 → 无真值不显示
        }catch{}
      });
    }
    string tip="";
    foreach(var s in services) if(s.Running) tip+=Short(s)+"("+s.Port+"):"+s.State+" RAM "+s.RamGb+"\n";
    if(tip.Length==0)tip="（无运行中的模型）\n";
    tip+=gpuTip.Length>0?gpuTip:(gpus.Count>0?"GPU: 查询中":"GPU: 无");
    tip+="\nCPU: "+cpu.ToString("0")+"%"+(cpuTemp.Length>0?" "+cpuTemp:"")+" 内存: "+mem.usedPct.ToString("0")+"%";
    // 只在 tooltip 内容变化、无菜单/下拉打开、且距上次至少 3s 时才更新 icon.Text（NotifyIcon.Text=Shell_NotifyIcon，频繁设置会阻塞 UI 线程）
    string tipNow = tip.TrimEnd();
    if(!menu.Visible && tipNow != _lastTip && Environment.TickCount - _lastIconMs >= 3000){ _lastTip = tipNow; _lastIconMs = Environment.TickCount; try{ icon.Text=tipNow; }catch{} }
    // —— DSH 服务状态：端口探测放后台（不阻塞 UI 线程，每秒最多一次）——
    if(Interlocked.Exchange(ref _probeBusy,1)==0){
      Task.Run(()=>{ try{ dshPortUp = DshUp()?1:0; } catch{} finally { Interlocked.Exchange(ref _probeBusy,0); } });
    }
    if(dshPortUp>=0){
      bool dshUpNow = dshPortUp==1;
      if(dshUpNow){ if(dshState!=2){ dshState=2; logForm.Append("DSH 已就绪（端口 3080 监听中）\r\n"); RefreshDshUi(); } }
      else if(dshState==2){ dshState=0; logForm.Append("DSH 已停止（端口 3080 无监听）\r\n"); RefreshDshUi(); }
      else if(dshState==1 && Environment.TickCount-dshStartMs>90000){ if(!dshTimeoutLogged){ dshTimeoutLogged=true; logForm.Append("DSH 启动超时（90s）：端口 3080 未就绪，请查看 dsh-web-err.log\r\n"); } dshState=0; RefreshDshUi(); }
    }
    if(logForm!=null&&!logForm.IsDisposed&&logForm.DshTabOpened) DshTailLoad(false);   // 统一窗口：DSH 页签至少打开过一次才跟踪
    // 游标语义：Length 单调递增；窗口被裁剪时 Read 自动夹取到窗口起点（丢历史、不丢新日志）—— S0 P1-1/P1-2
    foreach(var svc in services){ long L=svc.log.Length; if(L>svc.lastLen){ string t=svc.log.Read(svc.lastLen); svc.lastLen=L; logForm.Append("["+svc.Name+"] "+t); } }
  }

  // 退出默认保留模型（2026-09-11 语义反转）：只退托盘、不停 llama-server。保留的模型继续在端口上服务，
  // 下次启动托盘由 Adopt()（netstat → 端口 LISTENING pid → proc）重新接管，无需重载 30-60s。
  // 要连模型一起停必须显式：CLI `--exit --stopall` / 菜单「退出（同时停止模型服务）」。
  void ExitApp(){ ExitApp(false); }
  void ExitApp(bool stopServices){
    if(stopServices) StopAll();
    else{ try{ logForm.Append(">>> 退出（保留模型服务：模型继续在端口上服务，下次启动托盘自动接管）\r\n"); }catch{} }
    icon.Visible=false; if(popup!=null)popup.Close(); Application.Exit();
  } // icon.Visible=false → NIM_DELETE，退出干净不留死图标
  // 优雅退出：外部 --exit [--stopall] 信号（走同一 ExitApp，先注销图标再退出，避免 kill /F 在通知区留下死图标槽）
  public void ExitFromSignal(){ ExitFromSignal(false); }
  public void ExitFromSignal(bool stopServices){ try{ Ui(()=>ExitApp(stopServices)); }catch{} }
  // 重启托盘：新实例先起（它会 Adopt 端口上已在跑的模型），旧实例**保留模型**退出 → 模型不再被重启托盘打断
  void RestartTray(){ try{ Process.Start(new ProcessStartInfo(Application.ExecutablePath){ UseShellExecute=true, WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory }); }catch{} ExitApp(false); }

  // —— 隐藏自检模式：--dump-menu 打印当前菜单结构（供开发验证，不进正式 UI）——
  public void ProbeOnce(){ dshPortUp = DshUp()?1:0; dshState = dshPortUp==1?2:0; RefreshDshUi(); }
  // —— 统一日志窗口自检（--selftest-logwin）：页签/工具栏/时间戳规则/环境清洗，逐项给 PASS/FAIL ——
  public string LogWinProbe(){
    var sb=new System.Text.StringBuilder();
    try{
      if(logForm==null||logForm.IsDisposed) return "LOGWIN PROBE FAIL: 无日志窗口";
      logForm.ShowTab(false); logForm.Append("自检托盘行 A");
      logForm.ShowTab(true);  logForm.AppendDshStamp("自检托盘行 B（写入 DSH 页）");
      logForm.AppendDsh("无戳历史行");                 // 模拟老文件的行 → 应补 [--:--:--]
      logForm.AppendDsh("[12:00:00] 已有戳的行");      // 已打戳 → 应原样保留
      string trayT=logForm.box.Text, dshT=logForm.dshBox.Text;
      string[] tl=trayT.TrimEnd('\r','\n').Split('\n');
      string lastTray=tl.Length>0?tl[tl.Length-1].TrimEnd('\r'):"";
      sb.Append(logForm.UiSummary()).Append("\r\n");
      sb.Append("托盘页末行        : ").Append(lastTray).Append("\r\n");
      sb.Append("TRAY-TIMESTAMP    = ").Append(System.Text.RegularExpressions.Regex.IsMatch(lastTray,@"^\[\d{2}:\d{2}:\d{2}\] ")?"PASS":"FAIL").Append("\r\n");
      sb.Append("LEGACY-MARKED     = ").Append(dshT.Contains("[--:--:--] 无戳历史行")?"PASS":"FAIL").Append("\r\n");
      sb.Append("STAMP-PRESERVED   = ").Append(dshT.Contains("[12:00:00] 已有戳的行")?"PASS":"FAIL").Append("\r\n");
      sb.Append("CLEAR-BUTTONS     = ").Append(logForm.HasClearButton()?"PASS":"FAIL").Append("\r\n");
      sb.Append("TABS-3            = ").Append(logForm.UiSummary().Contains("tabs=3")?"PASS":"FAIL").Append("\r\n");
      sb.Append("PERF-TAB          = ").Append(logForm.HasPerfPanel()?"PASS":"FAIL").Append("\r\n");
      // 环境清洗：造一个「WorkBuddy shim + --use-system-ca」的 NODE_OPTIONS，验证只剔 shim 不动别的
      var psi=new ProcessStartInfo("node.exe");
      psi.EnvironmentVariables["NODE_OPTIONS"]="--require=\"C:/Program Files/WorkBuddy/resources/app.asar.unpacked/cli/vendor/shim/node-language-shim.cjs\" --use-system-ca";
      psi.EnvironmentVariables["CODEBUDDY_SESSION_ID"]="probe";
      psi.EnvironmentVariables["CODEBUDDY_TOOL_CALL_ID"]="probe";
      string note=ScrubWorkBuddyEnv(psi);
      string no=psi.EnvironmentVariables.ContainsKey("NODE_OPTIONS")?Convert.ToString(psi.EnvironmentVariables["NODE_OPTIONS"]):"(removed)";
      sb.Append("env scrub note    : ").Append(note).Append("\r\n");
      sb.Append("NODE_OPTIONS 剩余 : ").Append(no).Append("\r\n");
      sb.Append("KEEP-USE-SYSTEM-CA= ").Append(no.Contains("--use-system-ca")?"PASS":"FAIL").Append("\r\n");
      sb.Append("DROP-WB-SHIM      = ").Append(no.ToLowerInvariant().Contains("workbuddy")?"FAIL":"PASS").Append("\r\n");
      sb.Append("DROP-SESSION-ID   = ").Append(psi.EnvironmentVariables.ContainsKey("CODEBUDDY_SESSION_ID")?"FAIL":"PASS").Append("\r\n");
      sb.Append("SAFE-DELETE-OFF   = ").Append((psi.EnvironmentVariables.ContainsKey("CODEBUDDY_SAFE_DELETE_ENABLED")&&Convert.ToString(psi.EnvironmentVariables["CODEBUDDY_SAFE_DELETE_ENABLED"])=="0")?"PASS":"FAIL").Append("\r\n");
    }catch(Exception ex){ sb.Append("LOGWIN PROBE EX: ").Append(ex.Message); }
    return sb.ToString();
  }
  public void DisposeForDump(){ try{ icon.Visible=false; icon.Dispose(); }catch{} try{ clock.Stop(); }catch{} try{ if(logForm!=null&&!logForm.IsDisposed){ logForm.Dispose(); } }catch{} try{ uiDisp?.Dispose(); }catch{} }
  public string DumpMenuText(){
    var sb=new System.Text.StringBuilder();
    sb.AppendLine("dshState="+dshState);
    foreach(ToolStripItem it in menu.Items){
      if(it is ToolStripSeparator){ sb.AppendLine("---"); continue; }
      sb.AppendLine("[ "+(it.Enabled?"":"(禁用) ")+it.Text+" ]");
      if(it is ToolStripMenuItem mi && mi.DropDownItems.Count>0){
        if(ReferenceEquals(mi,dshMenu)) sb.AppendLine("    状态图标: "+(dshState==2?"●绿 运行中":dshState==1?"●黄 启动中":"●红 未启动"));
        foreach(ToolStripItem sub in mi.DropDownItems){
          if(sub is ToolStripSeparator){ sb.AppendLine("    ---"); continue; }
          sb.AppendLine("    - "+(sub.Enabled?"":"(禁用) ")+sub.Text+((sub is ToolStripMenuItem cm&&cm.Checked)?" [勾选]":""));
        }
      }
    }
    return sb.ToString();
  }
  // 隐藏自检模式：--selftest-plugins 扫描插件清单并演练 patch 生成（供开发验证，不进正式 UI）
  public string PluginScanProbe(){
    var sb=new System.Text.StringBuilder();
    var inv=PluginCenter.Scan(cfg, disabledDevPlugins, true);
    sb.AppendLine("dumpOk="+inv.DumpOk+" err="+inv.DumpError);
    sb.AppendLine("entries="+inv.Entries.Count+" dev="+inv.DevPlugins.Count);
    foreach(var e in inv.Entries) sb.AppendLine((e.Official?"[官方] ":"[外部] ")+e.Id+" | "+e.Name+" | bundle="+e.Bundle+(e.DisabledEffective?" | 已禁用":""));
    foreach(var d in inv.DevPlugins) sb.AppendLine("[注入] "+d.Name+" | "+d.Dir+(d.DisabledByTray?" | 托盘禁用":""));
    var b1=PluginCenter.BuildPatchYaml(inv,false,new HashSet<string>());
    sb.AppendLine("patch(无禁用) count="+b1.count);
    var b2=PluginCenter.BuildPatchYaml(inv,true,new HashSet<string>());
    sb.AppendLine("patch(安全模式) count="+b2.count);
    foreach(var note in inv.Notes) sb.AppendLine("note: "+note);
    return sb.ToString();
  }
  // 隐藏自检模式：--selftest-rpc 验证 DshAuth 自签 cookie + DshRpc 链路（打真实 3080 的 session/list）
  public async Task<string> RpcProbe(){
    var r=new DshRpc(cfg.DshUrl, DshHomeDir());
    var res=await r.Call("session/list","{\"_request\":{}}");
    if(!res.ok) return "RPC FAIL: "+res.error;
    int n=-1; try{ using var d=System.Text.Json.JsonDocument.Parse(res.value); n=d.RootElement.GetProperty("items").GetArrayLength(); }catch{}
    return "RPC OK session/list items="+n;
  }
  public string MenuShowProbe(){
    try{ recentList=ReadRecent(); RebuildRecentMenu(); }catch(Exception ex){ return "APPLY_FAIL "+ex.GetType().Name+": "+ex.Message; }
    try{
      int n=menu.Items.Count;
      menu.Show(new Point(60,60));
      System.Threading.Thread.Sleep(200);
      return "SHOW_OK items="+n+" recent="+recentList.Count;
    }catch(Exception ex){ return "SHOW_FAIL "+ex.GetType().Name+": "+ex.Message; }
  }
  // 隐藏自检模式：--selftest-lock 检查/清除孤儿启动写锁（profiles\node_modules.lock）
  public string LockProbe(){
    string lp=ProfileLockPath();
    bool before=File.Exists(lp);
    string act=HealProfileOrphanLock(false);
    bool after=File.Exists(lp);
    return "lock: "+lp
      +"\r\nbefore: "+(before?"present":"absent")
      +"\r\naction: "+act
      +"\r\nafter: "+(after?"present":"absent")
      +"\r\nresult: "+((!after)?"OK (no stale lock left)":"KEPT (lock owner alive)");
  }
  // 隐藏自检模式：--selftest-svcmenu 演练二级菜单（状态映射矩阵 + 每服务运行时配置渲染），不启停真实服务
  public string SvcMenuProbe(){
    var sb=new System.Text.StringBuilder();
    sb.AppendLine("SVC MENU PROBE (ascii-anchor; svcmenus="+svcMenus.Count+")");
    sb.AppendLine("=== 状态映射矩阵（starting,running,busy → 圆点色 / 状态文本 / 启停可用）===");
    foreach(var t in new (bool s,bool r,bool b)[]{ (false,false,false),(true,false,false),(false,true,false),(false,false,true) }){
      var en=SvcEnable(t.s,t.r,t.b);
      sb.AppendLine(string.Format("starting={0,-5} running={1,-5} busy={2,-5} → 圆点={3,-7} 状态={4,-14} 启动={5,-5} 重启={6,-5} 停止={7}",
        t.s,t.r,t.b,SvcDot(t.s,t.r,t.b),SvcState(t.s,t.r,t.b),en.start,en.restart,en.stop));
    }
    sb.AppendLine();
    sb.AppendLine("=== 真实端口探测（只读：Adopt + /health + /props；不启停任何服务）===");
    foreach(var m in svcMenus) Adopt(m.svc);                       // 复刻 Tick：接管已在端口上跑的 llama（外部/上一实例启动的）
    foreach(var m in svcMenus){
      var s=m.svc; bool up=HealthUp(s.Port); s.PortBusy=up&&!s.Running;
      if(up){ var pr=ProbeLlamaProps(s.Port); if(pr.Item1>0) s.runCtx=pr.Item1; if(pr.Item2.HasValue) s.runVision=pr.Item2.Value?1:0; }
      sb.AppendLine("  "+s.Name+"  端口 "+s.Port+"  health="+(up?"UP":"down")+"  进程="+(s.Running?"已接管":"未接管")+"  实测ctx="+(s.runCtx>0?(s.runCtx/1024)+"K":"-")+"  视觉="+(s.RunVision.HasValue?(s.RunVision.Value?"支持":"不支持"):"-"));
    }
    sb.AppendLine();
    sb.AppendLine("=== 每服务二级菜单（真实配置渲染）===");
    foreach(var m in svcMenus){
      var s=m.svc;
      m.sig=""; RefreshSvcMenu(m);   // 清签名强制重算
      bool busy=s.PortBusy&&!s.Running&&!s.Starting;
      sb.AppendLine("["+s.Name+" ("+s.Port+")]");
      sb.AppendLine("    一级项文本 = "+m.root.Text+"   圆点="+SvcDot(s.Starting,s.Running,busy));
      sb.AppendLine("    状态行 = "+m.status.Text);
      sb.AppendLine("    启用 = 启动:"+m.start.Enabled+" 重启:"+m.restart.Enabled+" 停止:"+m.stop.Enabled);
      sb.AppendLine("    "+m.envCuda.Text.Trim());
      sb.AppendLine("    "+m.envAllreduce.Text.Trim());
      sb.AppendLine("    "+m.cppHeader.ToolTipText.Replace("\r\n"," "));
      sb.AppendLine("    （运行时配置行；注意 ToolStripItem.Visible 在父下拉从未显示时恒读 false，故此处按原始文本打印）");
      foreach(var it in m.cfgLines) sb.AppendLine("    "+it.Text.Trim());
    }
    sb.AppendLine();
    sb.AppendLine("=== 一级菜单是否残留每模型旧入口（应为 0）===");
    int old=0;
    foreach(ToolStripItem it in menu.Items){ if(it.Text.StartsWith("启动 ")||it.Text.StartsWith("打开 DSH 会话")) old++; }
    sb.AppendLine("旧项数 = "+old+(old==0?"  (OK)":"  (FAIL: 一级仍单列每模型启动/会话入口)"));
    sb.AppendLine("OLD-TOPLEVEL-ENTRIES = "+old+(old==0?"  (OK)":"  (FAIL)"));
    sb.AppendLine();
    sb.AppendLine("=== 「点击后不收起菜单」注册项（应为 3/服务）===");
    sb.AppendLine("svcMenus="+svcMenus.Count+"  每服务固定子项数="+(svcMenus.Count>0?svcMenus[0].root.DropDownItems.Count:0));
    return sb.ToString();
  }
}
