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
              if(SvcProbe.HealthUp(t.svc.Port)){ long ms=t.svc.startMs; t.svc.Starting=false; t.svc.startMs=0; t.svc.runCtx=0; t.svc.runVision=-1;
                var pr=ProbeLlamaProps(t.svc.Port); if(pr.Item1>0) t.svc.runCtx=pr.Item1; if(pr.Item2.HasValue) t.svc.runVision=pr.Item2.Value?1:0;
                long used=(ms>0?Math.Max(0,(Environment.TickCount-ms)/1000):0);
                logForm.Append("["+t.svc.Name+"] 已就绪（端口 "+t.svc.Port+"，耗时 "+used+"s）\r\n");
                // 加载刚把整个 gguf 读了一遍 ⇒ 此刻工作集达峰，是回收收益最大的一刻。
                // 只**打标**不当场回收：回收含 120ms 阻塞等待 + 一次 /slots 探测，放在这个还要继续探别的服务的
                // 后台任务里做，会把整轮探活拖长（探活是 2s 节拍）；交 TrimTick 去执行。
                t.svc.lastTrimMs=0; t.svc.pendingTrimOnReady=true;
                // L3 启动预热同样只打标：它要起一个 python 子进程（秒级），绝不能挂在探活线程上。
                // 与 trim 的差别：trim 是清页（快），预热是「起进程 + 打 REST」（慢）⇒ 更需要离开这里。
                t.svc.pendingWarmupOnReady=true;
                try{ perf?.OnReady(t.svc.Spec, ms, pr.Item1, ProbeBuildInfo(t.svc.Port)); }catch{} }
            } else if(t.running){
              var pr=ProbeLlamaProps(t.svc.Port); if(pr.Item1>0) t.svc.runCtx=pr.Item1; if(pr.Item2.HasValue) t.svc.runVision=pr.Item2.Value?1:0;
            } else {
              // 未运行：端口是否被外部/上一实例的 llama 占用（占用 → 状态行标注、允许「停止模型」兜底回收，并尝试接管）
              bool busy=SvcProbe.HealthUp(t.svc.Port);
              if(busy){ t.svc.PortBusy=true; if(!t.svc.Running) Adopt(t.svc); }
              else t.svc.PortBusy=false;
            }
          }
        });
      }
    }
    RefreshSvcMenus();   // 二级菜单：状态圆点 / 运行状态行 / 运行时配置（签名未变则跳过，零重绘）
    TrimTick();          // 内存回收节拍：就绪后一次 + 空闲自动（重活丢后台线程，绝不拖住 UI）
    WarmupTick();        // 启动预热节拍：就绪后一次（起 python 子进程，秒级 ⇒ 必须丢后台）
    gpuTick++; 
    double cpu=SysInfo.CpuPercent();
    var mem=SysInfo.Mem();
    if(gpuTick%5==0){
      // nvidia-smi (GpuInfo.Discover) + WMI CPU温度 采样较慢，放到后台线程，避免阻塞 UI 线程（右键/菜单即时响应）
      // KV cache 用量（/props + /slots 两个本地请求）一并放这里：同样不许占 UI 线程。
      Task.Run(()=>{
        try{
          var gl=GpuInfo.Discover(); gpus=gl; gpuTip=string.Join("\n",gl.Select(g=>g.Line));
          double t=HwInfo.CpuTemp(); if(double.IsNaN(t)) t=SysInfo.CpuTemp(); cpuTemp=double.IsNaN(t)?"":t.ToString("0")+"°C"; // CPU 温度：HWiNFO(真) 优先 → ACPI 变化值兜底 → 无真值不显示
          foreach(var s in services){
            if(!s.Running){ s.kvUsed=0; s.kvTotal=0; continue; }   // 停掉的服务不留上一轮读数
            var kv=SvcProbe.KvUsage(s.Port); s.kvUsed=kv.used; s.kvTotal=kv.total;
          }
        }catch{}
      });
    }
    // —— 悬浮 tooltip（icon.Text）组装 ——
    // 2026-09-13 加两项：KV cache 用量/总量、内存缓存（--cache-ram）用量/上限（合成见 SvcLines.SvcTipLine）。
    // 🔴 必须经 ClampTip：NotifyIcon.Text 上限 127 字符，超限 .NET 抛异常而被下面 try/catch 吞掉 ⇒ tooltip 静默不刷新。
    var tipLines=new List<string>();
    foreach(var s in services) if(s.Running) tipLines.Add(SvcLines.SvcTipLine(Short(s),s.Port,s.State,s.RamGb,s.kvUsed,s.kvTotal,s.cacheRamUsedMib,s.cacheRamLimitMib));
    if(tipLines.Count==0) tipLines.Add("（无运行中的模型）");
    if(gpuTip.Length>0) tipLines.AddRange(gpuTip.Split('\n')); else tipLines.Add(gpus.Count>0?"GPU: 查询中":"GPU: 无");
    tipLines.Add("CPU: "+cpu.ToString("0")+"%"+(cpuTemp.Length>0?" "+cpuTemp:"")+" 内存: "+mem.usedPct.ToString("0")+"%");
    string tip=SvcLines.ClampTip(tipLines);
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
  // 图标自愈（2026-09-22）：被「救活信号」唤醒时重挂通知区图标（delete+add，同 TaskbarWatcher 的重注册姿势）。
  //   ⚠️ Ack 必须**在 UI 线程回调里**发出：UI 线程僵死 ⇒ Tick 永不执行 ⇒ 无 Ack ⇒ 后到实例据此判死并接管。
  //   与 ExitFromSignal 同构（都经 UiDispatcher.Post → 隐藏锚点控件 BeginInvoke），不引入新的跨线程姿势。
  public void ResurrectIcon(Action ack){
    try{
      Ui(()=>{
        try{ icon.Visible=false; icon.Visible=true; }catch{}
        try{ logForm.Append(">>> 图标自愈：收到外部救活信号，已重挂通知区图标\r\n"); }catch{}
        if(ack!=null){ try{ ack(); }catch{} }
      });
    }catch{}
  }
  // 重启托盘：新实例先起（它会 Adopt 端口上已在跑的模型），旧实例**保留模型**退出 → 模型不再被重启托盘打断
  void RestartTray(){ try{ Process.Start(new ProcessStartInfo(Application.ExecutablePath){ UseShellExecute=true, WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory }); }catch{} ExitApp(false); }

  // 隐藏自检模式：--selftest-memtrim（内存回收：策略矩阵 + 菜单接线 + 台账可写；加 live 才真回收）
  // L1（可自动跑）：不弹菜单、不碰鼠标、默认不动真实进程。
  // L2（live）：对运行中的模型真的回收一次 —— 这是"判据在真环境成立"的那一半，需显式索要。
  public string MemTrimProbe(bool live){
    var sb=new System.Text.StringBuilder();
    sb.AppendLine("MEM TRIM PROBE (ascii-anchor; selftest-memtrim"+(live?" live":"")+")");
    sb.AppendLine("impl: src/QwenTray.Core/MemTrim.cs + WinMem.cs | unit: tests/MemTrimTests.cs");
    int pass=0,fail=0;
    void Check(string name,bool ok,string detail=""){
      if(ok)pass++; else fail++;
      sb.AppendLine((ok?"[PASS] ":"[FAIL] ")+name+(detail.Length>0?("   | "+detail):""));
    }
    try{
      sb.AppendLine();
      sb.AppendLine("=== ① 策略矩阵（纯函数；与 MemTrimTests 同一组判据）===");
      Check("手动路径恒放行（即便有请求在跑）", MemTrim.Allow(TrimKind.Manual,false,false,true,0,60));
      Check("负向：空闲关 ⇒ 不放行", !MemTrim.AllowIdle(false,false,-1,1800));
      Check("负向：有请求在处理 ⇒ 空闲自动**一律**不放行（防缺页抖动）",
            !MemTrim.AllowIdle(true, anyProcessing:true, sinceLastTrimMs:-1, intervalSec:60)
            && !MemTrim.AllowIdle(true, anyProcessing:true, sinceLastTrimMs:99999999, intervalSec:60));
      Check("空闲到达间隔 ⇒ 放行", MemTrim.AllowIdle(true,false,1800_000,1800));
      Check("负向：间隔差 1ms ⇒ 不放行", !MemTrim.AllowIdle(true,false,1800_000-1,1800));
      Check("就绪路径跟随开关", MemTrim.AllowOnReady(true,false) && !MemTrim.AllowOnReady(false,false));
      Check("间隔夹取：0 秒被夹到 60 秒", MemTrim.ClampInterval(0)==MemTrim.MinIdleIntervalSec);
      Check("间隔夹取：超大值被夹到 24h", MemTrim.ClampInterval(int.MaxValue)==MemTrim.MaxIdleIntervalSec);

      sb.AppendLine();
      sb.AppendLine("=== ② 菜单接线（结构断言；不点、不弹）===");
      Check("「内存与缓存」下拉已建", trimMenu!=null && trimMenu.DropDownItems.Count>0);
      Check("手动项存在", trimNowItem!=null);
      Check("就绪后一次项存在", trimOnReadyItem!=null);
      Check("空闲自动项存在", trimIdleItem!=null);
      Check("回收间隔档位数 == MemTrim.IdleIntervalOptions.Length",
            trimIntervalItems.Count==MemTrim.IdleIntervalOptions.Length,
            "实得="+trimIntervalItems.Count+" 期望="+MemTrim.IdleIntervalOptions.Length);
      Check("硬盘 KV 缓存开关存在", slotSaveItem!=null);
      // 三个开关项必须都落在"点了不收起菜单"的①类里，否则用户调参要反复展开菜单
      int notState=0;
      foreach(var it in trimMenu!.DropDownItems){ if(it is not ToolStripMenuItem mi) continue; if(!_keep.IsState(trimMenu.DropDown,mi)) notState++; }
      Check("下拉内全部项都是①类（点了不收起菜单）", notState==0, "违规="+notState);
      Check("硬盘缓存开关已登记为①类", slotSaveItem!=null && _keep.IsState(trimMenu.DropDown,slotSaveItem));

      sb.AppendLine();
      sb.AppendLine("=== ③ 当前生效策略（读的是内存态，不是常量）===");
      sb.AppendLine("  "+MemTrim.PolicyText(trimOnReady,trimIdle,trimIntervalSec));
      sb.AppendLine("  回收间隔档位 = "+MemTrim.IntervalLabel(trimIntervalSec)+"（"+trimIntervalSec+"s）");
      sb.AppendLine("  硬盘 KV 缓存 = "+(slotSaveOn?"开":"关")+"  路径 = "+(slotSaveOn?slotSavePath:"（未启用）"));
      Check("生效间隔已过夹取（不会被 cfg 里的非法值绕过）", trimIntervalSec==MemTrim.ClampInterval(trimIntervalSec),
            "值="+trimIntervalSec);
      Check("生效间隔在菜单档位内", Array.IndexOf(MemTrim.IdleIntervalOptions,trimIntervalSec)>=0,
            "值="+trimIntervalSec);

      sb.AppendLine();
      sb.AppendLine("=== ④ 台账文件可落盘（不污染真台账：写临时文件）===");
      string logPath=MemTrim.DefaultLogPath, dir=Path.GetDirectoryName(logPath) ?? "";
      sb.AppendLine("  台账 = "+logPath);
      Check("台账父目录非空", dir.Length>0);
      bool writable=false;
      try{ if(dir.Length>0){ Directory.CreateDirectory(dir); string probe=Path.Combine(dir,"_probe.tmp"); File.WriteAllText(probe,"x"); File.Delete(probe); writable=true; } }catch{}
      Check("台账目录可写", writable);

      sb.AppendLine();
      sb.AppendLine("=== ⑤ 运行中的服务（只读探测）===");
      int runN=0;
      foreach(var s in services){
        bool up=SvcProbe.HealthUp(s.Port); int pid=WinMem.PidByPort(s.Port);
        if(up&&pid>0) runN++;
        sb.AppendLine("  "+s.Name+" ("+s.Port+")  health="+(up?"UP":"down")+"  pid="+(pid>0?pid.ToString():"-")
                      +"  内存="+s.RamGb+"  busy="+(up?SvcProbe.Busy(s.Port).ToString():"-")
                      +(s.lastTrimLine.Length>0?"  上次回收: "+s.lastTrimLine:""));
      }
      sb.AppendLine("  运行中 = "+runN+" 个");

      sb.AppendLine();
      sb.AppendLine("=== ⑥ 实测回收（"+ (live?"已索要 live":"默认跳过；加 live 参数才执行") +"）===");
      if(live){
        if(runN==0){ sb.AppendLine("  无运行中的模型 —— 跳过"); }
        else{
          long before=0; foreach(var s in services){ if(s.Running) before+=WinMem.WorkingSetBytes(WinMem.PidByPort(s.Port)); }
          int n=TrimAll(TrimKind.Manual);
          long after=0; foreach(var s in services){ if(s.Running) after+=WinMem.WorkingSetBytes(WinMem.PidByPort(s.Port)); }
          sb.AppendLine("  回收服务数 = "+n+"   合计工作集 "+MemTrim.Bytes(before)+" → "+MemTrim.Bytes(after));
          Check("实测：至少一个服务被成功回收", n>0);
          Check("实测：合计工作集未上升", after<=before, "前="+before+" 后="+after);
        }
      } else {
        sb.AppendLine("  （跳过。要真回收请跑：DSHTray.exe --selftest-memtrim live）");
      }
      sb.AppendLine();
      sb.AppendLine("  也可以不开托盘直接回收：DSHTray.exe --trim [port]  →  selftest-trim.txt");
    }catch(Exception ex){ fail++; sb.AppendLine("[FAIL] PROBE EX: "+ex.Message+"\r\n"+ex.StackTrace); }
    sb.AppendLine();
    sb.AppendLine("SUMMARY pass="+pass+" fail="+fail);
    sb.AppendLine(fail==0?"RESULT PASS":"RESULT FAIL");
    return sb.ToString();
  }

  // 隐藏自检模式：--dump-menu 打印当前菜单结构（供开发验证，不进正式 UI）——
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
      var en=SvcStatus.Enable(t.s,t.r,t.b);
      sb.AppendLine(string.Format("starting={0,-5} running={1,-5} busy={2,-5} → 圆点={3,-7} 状态={4,-14} 启动={5,-5} 重启={6,-5} 停止={7}",
        t.s,t.r,t.b,SvcStatus.Dot(t.s,t.r,t.b),SvcStatus.State(t.s,t.r,t.b),en.start,en.restart,en.stop));
    }
    sb.AppendLine();
    sb.AppendLine("=== 真实端口探测（只读：Adopt + /health + /props；不启停任何服务）===");
    foreach(var m in svcMenus) Adopt(m.svc);                       // 复刻 Tick：接管已在端口上跑的 llama（外部/上一实例启动的）
    foreach(var m in svcMenus){
      var s=m.svc; bool up=SvcProbe.HealthUp(s.Port); s.PortBusy=up&&!s.Running;
      if(up){ var pr=ProbeLlamaProps(s.Port); if(pr.Item1>0) s.runCtx=pr.Item1; if(pr.Item2.HasValue) s.runVision=pr.Item2.Value?1:0; }
      sb.AppendLine("  "+s.Name+"  端口 "+s.Port+"  health="+(up?"UP":"down")+"  进程="+(s.Running?"已接管":"未接管")+"  实测ctx="+(s.runCtx>0?(s.runCtx/1024)+"K":"-")+"  视觉="+(s.RunVision.HasValue?(s.RunVision.Value?"支持":"不支持"):"-"));
    }
    sb.AppendLine();
    sb.AppendLine("=== 一级模型行 + 「当前模型」组（2026-09-26：二级菜单整体取消后的一级形态）===");
    // 逐个把 selSvc 指到每个模型上，把这一组在**那个模型**下的真实文本打出来
    // （组内的启停/会话/配置行全部以 selSvc 为唯一事实源，所以遍历一遍就等于把四个模型各渲染一次）。
    var saveSel=selSvc; bool saveSup=PersistSuppressed; PersistSuppressed=true;
    int selOk=0, selBad=0;   // 选中互斥守卫：选中 s 后应**恰好一个**勾且勾在 s 上（2026-09-26 回归守卫：
    foreach(var m in svcMenus){  //  签名门控曾把 Checked 拦死 → 勾残留 + "要双击"，见 RefreshSvcMenu 注释）
      var s=m.svc;
      SelectSvc(s);   // 走**真实选择链路**（含 MaterializeConfig 空位补齐）——探针期 PersistSuppressed 拦落盘
      try{ RefreshCurGroup(); }catch(Exception ex){ sb.AppendLine("    EX: "+ex.Message); }
      int chk=0; foreach(var mm in svcMenus) if(mm.root.Checked) chk++;
      if(chk==1 && m.root.Checked) selOk++; else selBad++;
      bool busy=s.PortBusy&&!s.Running&&!s.Starting;
      sb.AppendLine("["+s.Name+" ("+s.Port+")]");
      sb.AppendLine("    一级项文本 = "+m.root.Text+"  圆点="+SvcStatus.Dot(s.Starting,s.Running,busy)+" Checked="+m.root.Checked);
      sb.AppendLine("    "+curHeader?.Text);
      sb.AppendLine("    状态行 = "+curStatus?.Text);
      sb.AppendLine("    启用 = 启动:"+curStart?.Enabled+" 重启:"+curRestart?.Enabled+" 停止:"+curStop?.Enabled);
      sb.AppendLine("    运行时配置行["+(curCfgLine?.Visible==true?"显示":"隐藏")+"] = "+curCfgLine?.Text);
      // 明细在 ToolTip 里：SvcLines.CfgLines 每行都以 4 空格开头（生效环境/完整命令行不带缩进，据此区分）
      try{
        string tip=curCfgLine?.ToolTipText??"";
        foreach(var ln in tip.Split('\n')){
          var t=ln.TrimEnd('\r');
          if(t.StartsWith("    ")&&t.Trim().Length>0) sb.AppendLine("      "+t.Trim());
        }
      }catch{}
    }
    selSvc=saveSel; PersistSuppressed=saveSup;
    foreach(var mm in svcMenus) mm.root.Checked=ReferenceEquals(mm.svc,selSvc);   // 还原选中显示
    try{ RefreshCurGroup(); }catch{}
    sb.AppendLine();
    sb.AppendLine("=== 选中互斥（点击即换勾、旧勾不残留；2026-09-26 回归守卫）===");
    sb.AppendLine("逐个选中后 恰好一勾且勾在当前模型 = "+selOk+"/"+svcMenus.Count+(selBad==0?"  (OK)":"  (FAIL: 选中后勾数不对或勾错位)"));
    sb.AppendLine("SVC-SEL-EXCLUSIVE = "+(selBad==0&&svcMenus.Count>0?"OK":"FAIL("+selBad+")"));
    sb.AppendLine();
    sb.AppendLine("=== 一级菜单是否残留每模型旧入口（应为 0）===");
    int old=0;
    // 「启动模型 / 重启模型 / 停止模型」是**新的**「当前模型」组三条（作用于选中模型），不算旧入口 ——
    // 只认旧形态："每模型各自的 启动 X / 打开 DSH 会话 X" 那种带了模型名的特指入口。
    // 判据改成**按对象排除**而不是按文本排除：文本会随内置渲染开关加后缀，按文本排永远排不干净。
    var curGroup=new HashSet<ToolStripItem>();
    foreach(var x in new ToolStripItem?[]{curStart,curRestart,curStop,curOpenDsh,curOpenBuiltin})
      if(x!=null) curGroup.Add(x);
    foreach(ToolStripItem it in menu.Items){
      if(curGroup.Contains(it)) continue;
      if(it.Text.StartsWith("启动 ")||it.Text.StartsWith("打开 DSH 会话")) old++;
    }
    sb.AppendLine("旧项数 = "+old+(old==0?"  (OK)":"  (FAIL: 一级仍单列每模型启动/会话入口)"));
    sb.AppendLine("OLD-TOPLEVEL-ENTRIES = "+old+(old==0?"  (OK)":"  (FAIL)"));
    sb.AppendLine();
    sb.AppendLine("=== 一级模型行是否为叶子项（二级菜单已取消 ⇒ 各 0 项）===");
    int leaf=0; foreach(var m in svcMenus) if(m.root.DropDownItems.Count==0) leaf++;
    sb.AppendLine("svcMenus="+svcMenus.Count+"  叶子项="+leaf+(leaf==svcMenus.Count?"  (OK)":"  (FAIL: 一级模型行又长出了二级菜单)"));
    sb.AppendLine("SVC-MENU-LEAF-ITEMS = "+leaf+(leaf==svcMenus.Count?"  (OK)":"  (FAIL)"));
    return sb.ToString();
  }
  // 隐藏自检模式：--selftest-menukeep 验证「点了关不关」统一裁决器（MenuKeepOpen.cs）。
  // L1（本探针，agent 可跑）：① 纯函数三态 + ② 真实菜单树逐项分类 + ③ 挂载计数与结构契约 + ④ 隔离菜单行为（含 P1 幂等负向控制 A19：连续 Hook 两次订阅数不增）。
  // ⚠️ 真鼠标（SendInput）路径**不在这里**：CLI 进程无消息泵/无真实通知区图标 ⇒ 会给出假 FAIL（gotchas.md §E）。
  //    真鼠标结论由 L2 常驻探针（DSHTRAY_CHECKCLICK=1）在用户真实桌面会话里给出，本探针不冒充。
  public string MenuKeepProbe(){
    var sb=new System.Text.StringBuilder();
    sb.AppendLine("MENU KEEP PROBE (ascii-anchor; selftest-menukeep)");
    sb.AppendLine("discipline: ~/.workbuddy/skills/tray-menu-keepopen | impl: MenuKeepOpen.cs");
    int pass=0,fail=0;
    void Check(string name,bool ok,string detail=""){
      if(ok)pass++; else fail++;
      sb.AppendLine((ok?"[PASS] ":"[FAIL] ")+name+(detail.Length>0?("   | "+detail):""));
    }
    // 树内按精确文本查找（只读；不改结构）
    ToolStripMenuItem? Find(ToolStripDropDown owner,string text){
      foreach(ToolStripItem it in owner.Items) if(it is ToolStripMenuItem mi && mi.Text==text) return mi;
      return null;
    }
    // 逐项分类核对：IsState 必须等于期望值
    void Classify(string label,ToolStripDropDown owner,ToolStripMenuItem? mi,bool expectState){
      if(mi==null){ fail++; sb.AppendLine("[FAIL] "+label+"：菜单项未找到（结构被改？）"); return; }
      bool got=_keep.IsState(owner,mi);
      string on=ReferenceEquals(owner,menu)?"root":(dshMenu!=null&&ReferenceEquals(owner,dshMenu.DropDown))?"dshMenu":"svc";
      Check(label+"  IsState=="+expectState,got==expectState,"owner="+on+" text="+mi.Text);
    }
    try{
      sb.AppendLine();
      sb.AppendLine("=== ① 纯函数裁决（MenuKeepOpen.Should*）===");
      Check("P1 ShouldCancel(ItemClicked)==true（先拦：菜单先别关）",MenuKeepOpen.ShouldCancel(ToolStripDropDownCloseReason.ItemClicked));
      Check("P2 负向 ShouldCancel(AppFocusChange)==false（单独拦失焦=钉子）",!MenuKeepOpen.ShouldCancel(ToolStripDropDownCloseReason.AppFocusChange));
      Check("P3 负向 ShouldCancel(Keyboard)==false（Esc 照关）",!MenuKeepOpen.ShouldCancel(ToolStripDropDownCloseReason.Keyboard));
      Check("P4 ShouldCancelFocusChange(0ms)==true（新鲜印记要拦：真鼠标根菜单先关的那一下）",MenuKeepOpen.ShouldCancelFocusChange(0));
      Check("P5 负向 ShouldCancelFocusChange(99999ms)==false（陈旧印记不拦：点别处/Alt+Tab）",!MenuKeepOpen.ShouldCancelFocusChange(99999));
      Check("P6 ShouldCloseOnClick(false)==true（未声明项 fail-safe 归③ ⇒ 必须补关）",MenuKeepOpen.ShouldCloseOnClick(false));
      Check("P7 负向 ShouldCloseOnClick(true)==false（①②类不补关）",!MenuKeepOpen.ShouldCloseOnClick(true));

      // ── P13–P15 落点护栏（MenuPlace，2026-09-12）──────────────────────────────
      //    接线型修复：挂事件即隐身 ⇒ 漏挂/被重构掉会**静默退回缺陷**，所以必须断言"真挂上了"。
      Check("P13 落点护栏：根菜单挂上了", MenuPlace.IsGuarded(menu));
      Check("P14 落点护栏：DSH 二级菜单挂上了", dshMenu!=null && MenuPlace.IsGuarded(dshMenu.DropDown));
      Check("P15 落点护栏：整棵树都挂上了（根 + 全部子菜单）", MenuPlace.AllGuarded(menu));

      // ── P8–P12 ②的**语义闸门**（2026-09-12「点空白处菜单不消失」的修复）────────────
      //    旧写法只看"500ms 内有没有按过"，而印记挂在 MouseDown 上 ⇒ 右键按在项上也会刷新它，
      //    且不区分"刚碰过本菜单项"与"点的是菜单外" ⇒ 点菜单外的空白会被误拦成钉子。
      //    现在 ② 必须**同时**有「左键按下印记」。
      Check("P8 ②闸门：AppFocusChange **且无按下印记** ⇒ 不拦（点菜单外空白靠这条）",
            !MenuKeepOpen.ShouldCancelClose(ToolStripDropDownCloseReason.AppFocusChange,false,1));
      Check("P9 ②闸门：AppFocusChange 且有**新鲜**印记 ⇒ 拦（点菜单项时根菜单要留住，否则留孤儿子菜单）",
            MenuKeepOpen.ShouldCancelClose(ToolStripDropDownCloseReason.AppFocusChange,true,1));
      Check("P10 ②闸门：有印记但**陈旧**（99999ms）⇒ 不拦（切窗口/Alt+Tab 照关）",
            !MenuKeepOpen.ShouldCancelClose(ToolStripDropDownCloseReason.AppFocusChange,true,99999));
      Check("P11 ①不变：ItemClicked **一律**拦（与印记无关 —— 程序化路径没有 MouseDown）",
            MenuKeepOpen.ShouldCancelClose(ToolStripDropDownCloseReason.ItemClicked,false,99999));
      Check("P12 负向：AppClicked（点应用其它处）⇒ 不拦",
            !MenuKeepOpen.ShouldCancelClose(ToolStripDropDownCloseReason.AppClicked,true,1));

      sb.AppendLine();
      sb.AppendLine("=== ② 真实菜单树分类（IsState；A7 正向 / A8+A9 负向）===");
      int posN=0; void Pos(string label,ToolStripDropDown owner,ToolStripMenuItem? mi){ Classify(label,owner,mi,true); if(mi!=null)posN++; }
      int negN=0; void Neg(string label,ToolStripDropDown owner,ToolStripMenuItem? mi){ Classify(label,owner,mi,false); if(mi!=null)negN++; }
      var dshDd=dshMenu!.DropDown;
      // A7 正向：①②类（用真实对象断言，不是清单快照）
      Pos("[①] 使用托盘内置渲染打开",dshDd,builtinRenderItem);
      Pos("[①] 安全模式启动",dshDd,safeModeItem);
      Pos("[①] 模型监听 0.0.0.0",menu,bindItem);
      Pos("[①] 硬盘 KV 缓存（--slot-save-path）",trimMenu!.DropDown,slotSaveItem!);
      Pos("[①] 模型就绪后预热最近 KV",trimMenu!.DropDown,warmupOnReadyItem!);
      Pos("[①] 开机自启动",menu,autoStartItem);
      Pos("[②] 启动 DSH",dshDd,dshStartItem);
      Pos("[②] 重启 DSH",dshDd,dshRestartItem);
      Pos("[②] 停止 DSH",dshDd,dshStopItem);
      Pos("[②] 重启全部",menu,Find(menu,"重启全部"));
      // 2026-09-26：每模型的二级菜单整体取消 ⇒ 启停不再按"每模型 3 项"递增，而是全局一组
      // 「当前模型」3 项（作用于当前选中那个模型）；每个模型名一级项自身是①类（点了=选中，菜单要留住）。
      Pos("[②] 启动模型（当前模型组）",menu,curStart);
      Pos("[②] 重启模型（当前模型组）",menu,curRestart);
      Pos("[②] 停止模型（当前模型组）",menu,curStop);
      foreach(var m in svcMenus) Pos("[①] "+m.svc.Name+" 模型名（点击选中，互斥单选）",m.root.DropDown,m.root);
      // A9 正向：路径 A 的 7 个参数面板下拉里每一个 ToolStripMenuItem 都应为 State（含禁用项）
      var panels=new (string,ToolStripDropDown)[]{("推理参数组",pm!.DropDown),("GPU",gpuMenu!.DropDown),("上下文",ctxMenu!.DropDown),("切分模式",splitMenu!.DropDown),("KV 缓存",kvMenu!.DropDown),("缓存内存",cacheRamMenu!.DropDown),("MTP",mtpMenu!.DropDown),("内存与缓存",trimMenu!.DropDown)};
      foreach(var (nm,dd) in panels){
        int tot=0,bad=0;
        foreach(ToolStripItem it in dd.Items){ if(it is not ToolStripMenuItem mi) continue; tot++; if(!_keep.IsState(dd,mi)) bad++; }
        Check("路径A "+nm+" 下拉内 "+tot+" 项全部 IsState==true",tot>0&&bad==0,"bad="+bad);
      }
      // A8 负向：③类项（不声明 ⇒ fail-safe 归③；漏判就是"点完赖着不走"）
      Neg("[③] 停止全部",menu,Find(menu,"停止全部"));
      Neg("[③] 打开官方会话 chat",menu,itemOpenChat);
      Neg("[③] 查看日志",menu,Find(menu,"查看日志"));
      Neg("[③] 模型性能日志",menu,Find(menu,"模型性能日志"));
      Neg("[③] 打开配置文件",menu,Find(menu,"打开配置文件"));
      Neg("[③] 重启托盘",menu,Find(menu,"重启托盘"));
      Neg("[③] 退出",menu,Find(menu,"退出"));
      Neg("[③] 退出（同时停止模型服务）",menu,Find(menu,"退出（同时停止模型服务）"));
      Neg("[③] 查看 DSH 日志",dshDd,Find(dshDd,"查看 DSH 日志"));
      Neg("[③] 检查/清除孤儿启动锁",dshDd,Find(dshDd,"检查/清除孤儿启动锁（node_modules.lock）"));
      Neg("[③] 插件管理…",dshDd,Find(dshDd,"插件管理…"));
      Neg("[③] 打开 DSH 程序目录",dshDd,Find(dshDd,"打开 DSH 程序目录"));
      Neg("[③] 打开 .dsh 目录",dshDd,Find(dshDd,"打开 .dsh 目录"));
      // 2026-09-26：会话入口从"每模型二级菜单"上移为「当前模型」组里的两条（作用于当前选中模型）。
      // 「启动 DSH 会话」文案仍随内置渲染开关加后缀（标签必须能预测行为）；「启动内置对话」恒走内置渲染。
      Neg("[③] 启动 DSH 会话（当前模型组，文案随「内置渲染」开关加后缀）",menu,curOpenDsh);
      Neg("[③] 启动内置对话（当前模型组，恒走内置渲染）",menu,curOpenBuiltin);
      sb.AppendLine("分类核对：正向 "+posN+" 项 / 负向 "+negN+" 项");

      sb.AppendLine();
      sb.AppendLine("=== ③ 挂载计数与结构契约（A10/A11/A12）===");
      // 2026-09-13 起：+1 下拉（内存与缓存）、+1 显式①类项（其内的「硬盘 KV 缓存」开关）。
      // expState 保持 9+3N：同日加的启动预热三件套（就绪后预热开关 / 立即预热 / 预热状态行）
      // **不进 _keepItems** —— 它们整个 trimMenu 已在 DeclareWholeState 里（路径 A），
      // 再显式点名既多余、又会把这个计数契约搅浑（当时就是这么误报 A11 的）。
      // 预热开关到底算不算①类，由 --selftest-warmup 用 _keep.IsState() 正面断言（路径 A 感知），不靠数数。
      // 2026-09-26 结构变更后的计数契约（改前是 10+N / 9+3N）：
      //   AttachedDropdowns：根菜单 + DSH 二级 + 8 个参数面板下拉 = 10，**不再 +svcMenus.Count**
      //     —— 每个模型名的二级菜单已取消，一级模型行是个叶子项。
      //   StateItemCount：全局 12（9 + 「当前模型」组那 3 项启停）+ 每模型名 1（①类根项）。
      int expAttach=10, expState=12+svcMenus.Count;
      Check("A10 AttachedDropdowns == 10+svcMenus.Count",_keep.AttachedDropdowns==expAttach,"实得="+_keep.AttachedDropdowns+" 期望="+expAttach);
      Check("A11 StateItemCount == 9+3*svcMenus.Count",_keep.StateItemCount==expState,"实得="+_keep.StateItemCount+" 期望="+expState);
      Check("WiredItems>0（最近一次 Hook 真的挂了项）",_keep.WiredItems>0,"WiredItems="+_keep.WiredItems);
      // 2026-09-26：这条契约从"每模型二级菜单恰好 20 项"改成"一级模型行是叶子项"，
      // 判据同源 —— 都是钉住"每模型二级菜单不许回来"。数项数会随新增项频繁误报，数"有没有子菜单"不会。
      int leafCnt=0; foreach(var m in svcMenus) if(m.root.DropDownItems.Count==0) leafCnt++;
      Check("A12 一级模型行=叶子项（每模型二级菜单已取消；root.DropDownItems.Count==0）",svcMenus.Count>0&&leafCnt==svcMenus.Count,
        "svcMenus="+svcMenus.Count+" 叶子项="+leafCnt);

      sb.AppendLine();
      sb.AppendLine("=== ④ 隔离菜单行为（PerformClick 程序化路径；不碰真菜单，防误触「退出」）===");
      (bool openAfterState,bool openAfterAction,int actDone,bool autoCloseOn) RunIsolated(bool hook){
        int done=0; var iso=new ContextMenuStrip();
        var st=new ToolStripMenuItem("假状态项"){CheckOnClick=true};
        var ac=new ToolStripMenuItem("假动作项"); ac.Click+=(s,e)=>{done++;};
        iso.Items.Add(st); iso.Items.Add(ac);
        if(hook) _keep.Hook(iso);
        iso.Show(new Point(60,60)); Application.DoEvents();
        st.PerformClick(); Application.DoEvents();
        bool oState=iso.Visible;
        ac.PerformClick();
        for(int i=0;i<6&&iso.Visible;i++){ Application.DoEvents(); System.Threading.Thread.Sleep(30); }
        bool oAct=iso.Visible; bool autoc=iso.AutoClose;
        try{ iso.Close(); }catch{} Application.DoEvents(); iso.Dispose();
        return (oState,oAct,done,autoc);
      }
      var hooked=RunIsolated(true);
      Check("A14 挂裁决器：①类项 PerformClick ⇒ 根菜单仍 Visible（不关）",hooked.openAfterState,"Visible="+hooked.openAfterState);
      Check("A15 挂裁决器：③类项 PerformClick ⇒ 菜单关闭（拦下后 BeginInvoke 补关）",!hooked.openAfterAction,"Visible="+hooked.openAfterAction);
      Check("A16 ③类项的动作真的被执行（没被「不关菜单」吞掉）",hooked.actDone==1,"actDone="+hooked.actDone);
      Check("A17a AutoClose 未被引入（**真实根 menu** 的 AutoClose 仍为 true；源码级证据：全仓非注释行无可执行 AutoClose 赋值）",menu.AutoClose,"menu.AutoClose="+menu.AutoClose);
      var legacy=RunIsolated(false);
      Check("A13 负向控制：不挂裁决器 ⇒ ①类项 PerformClick 后菜单被关（证明 A14 不是空转）",!legacy.openAfterState,"Visible="+legacy.openAfterState);
      // A23（2026-09-12 副修 · 回归守卫）：**父项（带子菜单）默认不补关** —— 点一级项 = 展开子菜单，菜单留着。
      //   起因：程序化实测父项 PerformClick **会**触发 Click ⇒ 旧实现让它走③类补关 ⇒"点一级项 = 整条菜单关"
      //   （用户实感"点击后消失"）。对照实测：不挂本机制时隔离菜单里父项 PerformClick 后 IsVisible 仍 True。
      //   ⇒ 父项语义是"导航到子菜单"，不该补关；谁把父项的 CloseSoon 加回来，本条当场红。
      //   ⚠️ 与 A15（③类**叶子**仍照关）成对：区别就在"父项 vs 叶子"，这正是本次副修的内容。
      (bool vis,int clicks) RunParent(bool hook){
        int clicks=0; var iso=new ContextMenuStrip();
        var par=new ToolStripMenuItem("假父项"); par.DropDownItems.Add(new ToolStripMenuItem("假子项"));
        par.Click+=(s,e)=>{clicks++;};
        iso.Items.Add(par);
        if(hook) _keep.Hook(iso);
        iso.Show(new Point(160,160)); Application.DoEvents();
        par.PerformClick(); Application.DoEvents();
        bool vis=iso.Visible;
        try{ iso.Close(); }catch{} Application.DoEvents(); iso.Dispose();
        return (vis,clicks);
      }
      var hp=RunParent(true);
      Check("A23 挂裁决器：父项（带子菜单）PerformClick ⇒ 菜单**留着**（点一级项=展开，不整条关）",hp.vis,"Visible="+hp.vis);
      var hpNo=RunParent(false);
      Check("A23b 对照：不挂裁决器时父项 PerformClick 同样留着（⇒ 父项不补关 = 贴合原生语义）",hpNo.vis,"Visible="+hpNo.vis);
      // A25（2026-09-26 · 「点了没效果」回归守卫）：参数项 PerformClick 必须**真改全局值**。
      //   起因：SetCtx(v,ctxLabels[i]) 把 for 的**共享 i** 捕进 lambda ⇒ 点击时循环早已结束，
      //   i==Length ⇒ ctxLabels[i] 越界，异常在**实参求值阶段**抛出 ⇒ 处理体一行不执行 ⇒
      //   勾选/标题纹丝不动（用户实感"点击 32K 没有效果"）。这类缺陷编译期无警告、
      //   结构探针看不出（接线是挂上了的，只是一跑就炸）⇒ 只能真触发点击来抓。
      //   探针期临时置空 selSvc ⇒ PersistParam 首行判空返回，不写 dsh-tray-config.json。
      (bool ok,string detail) RunParamClick(string name, Action<int> click, Func<int> get, int[] opts, int cur){
        var saveSel=selSvc; selSvc=null; bool saveSup=PersistSuppressed; PersistSuppressed=true;   // 探针点击不落盘、不改模型配置
        try{
          int target = cur!=opts[0] ? opts[0] : opts[1];     // 挑一个 ≠ 当前的档
          int iT = Array.IndexOf(opts,target), i0 = Array.IndexOf(opts,cur);
          click(iT); Application.DoEvents();
          bool ch = get()==target;
          click(i0); Application.DoEvents();                 // 点回原档还原
          bool restored = get()==cur;                        // ⚠️ 必须在还原后、拼文案前各取一次
          return (ch && restored, name+" 改值="+(ch?"成功":"失败")+" 还原="+(restored?"成功":"失败")+" 异常=无");
        }catch(Exception ex){ return (false, name+" 点击抛异常: "+ex.GetType().Name+" "+ex.Message); }
        finally{ selSvc=saveSel; PersistSuppressed=saveSup; }
      }
      var rCtx = RunParamClick("上下文", i=>ctxItems[i].PerformClick(), ()=>ctxVal, LaunchArgs.CtxOptions, ctxVal);
      Check("A25 上下文项 PerformClick 真改全局值（lambda 捕获循环变量回归守卫）",rCtx.ok,rCtx.detail);
      var rRam = RunParamClick("缓存内存", i=>cacheRamItems[i].PerformClick(), ()=>cacheRam, LaunchArgs.CacheRamOptions, cacheRam);
      Check("A25b 缓存内存项 PerformClick 真改全局值（同上）",rRam.ok,rRam.detail);
      // A25c（2026-09-26 · 「显示慢一拍」回归守卫）：**有选中模型**时点击参数项，标题必须立即=本次的值。
      //   起因：PersistParam 的 Spec 同步跑在 setter 的 RefreshChecks **之后** ⇒ 面板显示的是上一次
      //   点击的值（用户实感"点 MTP4 没反应、点 MTP3 才跳到 MTP4"）。修复=PersistParam 在同步 Spec
      //   后补刷显示。A25 的全局路径（selSvc=null）测不到这条 —— 显示读的是 Spec，必须走真实模型链路。
      //   探针前后保存/恢复该模型 sc 的 9 项覆盖 + 选中态 + 落盘，净变化=0（真落盘一次、回写一次）。
      if(svcMenus.Count>0){
        var svcT=svcMenus[0].svc; var scT=FindServiceConfig(svcT.Name,svcT.Port);
        if(scT!=null){
          (int? C,int? K,int? R,int? S,int? T,int? M,int? P,bool? B,string G) o=
            (scT.Ctx,scT.KvMode,scT.CacheRam,scT.SplitMode,scT.TsGpu1,scT.MtpLevel,scT.ParamMode,scT.BindAll,scT.GpuSel);
          var saveSel2=selSvc; bool saveSup2=PersistSuppressed; PersistSuppressed=false;   // 真实链路（含落盘；末尾回写原值净变化=0）
          try{
            selSvc=svcT;
            int eff=CurEff().MtpLevel;
            int target2 = eff!=4 ? 4 : 3;                    // 挑一个 ≠ 当前生效档的 MTP 值
            mtpItems[target2].PerformClick(); Application.DoEvents();
            bool val=CurEff().MtpLevel==target2;
            bool disp=mtpMenu!=null && mtpMenu.Text=="MTP: "+MtpLabel(target2);
            Check("A25c 选中模型点 MTP 项：值与面板标题**同拍**更新（「慢一拍」回归守卫）",val&&disp,
              "值="+(val?"同步":"滞后")+" 标题="+(disp?"同步：「"+mtpMenu.Text+"」":"滞后：「"+(mtpMenu?.Text??"-")+"」期望 MTP: "+MtpLabel(target2)));
          }catch(Exception ex){ Check("A25c 选中模型点 MTP 项",false,"抛异常: "+ex.GetType().Name+" "+ex.Message); }
          finally{
            scT.Ctx=o.C; scT.KvMode=o.K; scT.CacheRam=o.R; scT.SplitMode=o.S;
            scT.TsGpu1=o.T; scT.MtpLevel=o.M; scT.ParamMode=o.P; scT.BindAll=o.B; scT.GpuSel=o.G;
            svcT.Spec.Ctx=o.C; svcT.Spec.KvMode=o.K; svcT.Spec.CacheRam=o.R; svcT.Spec.SplitMode=o.S;
            svcT.Spec.TsGpu1=o.T; svcT.Spec.MtpLevel=o.M; svcT.Spec.ParamMode=o.P; svcT.Spec.BindAll=o.B; svcT.Spec.GpuSel=o.G??"";
            Config.Save(cfg);                                // 回写原值 ⇒ 磁盘净变化=0
            selSvc=saveSel2; PersistSuppressed=saveSup2; RefreshChecks();
          }
        }
      }
      // A19（P1 的对照 · 负向控制）：同一棵树连续 Hook 两次，项级订阅数**只记一次**。
      // 期望 delta==2（树内 2 个项各订阅一次）；若回归成"每次 Hook 都重新订阅"则 delta==4 ⇒ 当场红。
      var idem=new ContextMenuStrip();
      idem.Items.Add(new ToolStripMenuItem("幂等项①"){CheckOnClick=true});
      idem.Items.Add(new ToolStripMenuItem("幂等项②"));
      int subBefore=_keep.ItemSubscriptions;
      _keep.Hook(idem); _keep.Hook(idem);
      int subDelta=_keep.ItemSubscriptions-subBefore;
      Check("A19 负向控制(P1)：同一棵树连续 Hook 两次，项级订阅数不增（期望 2；双倍=4 ⇒ 无界订阅回归）",subDelta==2,"delta="+subDelta+" items=2");
      try{ idem.Close(); }catch{} idem.Dispose(); Application.DoEvents();
      // A17b 负向控制：挂了裁决器也不能变成钉子。先把新鲜印记放陈旧（>500ms）再切窗口 —— 否则被闸门拦下是正确行为，不是钉子。
      System.Threading.Thread.Sleep(600);
      var nail=new ContextMenuStrip(); nail.Items.Add(new ToolStripMenuItem("假状态项"){CheckOnClick=true});
      _keep.Hook(nail); nail.Show(new Point(80,80)); Application.DoEvents();
      var other=new Form{Text="钉子探针",ShowInTaskbar=false,Size=new Size(160,80)};
      other.Show(); other.Activate(); Application.DoEvents(); System.Threading.Thread.Sleep(120); Application.DoEvents();
      Check("A17b 负向控制：切到别的窗口后菜单照关（裁决器不管失焦 ⇒ 不是钉子）",!nail.Visible,"Visible="+nail.Visible);
      other.Close(); other.Dispose(); try{nail.Close();}catch{} nail.Dispose(); Application.DoEvents();

      sb.AppendLine();
      sb.AppendLine("=== ⑤ 一级项接线（2026-09-12 快捷启动 / 2026-09-26 选中语义）===");
      // 2026-09-26：快捷启动**只对 DSH 一位一级项成立**（点一级 DSH 项=未启动时拉起）。
      //   模型的一级项改了语义：点 = 选中该模型（互斥单选），不再叠加启动；启动走「当前模型」组那三个按钮。
      //   ⇒ 接线数期望从 1+svcMenus.Count 降为 1（降了不是漏判，是这次改动的预期结果）。
      Check("[接线] 已接线一级项数 == 1(仅 DSH；模型一级项改走选中)",quickStartWired==1,
        "实得="+quickStartWired+" 期望=1");
      // 判据同源（用户视觉一致）：「启动模型」按钮的放行条件必须 == SvcStatus.Enable 的判定
      //   ⇒ 圆点红 / 按钮可点 / 真能启动 —— 三者同真同假（判据没变，只是落点从每模型子菜单挪到当前模型组）。
      int qOk=0,qBad=0;
      {
        var s=CurSvc();
        if(s!=null){
          bool busy=s.PortBusy&&!s.Running&&!s.Starting;
          bool gate=SvcStatus.Enable(s.Starting,s.Running,busy).start;
          if(gate==curStart.Enabled) qOk++; else qBad++;
        }
      }
      Check("[接线] 判据与「启动模型」项 Enabled 同源（当前模型）",CurSvc()!=null&&qBad==0,"同源="+qOk+" 不一致="+qBad);
      // 判据穷举：只有"未跑 / 未启动中 / 端口没被占"才放行 —— 四条独立断言（非空转的负向控制）
      Check("[快启] 未运行+未启动中+端口空闲 ⇒ 放行",SvcStatus.Enable(false,false,false).start);
      Check("[快启][负向] 启动中 ⇒ 不放行（防重复拉起）",!SvcStatus.Enable(true ,false,false).start);
      Check("[快启][负向] 运行中 ⇒ 不放行",!SvcStatus.Enable(false,true ,false).start);
      Check("[快启][负向] 端口被外部占 ⇒ 不放行（会撞 Start 的守卫）",!SvcStatus.Enable(false,false,true ).start);
      // DSH 一级项必须**仍是③类**：快捷启动是叠加（只多发一条启动指令），不改菜单展开/关闭语义。
      Neg("[快启] DSH 一级项仍是③类（叠加式：不动展开/关闭语义）",menu,dshMenu);
      // 反过来，模型一级项 2026-09-26 起**是①类**：点它=选中（设置态，不是执行），点完要留在菜单里继续调参数。
      foreach(var m in svcMenus)
        Check("[选中] "+m.svc.Name+" 一级项（点击选中）IsState==True（①类：点完留菜单）",
          _keep.IsState(menu,m.root),"IsState="+_keep.IsState(menu,m.root));
      // 行为断言（隔离菜单 + 假的 canStart/start ⇒ 不碰真服务、不真的启动什么）：
      //   ⚠️ 别用反射读订阅数 —— WinForms 的 ToolStripItem 事件走 EventHandlerList(Events)，
      //      **没有同名 backing field**（第一版那样写 n 恒 0，是读法错、不是没挂）。
      int qFired=0; var qIso=new ContextMenuStrip();
      var qItem=new ToolStripMenuItem("假一级项"); qIso.Items.Add(qItem);
      WireQuickStart(qItem,()=>true,()=>{qFired++;},"假一级项");
      qIso.Show(new Point(120,120)); Application.DoEvents();
      qItem.PerformClick(); Application.DoEvents();
      Check("A20 快启行为：canStart=true ⇒ PerformClick 触发启动恰好 1 次",qFired==1,"fired="+qFired);
      // A24（2026-09-12 副修）：父项默认不补关（见 A23）⇒ **挂了快捷启动的父项**在动作真触发时要自己补一次
      //   **延后**关闭（BeginInvoke，绝不同步 Close）。这里验的就是"关掉了"。
      Check("A24 快启父项：动作真触发后菜单被**延后**关闭（父项默认不补关的例外）",!qIso.Visible,"Visible="+qIso.Visible);
      qItem.PerformClick(); Application.DoEvents();
      Check("A21 快启负向(闸门)：1500ms 内再点一次 ⇒ 不重复触发（防重复拉起进程）",qFired==1,"fired="+qFired);
      try{ qIso.Close(); }catch{} qIso.Dispose(); Application.DoEvents();
      int q2Fired=0; var q2=new ContextMenuStrip();
      var q2Item=new ToolStripMenuItem("假一级项2"); q2.Items.Add(q2Item);
      WireQuickStart(q2Item,()=>false,()=>{q2Fired++;},"假一级项2");
      q2.Show(new Point(140,140)); Application.DoEvents();
      q2Item.PerformClick(); Application.DoEvents();
      Check("A22 快启负向(判据)：canStart=false（已启动/启动中/端口被占）⇒ 不触发",q2Fired==0,"fired="+q2Fired);
      try{ q2.Close(); }catch{} q2.Dispose(); Application.DoEvents();
      // INFO（不计分）：父项 PerformClick 到底触发不触发 Click —— 程序化事实，给真鼠标 L2 留参照。
      int parentClicks=0;
      var pIso=new ContextMenuStrip();
      var pPar=new ToolStripMenuItem("假父项"); pPar.DropDownItems.Add(new ToolStripMenuItem("假子项"));
      pPar.Click+=(s,e)=>{parentClicks++;};
      pIso.Items.Add(pPar); pIso.Show(new Point(100,100)); Application.DoEvents();
      pPar.PerformClick(); Application.DoEvents();
      bool pIsoStayed=pIso.Visible;
      sb.AppendLine("[INFO] 父项 PerformClick：Click 触发 "+parentClicks+" 次；隔离菜单仍 Visible="+pIsoStayed+"（程序化路径；真鼠标另论）");
      try{ pIso.Close(); }catch{} pIso.Dispose(); Application.DoEvents();
    }catch(Exception ex){ fail++; sb.AppendLine("[FAIL] PROBE EX: "+ex.Message+"\r\n"+ex.StackTrace); }
    sb.AppendLine();
    sb.AppendLine("SUMMARY pass="+pass+" fail="+fail);
    sb.AppendLine(fail==0?"RESULT PASS":"RESULT FAIL");
    return sb.ToString();
  }
}

