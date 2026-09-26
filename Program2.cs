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
    // 图标自愈监听者（2026-09-22 新增）：YES 才说明跑的是含自愈的构建，也是"产物确实是新版"的锚点
    bool rz=false;
    try{ EventWaitHandle ev; if(EventWaitHandle.TryOpenExisting(ResurrectName, out ev)){ rz=true; ev.Dispose(); } }catch{}
    sb.AppendLine("resurrect name="+ResurrectName+"  listener="+(rz?"YES (含图标自愈的构建)":"NO  (无托盘监听 / 旧构建)"));
    return sb.ToString();
  }

  // —— 图标自愈（2026-09-22）——
  // 症状：托盘进程活着、通知区图标却被丢弃（explorer 重启 / 启动早于 shell 就绪等）⇒ 界面看是"托盘没了"；
  //       而 §2 的单实例锁让用户再点一次也**静默无反应** ⇒ 唯一出路是 taskkill。用户视角 =「点不动」。
  // 修法（不违反 §2「后到实例不产生第二托盘」）：后到实例改为**先救活老实例**，救不动才接管。
  //   Local\DSH托盘_Resurrect    = 后到实例 → 主实例：请把通知区图标挂回来
  //   Local\DSH托盘_ResurrectAck = 主实例 **在 UI 线程**（UiDispatcher.Post → 隐藏锚点控件 BeginInvoke）
  //                                重挂成功后的回执。UI 线程已僵死 ⇒ 回执永不抵达 ⇒ 后到实例据此判死接管。
  static string ResurrectName    => @"Local\DSH托盘_Resurrect";
  static string ResurrectAckName => @"Local\DSH托盘_ResurrectAck";

  // 请求在跑的主实例重挂图标。true = 有活实例且**UI 线程**已回执（调用方应静默退出）
  static bool TryResurrectPrimary(){
    try{
      // 先建回执口再发信号：AutoReset 事件在无人等待时 Set 会丢，先建可免这层竞态
      using var ack=new EventWaitHandle(false,EventResetMode.AutoReset,ResurrectAckName);
      EventWaitHandle? sig=null;
      if(!EventWaitHandle.TryOpenExisting(ResurrectName, out sig) || sig==null) return false; // 无监听者 ⇒ 不是活托盘
      using(sig) sig.Set();
      return ack.WaitOne(1500);
    }catch{ return false; }
  }

  // 清掉同名僵尸进程（**不含自己**）：仅在"主实例无 UI 回执"判定后调用，是接管前最后一步。
  // 不带 /T：模型服务是独立进程，清托盘不打断模型（与 --exit 默认保留模型同义）。
  static void KillStaleTrays(){
    try{
      int me=Process.GetCurrentProcess().Id;
      foreach(var p in Process.GetProcessesByName("DSHTray")){
        try{ if(p.Id!=me) p.Kill(); }catch{}
        try{ p.Dispose(); }catch{}
      }
    }catch{}
  }
  [STAThread] public static void Main(string[] args){
    // Per-Monitor V2：高分屏(2560@150%)下若 DPI unaware，Show(点) 坐标被系统二次缩放 → 菜单落到 (0,0)/越界看不到
    try{ Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }catch{}
    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    Application.ThreadException+=(s,e)=>{ try{ File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tray-ex.log"),DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" THREADEX "+e.Exception+"\r\n"); }catch{} };
    AppDomain.CurrentDomain.UnhandledException+=(s,e)=>{ try{ File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tray-ex.log"),DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" FATAL "+e.ExceptionObject+"\r\n"); }catch{} };
    // 单实例互斥：开机自启双入口/重复启动时，后到者直接退出（防双托盘）
    // S4（2026-09-12）：命令行解析已抽到 CliOptions（Core，纯数据 ⇒ 可单测）。
    // 下面只做**同义绑定**（字段名 → 局部名），本方法后续 130 行分派逻辑一行未改。
    var cli = CliOptions.Parse(args);
    bool dump        = cli.DumpMenu;
    bool selftest    = cli.SelftestLogWin;
    bool menuProbe   = cli.SelftestMenuShow;
    bool pluginProbe = cli.SelftestPlugins;
    bool rpcProbe    = cli.SelftestRpc;
    bool lockProbe   = cli.SelftestLock;
    bool exitProbe   = cli.SelftestExit;
    bool svcProbe    = cli.SelftestSvcMenu;
    bool perfProbe   = cli.SelftestPerf;
    bool sinkProbe   = cli.SelftestLogSink;
    bool uiProbe     = cli.SelftestUiThread;
    bool keepProbe   = cli.SelftestMenuKeep;
    bool tipsProbe   = cli.SelftestTips;
    bool memTrimProbe= cli.SelftestMemTrim;
    bool warmupProbe = cli.SelftestWarmup;
    // 命令行真实基准：DSHTray.exe --bench <port> [promptTok] [genTok] [runs]
    int benchPort=cli.BenchPort, benchP=cli.BenchPrompt, benchG=cli.BenchGen, benchR=cli.BenchRuns;
    // 退出语义（2026-09-11 反转）：默认「只退托盘、保留模型」；要连模型一起停必须显式 --stopall。
    // --nostop 保留为兼容别名（等价默认，旧脚本原样可用）；同时给 --stopall 时以 --stopall 为准。
    bool stopAll = cli.StopAll;
    bool noStop  = cli.NoStop;   // legacy alias: keep model (== default)
    bool askExit = cli.AskExit;
    // 单实例互斥：开机自启双入口/重复启动时后到者退出；**诊断模式与 --bench 不占锁**
    // （原来的 11 个 `!flag &&` 长串折成 cli.IsLockFree 一个名字，判定完全等价，且该属性有单测）
    if(!cli.IsLockFree){
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
      if(!createdNew){
        // 图标自愈（2026-09-22，契约见 DEVELOPMENT.md §2 第 2 条）：已有实例在跑，但它可能是**无头托盘**
        //（进程活着、图标被通知区丢弃）—— 那就正是"用户点它没反应"的现场。
        //   ①先请它自己把图标挂回来；拿到 **UI 线程**回执 ⇒ 它是活的 ⇒ 本实例按契约静默退出（不产生第二托盘）。
        //   ②拿不到回执（无监听者 = 旧构建残留 / UI 线程僵死）⇒ 清僵尸后由本实例接管 —— 保证"双击必有图标"。
        if(!TryResurrectPrimary()){
          KillStaleTrays();
          for(int i=0;i<20 && !createdNew;i++){ // 等僵尸真正释放互斥锁（≤3s），再抢一次
            try{ _single.Dispose(); }catch{}
            System.Threading.Thread.Sleep(150);
            _single=new Mutex(true,@"Local\DSH托盘_SingleInstance", out createdNew);
          }
        }
        if(!createdNew) return; // 仍被占 ⇒ 按契约放弃，绝不硬撑出第二托盘
      }
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
    // 工作集回收（2026-09-13）：DSHTray.exe --trim [port] —— 纯静态，**不建 TrayApp**（同 --bench 的理由：
    // 批处理/计划任务里跑，绝不该因此起出一个带通知区图标、永不退出的真托盘）。
    if(cli.IsTrim){
      string tr="TRIM FAIL";
      try{ tr=MemTrimCli.Run(cli.TrimPort); }catch(Exception ex){ tr="TRIM EX: "+ex.Message; }
      try{ Console.WriteLine(tr); }catch{}
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-trim.txt"), tr); }catch{}
      Environment.ExitCode=0;
      return;
    }
    // 启动预热（2026-09-13）：DSHTray.exe --warmup [port] —— 与 --trim 同构，不建 TrayApp。
    // 单独有条命令行的理由：模型重启后「人还没到」的那段时间，才是预热的黄金窗口。
    if(cli.IsWarmup){
      string wr="WARMUP FAIL";
      try{ wr=WarmupCli.Run(cli.WarmupPort); }catch(Exception ex){ wr="WARMUP EX: "+ex.Message; }
      try{ Console.WriteLine(wr); }catch{}
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-warmup.txt"), wr); }catch{}
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
    // 「点了关不关」统一裁决器自检（--selftest-menukeep）：纯函数 + 真实菜单树分类 + 隔离菜单行为
    if(keepProbe){
      string kr="MENU KEEP PROBE FAIL";
      try{ kr=app.MenuKeepProbe(); }catch(Exception ex){ kr="MENU KEEP PROBE FAIL: "+ex.Message+"\r\n"+ex.StackTrace; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-menukeep.txt"), kr); }catch{}
      app.DisposeForDump();
      return;
    }
    // 悬停提示窗自检（--selftest-tips）：四判据（平台渲染关闭 / 几何扫描+负控 / ExStyle 读回 / 寿命配对）。
    if(tipsProbe){
      string tr="MENU TIPS PROBE FAIL";
      try{ tr=app.MenuTipsProbe(); }catch(Exception ex){ tr="MENU TIPS PROBE FAIL: "+ex.Message+"\r\n"+ex.StackTrace; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-tips.txt"), tr); }catch{}
      app.DisposeForDump();
      return;
    }
    // 内存回收自检（--selftest-memtrim）：策略矩阵 + 菜单接线 + 台账文件可写性；实测回收默认跳过（--selftest-memtrim live 才做）
    if(memTrimProbe){
      string mr="MEMTRIM PROBE FAIL";
      try{ mr=app.MemTrimProbe(args!=null&&Array.IndexOf(args,"live")>=0); }catch(Exception ex){ mr="MEMTRIM PROBE FAIL: "+ex.Message+"\r\n"+ex.StackTrace; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-memtrim.txt"), mr); }catch{}
      app.DisposeForDump();
      return;
    }
    // 启动预热自检（--selftest-warmup）：判定矩阵 + 运行态登记 + kvctl 可达性 + 菜单接线；实测默认跳过
    if(warmupProbe){
      string wp="WARMUP PROBE FAIL";
      try{ wp=app.WarmupProbe(args!=null&&Array.IndexOf(args,"live")>=0); }catch(Exception ex){ wp="WARMUP PROBE FAIL: "+ex.Message+"\r\n"+ex.StackTrace; }
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-warmup.txt"), wp); }catch{}
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
    // S4：改用 cli.IsDiagnostic（比原 10 项清单**多覆盖** perfProbe —— 它在更早处已 return，故实际不可达，
    //     但方向是单侧收紧；「每个探针标志都必须让 IsDiagnostic 为真」现在由单测穷举看住）。
    if(cli.IsDiagnostic){ Environment.ExitCode=3; return; }
    if(_isPrimary){ // 主实例监听：优雅退出信号（默认 --exit=保留模型；--exit --stopall=连模型停）＋ 图标自愈信号
      var evStopAll=new EventWaitHandle(false,EventResetMode.AutoReset,ExitSignalName(false));
      var evKeep   =new EventWaitHandle(false,EventResetMode.AutoReset,ExitSignalName(true));
      // 图标自愈：主实例持有这两个事件 = 「有监听者」，后到实例据此区分"活托盘"与"僵尸残留"
      var evResurrect=new EventWaitHandle(false,EventResetMode.AutoReset,ResurrectName);
      var evAck      =new EventWaitHandle(false,EventResetMode.AutoReset,ResurrectAckName);
      var wt=new System.Threading.Thread(()=>{
        while(true){
          int idx=WaitHandle.WaitAny(new WaitHandle[]{evStopAll,evKeep,evResurrect}); // 0=stopall 1=keep 2=resurrect
          if(idx==2){ // 重挂图标 + 回执（Ack 必须由 UI 线程发出：UI 线程僵死则回执不发 ⇒ 后到实例接管）
            try{ app.ResurrectIcon(()=>{ try{ evAck.Set(); }catch{} }); }catch{}
            continue;
          }
          try{ app.ExitFromSignal(idx==0); }catch{}
          return;
        }
      });
      wt.IsBackground=true; wt.Start();
      GC.KeepAlive(evAck); // 事件句柄活到进程结束（被 lambda 捕获，此处仅明示意图）
    }
    Application.Run(app);
  }
}

