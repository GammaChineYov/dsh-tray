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

// S1（2026-09-11）拆自 Program.cs：零逻辑改动，仅位移。
public static class Program2 {
  static Mutex? _single; static bool _isPrimary;
  // 退出信号分两条独立命名的事件（名字与各自含义自 2026-09-10 起固定，**不随默认值反转而改名**）：
  //   Local\DSH托盘_ExitSignal        = 停模型退出（--exit --stopall）
  //   Local\DSH托盘_ExitSignal_NoStop = 保留模型退出（--exit，默认）
  // 分名的意义是跨版本安全：老构建只监听 stop 事件，收到「保留模型」那条信号时听不见 → 不会被误停。
  static string ExitSignalName(bool keepModel=false){ return keepModel ? @"Local\DSH托盘_ExitSignal_NoStop" : @"Local\DSH托盘_ExitSignal"; }
  // 隐藏自检模式：--selftest-exit 探测两条退出信号是否都有监听者（有=托盘在跑，信号能被听到）
  static string ExitSignalProbe(){
    var sb=new System.Text.StringBuilder();
    sb.AppendLine("EXIT SIGNAL PROBE  (Local\\ session 命名事件)");
    sb.AppendLine("semantics: --exit = keep model (default) | --exit --stopall = stop model too");
    foreach(bool keep in new bool[]{true,false}){
      string name=ExitSignalName(keep); bool listener=false;
      try{ EventWaitHandle ev; if(EventWaitHandle.TryOpenExisting(name, out ev)){ listener=true; ev.Dispose(); } }catch{}
      sb.AppendLine((keep?"keep   ":"stopall")+" name="+name+"  listener="+(listener?"YES (托盘在跑，信号可送达)":"NO  (无托盘监听)"));
    }
    return sb.ToString();
  }
  [STAThread] public static void Main(string[] args){
    // Per-Monitor V2：高分屏(2560@150%)下若 DPI unaware，Show(点) 坐标被系统二次缩放 → 菜单落到 (0,0)/越界看不到
    try{ Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }catch{}
    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    Application.ThreadException+=(s,e)=>{ try{ File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tray-ex.log"),DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" THREADEX "+e.Exception+"\r\n"); }catch{} };
    AppDomain.CurrentDomain.UnhandledException+=(s,e)=>{ try{ File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tray-ex.log"),DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" FATAL "+e.ExceptionObject+"\r\n"); }catch{} };
    // 单实例互斥：开机自启双入口/重复启动时，后到者直接退出（防双托盘）
    bool dump = args!=null && args.Length>0 && args[0]=="--dump-menu";
    bool selftest = args!=null && Array.IndexOf(args,"--selftest-logwin")>=0;
    bool menuProbe = args!=null && Array.IndexOf(args,"--selftest-menushow")>=0;
    bool pluginProbe = args!=null && Array.IndexOf(args,"--selftest-plugins")>=0;
    bool rpcProbe = args!=null && Array.IndexOf(args,"--selftest-rpc")>=0;
    bool lockProbe = args!=null && Array.IndexOf(args,"--selftest-lock")>=0;
    bool exitProbe = args!=null && Array.IndexOf(args,"--selftest-exit")>=0;
    bool svcProbe = args!=null && Array.IndexOf(args,"--selftest-svcmenu")>=0;
    bool perfProbe = args!=null && Array.IndexOf(args,"--selftest-perf")>=0;
    bool sinkProbe = args!=null && Array.IndexOf(args,"--selftest-logsink")>=0;
    bool uiProbe   = args!=null && Array.IndexOf(args,"--selftest-uithread")>=0;
    // 命令行真实基准：DSHTray.exe --bench <port> [promptTok] [genTok] [runs]
    int benchPort=0, benchP=512, benchG=64, benchR=3;
    if(args!=null){
      int bi=Array.IndexOf(args,"--bench");
      if(bi>=0 && bi+1<args.Length){
        int.TryParse(args[bi+1], out benchPort);
        if(bi+2<args.Length) int.TryParse(args[bi+2], out benchP);
        if(bi+3<args.Length) int.TryParse(args[bi+3], out benchG);
        if(bi+4<args.Length) int.TryParse(args[bi+4], out benchR);
      }
    }
    // 退出语义（2026-09-11 反转）：默认「只退托盘、保留模型」；要连模型一起停必须显式 --stopall。
    // --nostop 保留为兼容别名（等价默认，旧脚本原样可用）；同时给 --stopall 时以 --stopall 为准。
    bool stopAll = args!=null && Array.IndexOf(args,"--stopall")>=0;
    bool noStop  = args!=null && Array.IndexOf(args,"--nostop")>=0;   // legacy alias: keep model (== default)
    bool askExit = (args!=null && Array.IndexOf(args,"--exit")>=0) || noStop || stopAll;
    // 单实例互斥：开机自启双入口/重复启动时后到者退出；诊断模式（--dump-menu/--selftest/--selftest-menushow/--selftest-plugins/--selftest-rpc/--selftest-lock）不占锁
    if(!dump && !selftest && !menuProbe && !pluginProbe && !rpcProbe && !lockProbe && !exitProbe && !svcProbe && !perfProbe && !sinkProbe && !uiProbe && benchPort<=0){
      if(askExit){ // 优雅退出：通知正在运行的主实例自己注销图标再退出（替代 kill /F，避免通知区死图标槽）
        bool keepModel = !stopAll;                      // 默认保留模型；--stopall 才连模型一起停
        string sig=ExitSignalName(keepModel); bool heard=false;
        try{
          EventWaitHandle ev;
          if(EventWaitHandle.TryOpenExisting(sig, out ev)){ using(ev){ ev.Set(); heard=true; } } // 有主实例在监听 → 只 Set 它自己创建的事件
          else{ using(var ev2=new EventWaitHandle(false,EventResetMode.AutoReset,sig)){ ev2.Set(); } } // 无监听者：建后即弃，无人响应
        }catch{}
        string res=(heard?(keepModel?"OK 已通知托盘退出（保留模型服务：模型继续服务，下次启动自动接管）":"OK 已通知托盘退出（--stopall：同时停止模型服务）"):"NOOP 没有正在运行的托盘实例，信号无人接收")
                   +"  signal="+sig;
        try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"exit-signal.txt"), res+"\r\n"); }catch{}
        Environment.ExitCode = heard?0:2; // 供脚本判定：0=已送达 2=无托盘在跑
        return;
      }
      bool createdNew;
      _single = new Mutex(true, @"Local\DSH托盘_SingleInstance", out createdNew);
      if(!createdNew) return;
      _isPrimary=true;
    }
    // 退出信号探测：纯静态，必须在 new TrayApp 之前处理 —— TrayApp 构造在非 dumpMode 下会建
    // TaskbarWatcher（隐藏窗口/伴随线程），进程不会因 Main 返回而退出（--selftest-exit 曾因此残留进程）
    if(exitProbe){
      string er="EXIT PROBE FAIL";
      try{ er=ExitSignalProbe(); }catch(Exception ex){ er="EXIT PROBE FAIL: "+ex.Message; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-exit.txt"), er); }catch{}
      Environment.ExitCode=0;
      return;
    }
    // 性能日志自检：纯数据层（指纹/解析/聚合/轮转/导入），不建 TrayApp、不碰真实服务
    if(perfProbe){
      string pr="PERF PROBE FAIL";
      try{ pr=PerfProbe.Run(); }catch(Exception ex){ pr="PERF PROBE FAIL: "+ex.Message; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-perf.txt"), pr); }catch{}
      Environment.ExitCode=0;
      return;
    }
    // 日志缓冲自检：纯数据层（有界 / 线程安全 / 游标夹取），不建 TrayApp、不碰真实服务
    if(sinkProbe){
      string sr="LOGSINK PROBE FAIL";
      try{ sr=LogSinkProbe.Run(); }catch(Exception ex){ sr="LOGSINK PROBE FAIL: "+ex.Message; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-logsink.txt"), sr); }catch{}
      Environment.ExitCode=0;
      return;
    }
    // 命令行基准（真实请求，会占用该端口模型数十秒）
    if(benchPort>0){
      string br="BENCH FAIL";
      try{ br=PerfProbe.BenchLive(benchPort, benchP, benchG, benchR); }catch(Exception ex){ br="BENCH EX: "+ex.Message; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-bench.txt"), br); }catch{}
      Environment.ExitCode=0;
      return;
    }
    // dumpMode 与 _isPrimary 严格互补：诊断模式一律不建 TaskbarWatcher/冷启动重注册（原写法漏了 menuProbe → 残留进程隐患）
    var app=new TrayApp(!_isPrimary);
    if(lockProbe){
      string lr="LOCK PROBE FAIL";
      try{ lr=app.LockProbe(); }catch(Exception ex){ lr="LOCK PROBE FAIL: "+ex.Message; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-lock.txt"), lr); }catch{}
      app.DisposeForDump();
      return;
    }
    if(svcProbe){
      string sr="SVC MENU PROBE FAIL";
      try{ sr=app.SvcMenuProbe(); }catch(Exception ex){ sr="SVC MENU PROBE FAIL: "+ex.Message; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-svcmenu.txt"), sr); }catch{}
      app.DisposeForDump();
      return;
    }
    // UI 线程调度自检（实例探针：需要一个**已创建但未 Show** 的 LogForm，正是 P0-1 的触发场景）
    if(uiProbe){
      string ur="UITHREAD PROBE FAIL";
      try{ ur=app.UiThreadProbe(); }catch(Exception ex){ ur="UITHREAD PROBE FAIL: "+ex.Message; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-uithread.txt"), ur); }catch{}
      app.DisposeForDump();
      return;
    }
    if(rpcProbe){
      string rr="RPC FAIL: probe exception";
      // 必须包 Task.Run：TrayApp 构造建 LogForm 后主线程装上 WindowsFormsSynchronizationContext，
      // 直接 GetResult() 等 await 会把续体排进不泵消息的主线程 → 死锁（自检路径专用规避）
      try{ rr=Task.Run(()=>app.RpcProbe()).GetAwaiter().GetResult(); }catch(Exception ex){ rr="RPC FAIL: "+ex.Message; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-rpc.txt"), rr); }catch{}
      app.DisposeForDump();
      return;
    }
    if(pluginProbe){
      string pr=app.PluginScanProbe();
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-plugins.txt"), pr); }catch{}
      app.DisposeForDump();
      return;
    }
    if(dump){
      app.ProbeOnce();
      string txt=app.DumpMenuText();
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"menu-dump.txt"), txt); }catch{}
      app.DisposeForDump();
      return;
    }
    if(selftest){
      app.ProbeOnce();
      app.OpenDshLog();
      System.Threading.Thread.Sleep(1500);
      string logRep="LOGWIN PROBE FAIL";
      try{ logRep=app.LogWinProbe(); }catch(Exception ex){ logRep="LOGWIN PROBE EX: "+ex.Message; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-logwin.txt"), app.DshLogSnapshot()+"\r\n\r\n== LOGWIN PROBE ==\r\n"+logRep); }catch{}
      app.DisposeForDump();
      return;
    }
    if(menuProbe){
      string r=app.MenuShowProbe();
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-menushow.txt"), r); }catch{}
      app.DisposeForDump();
      return;
    }
    // 防御栏：任何诊断/CLI 模式都不得走到 Application.Run。
    // 血的教训：曾漏写 exitProbe 分支（它又跳过了单实例锁），--selftest-exit 一路落到 Application.Run，
    // 起出一个「没有锁的幽灵托盘」+ 通知区图标且永不退出，只能手工 taskkill。
    if(dump||selftest||menuProbe||pluginProbe||rpcProbe||lockProbe||exitProbe||svcProbe||sinkProbe||uiProbe){ Environment.ExitCode=3; return; }
    if(_isPrimary){ // 主实例监听优雅退出信号（默认 --exit=保留模型；--exit --stopall=连模型停；菜单两项同理）
      var evStopAll=new EventWaitHandle(false,EventResetMode.AutoReset,ExitSignalName(false));
      var evKeep   =new EventWaitHandle(false,EventResetMode.AutoReset,ExitSignalName(true));
      var wt=new System.Threading.Thread(()=>{
        int idx=WaitHandle.WaitAny(new WaitHandle[]{evStopAll,evKeep}); // 0=stopall 1=keep
        try{ app.ExitFromSignal(idx==0); }catch{}
      });
      wt.IsBackground=true; wt.Start();
    }
    Application.Run(app);
  }
}
