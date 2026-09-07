using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace QwenTray;

public class LogForm : Form {
  public RichTextBox box=new();
  public LogForm(string title="DSH托盘 日志"){ Text=title; Size=new Size(680,460); box.Dock=DockStyle.Fill; box.ReadOnly=true; box.Font=new Font("Consolas",9); Controls.Add(box); FormClosing+=(s,e)=>{ e.Cancel=true; this.Hide(); }; }
  public void Append(string s){ try{ if(string.IsNullOrEmpty(s))return; if(box.IsDisposed)return; if(box.InvokeRequired){ box.BeginInvoke((MethodInvoker)(()=>{ if(!box.IsDisposed) box.AppendText(s); })); } else box.AppendText(s); }catch{} }
}

public class TrayApp : ApplicationContext {
  NotifyIcon icon; ContextMenuStrip menu; LogForm logForm; System.Windows.Forms.Timer clock; bool adopted=false;
  ToolStripMenuItem? itemOpenChat,autoStartItem; ToolStripSeparator? sepSess; ToolStripMenuItem? pm,pm0,pm1,pm2;
  ToolStripMenuItem? gpuMenu,gpuAllItem,gpuCpuItem,ctxMenu; List<(ToolStripMenuItem item,int idx)> gpuItems=new(); List<ToolStripMenuItem> ctxItems=new();
  ToolStripMenuItem? splitMenu,splitLayerItem,splitRowItem;
  ToolStripMenuItem? kvMenu,kvDefItem,kv8Item,kv16Item;
  ToolStripMenuItem? cacheRamMenu; List<ToolStripMenuItem> cacheRamItems=new();
  ToolStripMenuItem? bindItem; bool bindAll=true; // 模型监听地址：true=0.0.0.0（局域网可访问，默认）；false=127.0.0.1（仅本机）
  ToolStripMenuItem? dshMenu,dshStatusItem,dshStartItem,dshRestartItem,dshStopItem;
  volatile int dshState=0; // DSH 服务状态：0=未启动(红) 1=启动中(黄) 2=运行中(绿)
  long dshStartMs=0; bool dshTimeoutLogged=false; Bitmap? dotRed,dotYellow,dotGreen;
  volatile int dshPortUp=-1; int _probeBusy=0;
  LogForm? dshLogForm; long lastOutPos=0,lastErrPos=0;
  Form? popup; WebView2? wv; Form? officialForm; WebView2? officialWv;
  bool _popupLogged; string _pendingAfterLogin=""; // dsh web 弹窗登录态与待跳转目标（先 token 登录种 cookie 再续跳）
  int _popW=1080,_popH=760,_offW=1100,_offH=760; // 会话弹窗 / 官方弹窗默认尺寸（用户调整后记忆到 cfg）
  int _popX=int.MinValue,_popY=int.MinValue,_offX=int.MinValue,_offY=int.MinValue; // 弹窗上次位置（int.MinValue=未记录 → 首次居中）
  System.Windows.Forms.Timer? _posSaveTimer; Action? _posSaveAct; // 位置/大小保存防抖
  volatile string gpuTip=""; int gpuTick=0; volatile string cpuTemp=""; volatile string _lastTip=""; int _lastIconMs=0;
  volatile List<Gpu> gpus=new(); GpuSelection gpuSel=GpuSelection.FromCfg("all"); int ctxVal=196608;
  int paramMode=1; // 0=通用思考 1=编码思考 2=Instruct
  int splitMode=1; // 0=按层切分 layer（双卡偶发崩，不推荐） 1=张量并行 tensor（内置 AllReduce，稳定推荐，默认）
  int kvMode=1;    // KV 缓存类型：0=默认(llama默认f16) 1=q8_0(8bit,省显存) 2=f16(16bit)
  int cacheRam=0;   // llama-server -cram/--cache-ram（prompt/前缀缓存占系统内存上限，MiB；0=禁用、-1=无限制）
  int tsGpu1=50;  // 张量并行比例：GPU1 占比%（0-100，默认50=均分；越大 GPU1 分越多，方便给 GPU0/Unity 腾显存）
  AppConfig cfg; List<Service> services=new(); List<(ToolStripMenuItem item,Service svc)> sessionItems=new(); List<(ToolStripMenuItem item,Service svc)> startItems=new();
  // —— 最近会话（DSH 二级菜单：最近发消息的未归档会话；后台读 .dsh/storages，点击=弹窗 ?session= 直达）——
  ToolStripMenuItem? recentHeader; List<RecentSession> recentList=new(); int recentBusy=0; long recentRefreshMs=0;
  static readonly DateTime UNIX_EPOCH=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc);
  // 采集缓存：JSON 文件 mtime 未变则复用解析结果，避免每次全量读+防病毒扫描拖慢右键/启动
  long _wsMt; HashSet<string> _archCache=new();
  Dictionary<string,(long mt,string title,long last,long created,string cwd,bool sub)> _sessCache=new();

  public TrayApp(bool dumpMode=false){
    cfg=Config.Load();
    foreach(var sc in cfg.Services){ if(!sc.Enabled) continue;
      var svc=new Service{Name=sc.Name,Port=sc.Port,Model=sc.Model,UseMmproj=sc.UseMmproj,Mmproj=sc.Mmproj,SpecDecode=sc.SpecDecode,Provider=sc.Provider};
      if(sc.Batch>0) svc.Batch=sc.Batch; if(sc.Ubatch>0) svc.Ubatch=sc.Ubatch;
      services.Add(svc);
    }
    gpus=GpuInfo.Discover();
    gpuTip=string.Join("\n",gpus.Select(g=>g.Line));
    LoadCfg();
    logForm=new LogForm();
    icon=new NotifyIcon{Icon=WhaleIcon(),Text="DSH托盘",Visible=!dumpMode};
    menu=new ContextMenuStrip();
    var items=new List<ToolStripItem>();
    // —— DSH（本机 DSH Web 服务，端口 3080）控制：状态图标 绿=运行中 黄=启动中 红=未启动；启动/重启/停止 DSH ——
    dshMenu=new ToolStripMenuItem("DSH");
    dshMenu.ToolTipText="DSH 服务状态：绿色=运行中、黄色=启动中、红色=未启动。可启动 / 重启 / 停止本机 DSH Web（端口 3080）";
    dshStatusItem=new ToolStripMenuItem("状态：未启动"){ Enabled=false };
    dshStartItem=new ToolStripMenuItem("启动 DSH",null,(s,e)=>Bg(()=>DshStart()));
    dshRestartItem=new ToolStripMenuItem("重启 DSH",null,(s,e)=>Bg(()=>DshRestart()));
    dshStopItem=new ToolStripMenuItem("停止 DSH",null,(s,e)=>Bg(()=>DshStop()));
    dshMenu.DropDownItems.Add(dshStatusItem);
    dshMenu.DropDownItems.Add(new ToolStripSeparator());
    dshMenu.DropDownItems.Add(dshStartItem); dshMenu.DropDownItems.Add(dshRestartItem); dshMenu.DropDownItems.Add(dshStopItem);
    dshMenu.DropDownItems.Add(new ToolStripSeparator());
    dshMenu.DropDownItems.Add(new ToolStripMenuItem("查看 DSH 日志",null,(s,e)=>OpenDshLog()));
    dshMenu.DropDownItems.Add(new ToolStripSeparator());
    dshMenu.DropDownItems.Add(new ToolStripMenuItem("打开 DSH 程序目录",null,(s,e)=>OpenDir(DshProgDir(),"DSH 程序目录")));
    dshMenu.DropDownItems.Add(new ToolStripMenuItem("打开 .dsh 目录",null,(s,e)=>OpenDir(DshHomeDir(),".dsh 目录")));
    // 最近会话（动态重建，见 RebuildRecentMenu；点击=弹窗导航 ?session=<id>）
    dshMenu.DropDownItems.Add(new ToolStripSeparator());
    recentHeader=new ToolStripMenuItem("最近会话"){Enabled=false};
    dshMenu.DropDownItems.Add(recentHeader);
    dshMenu.DropDownItems.Add(new ToolStripMenuItem("（读取中…）"){Enabled=false});
    items.Add(dshMenu); items.Add(new ToolStripSeparator());
    // 打开 DSH 会话（每服务一条，按运行态显隐）
    foreach(var s2 in services){ var it=new ToolStripMenuItem("打开 DSH 会话（"+s2.Name+"）",null,(e,a)=>OpenSession(s2)); sessionItems.Add((it,s2)); items.Add(it); }
    itemOpenChat=new ToolStripMenuItem("打开官方会话 chat",null,(s,e)=>OpenOfficial());
    items.Add(itemOpenChat);
    sepSess=new ToolStripSeparator(); items.Add(sepSess); items.Add(new ToolStripSeparator());
    // 启动（每服务一条）
    foreach(var s2 in services){ var it=new ToolStripMenuItem("启动 "+s2.Name+" ("+s2.Port+")",null,(e,a)=>Start(s2)); startItems.Add((it,s2)); items.Add(it); }
    items.Add(new ToolStripSeparator());
    var m5=new ToolStripMenuItem("停止全部",null,(s,e)=>StopAll());
    var m6=new ToolStripMenuItem("重启全部",null,(s,e)=>RestartAll());
    items.Add(m5); items.Add(m6);
    items.Add(new ToolStripSeparator());
    // 推理参数组（radio）
    pm=new ToolStripMenuItem("推理参数组："+ParamLabel(paramMode));
    pm0=new ToolStripMenuItem("通用思考 (temp1.0/pres1.5)",null,(s,e)=>SetParam(0));
    pm1=new ToolStripMenuItem("编码思考 (temp0.6/pres0.0)",null,(s,e)=>SetParam(1));
    pm2=new ToolStripMenuItem("Instruct (temp0.7/pres1.5)",null,(s,e)=>SetParam(2));
    pm0.CheckOnClick=false; pm1.CheckOnClick=false; pm2.CheckOnClick=false;
    pm.DropDownItems.AddRange(new ToolStripItem[]{pm0,pm1,pm2});
    items.Add(pm); items.Add(new ToolStripSeparator());
    // GPU 选择（复选框：全部（GPU）/ CPU / GPUn 可多选）
    gpuMenu=new ToolStripMenuItem("GPU: 全部");
    gpuMenu.ToolTipText="启动服务时按所选 GPU 部署：多卡=--split-mode（见「切分模式」）、单卡=--split-mode none、CPU=-ngl 0";
    gpuAllItem=new ToolStripMenuItem("全部（GPU）",null,(s,e)=>SetGpuAll());
    gpuCpuItem=new ToolStripMenuItem("CPU",null,(s,e)=>SetGpuCpu());
    gpuMenu.DropDownItems.Add(gpuAllItem); gpuMenu.DropDownItems.Add(gpuCpuItem);
    if(gpus.Count>0){ gpuMenu.DropDownItems.Add(new ToolStripSeparator());
      foreach(var g in gpus){ int idx=g.Index; var it=new ToolStripMenuItem("GPU"+g.Index+"（PCI "+g.PciBus+"）",null,(s,e)=>ToggleGpu(idx)); gpuItems.Add((it,idx)); gpuMenu.DropDownItems.Add(it); }
    }
    items.Add(gpuMenu);
    // ctx 选择（单选）
    ctxMenu=new ToolStripMenuItem("上下文: 192K");
    ctxMenu.ToolTipText="启动服务时应用到 llama-server -c（KV 显存随 ctx 增大）";
    string[] ctxLabels={"8K","16K","32K","64K","128K","192K","256K"};
    for(int i=0;i<LaunchArgs.CtxOptions.Length;i++){ int v=LaunchArgs.CtxOptions[i]; var it=new ToolStripMenuItem(ctxLabels[i],null,(s,e)=>{ ctxVal=v; RefreshChecks(); SaveCfg(); }); it.CheckOnClick=false; ctxItems.Add(it); ctxMenu.DropDownItems.Add(it); }
    items.Add(ctxMenu);
    // KV 缓存类型（单选）：默认 / q8_0(8bit 省显存) / f16(16bit 更保真更吃显存)
    kvMenu=new ToolStripMenuItem("KV 缓存: "+KvLabel(kvMode));
    kvMenu.ToolTipText="KV cache 量化类型（GPU 模式生效，重启服务后应用）：默认=llama 自身默认(f16)；8bit q8_0=省显存(显存省~40%)；16bit f16=更保真。q8_0 依赖 flash-attn(托盘固定开启)；f16 显存占用大、192K 双卡下崩溃概率更高";
    kvDefItem=new ToolStripMenuItem("默认（llama f16）",null,(s,e)=>SetKv(0));
    kv8Item=new ToolStripMenuItem("8bit q8_0（省显存，推荐）",null,(s,e)=>SetKv(1));
    kv16Item=new ToolStripMenuItem("16bit f16（保真）",null,(s,e)=>SetKv(2));
    kvDefItem.CheckOnClick=false; kv8Item.CheckOnClick=false; kv16Item.CheckOnClick=false;
    kvMenu.DropDownItems.AddRange(new ToolStripItem[]{kvDefItem,kv8Item,kv16Item});
    items.Add(kvMenu);
    // 缓存内存（单选）：llama-server -cram/--cache-ram，prompt/前缀缓存占系统内存上限（MiB），0=禁用、-1=无限制；重启服务后生效
    cacheRamMenu=new ToolStripMenuItem("缓存内存: "+CacheRamLabel(cacheRam));
    cacheRamMenu.ToolTipText="启动服务时应用到 llama-server --cache-ram（prompt/前缀缓存占系统内存上限）：512M~4GB=上限值、无限制=-1（不限）、禁用=0（关缓存）。重启对应服务后生效。";
    string[] cacheRamLabels={"512M","1GB","2GB","4GB","无限制","禁用"};
    for(int i=0;i<LaunchArgs.CacheRamOptions.Length;i++){ int v=LaunchArgs.CacheRamOptions[i]; var it=new ToolStripMenuItem(cacheRamLabels[i],null,(s,e)=>{ cacheRam=v; RefreshChecks(); SaveCfg(); }); it.CheckOnClick=false; cacheRamItems.Add(it); cacheRamMenu.DropDownItems.Add(it); }
    items.Add(cacheRamMenu);
    // 多卡切分模式（单选）：张量并行 tensor（推荐，内置 AllReduce，稳定）/ 按层切分 layer（不推荐，双卡偶发崩）
    splitMenu=new ToolStripMenuItem("切分模式："+(splitMode==0?"按层切分":"张量并行"));
    splitMenu.ToolTipText="多卡部署时应用的 --split-mode：张量并行 tensor=推荐（内置 GGML_CUDA_ALLREDUCE=internal，稳定）；按层切分 layer=不推荐（双卡偶发崩）";
    splitLayerItem=new ToolStripMenuItem("按层切分 layer（不推荐，双卡偶发崩）",null,(s,e)=>SetSplit(0));
    splitRowItem=new ToolStripMenuItem("张量并行 tensor（推荐，内置 AllReduce）",null,(s,e)=>SetSplit(1));
    splitLayerItem.CheckOnClick=false; splitRowItem.CheckOnClick=false;
    splitMenu.DropDownItems.AddRange(new ToolStripItem[]{splitLayerItem,splitRowItem});
    // 张量并行比例滑块（GPU1 占比%）：仅张量并行生效，拖拽=调整 -ts，重启对应服务后应用
    splitMenu.DropDownItems.Add(new ToolStripSeparator());
    var tsLabel=new ToolStripMenuItem("GPU1 占比: "+tsGpu1+"%（张量并行）"){Enabled=false};
    splitMenu.DropDownItems.Add(tsLabel);
    var tsBar=new TrackBar{Minimum=10,Maximum=90,Value=tsGpu1,TickFrequency=10,SmallChange=5,LargeChange=10,Width=180,Height=26,AutoSize=false};
    tsBar.ValueChanged+=(s,e)=>{ tsGpu1=tsBar.Value; tsLabel.Text="GPU1 占比: "+tsGpu1+"%（张量并行）"; SaveCfg(); };
    splitMenu.DropDownItems.Add(new ToolStripControlHost(tsBar));
    items.Add(splitMenu);
    // 模型监听地址（复选）：勾选=--host 0.0.0.0 局域网可访问（默认）；取消=--host 127.0.0.1 仅本机（下次启动服务生效）
    bindItem=new ToolStripMenuItem("模型监听 0.0.0.0（局域网可访问）",null,(s,e)=>ToggleBind());
    bindItem.ToolTipText="勾选后启动/重启模型服务时绑定所有网卡（--host 0.0.0.0，局域网设备可直接访问模型端口 808x）；取消勾选则仅本机可访问（--host 127.0.0.1）。下次启动服务生效。";
    bindItem.CheckOnClick=false;
    items.Add(bindItem);
    // 复选/单选二级菜单：点击后不隐藏（点外部/ESC 才关闭）
    KeepOpen(pm.DropDown); KeepOpen(gpuMenu.DropDown); KeepOpen(ctxMenu.DropDown); KeepOpen(splitMenu.DropDown); KeepOpen(kvMenu.DropDown); KeepOpen(cacheRamMenu.DropDown);
    RefreshChecks();
    RefreshDshUi();
    items.Add(new ToolStripSeparator());
    var m7=new ToolStripMenuItem("查看日志",null,(s,e)=>ShowLogWin());
    var openCfg=new ToolStripMenuItem("打开配置文件",null,(s,e)=>{ try{ Process.Start(new ProcessStartInfo(Config.Path_){UseShellExecute=true}); }catch{} });
    items.Add(m7); items.Add(openCfg); items.Add(new ToolStripSeparator());
    autoStartItem=new ToolStripMenuItem("开机自启动",null,(s,e)=>ToggleAutoStart(autoStartItem)){CheckOnClick=true, Checked=AutoStart.Enabled()};
    autoStartItem.ToolTipText="在用户启动文件夹创建快捷方式，登录 Windows 自动运行 DSH托盘";
    items.Add(autoStartItem);
    var m8r=new ToolStripMenuItem("重启托盘",null,(s,e)=>RestartTray());
    items.Add(m8r);
    var m8=new ToolStripMenuItem("退出",null,(s,e)=>ExitApp());
    items.Add(m8);
    menu.Items.AddRange(items.ToArray());
    // 右键菜单：NotifyIcon 内置弹出（位置由系统提供，规避手动 Show 在本机 DPI 下的坐标越界）。
    // 必须配 Main 里 SetHighDpiMode(PerMonitorV2)；约束：菜单显示期间绝不重建 DropDownItems（RebuildRecentMenu 有守卫）。
    icon.ContextMenuStrip=menu;
    icon.DoubleClick+=(s,e)=>OpenPop();
    icon.MouseUp+=(s,e)=>{ if(e.Button!=MouseButtons.Right) return;
      if(menu==null) return;
      if(menu.Visible){ // 幽灵菜单防御：Visible=true 但不在任何工作屏（曾被弹到不可见位置且未关）→ 清掉，让内置 Show 下次弹到系统位置
        var b=menu.Bounds; bool onScreen=false;
        try{ foreach(var sc in Screen.AllScreens) if(sc.WorkingArea.IntersectsWith(b)){ onScreen=true; break; } }catch{}
        if(!onScreen&&b.Width>0&&b.Height>0){ try{ menu.Close(); }catch{} try{ File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tray-ex.log"),DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" GHOSTCLR "+b+"\r\n"); }catch{} }
      }
    };
    recentRefreshMs=Environment.TickCount-54000; // 首次采集错峰：启动约 6s 后，避开图标注册/explorer 拥挤（启动瞬间右键必须即时可用）
    if(dumpMode){ try{ recentList=ReadRecent(); RebuildRecentMenu(); }catch{} } // 诊断模式同步填充，供 --dump-menu 核对
    else{ menu.Closed+=(s,e)=>RebuildRecentMenu(); } // 采集完全交给 Tick 低频；Closed 仅内存快照重建（无 IO）
    // 通知区冷启动右键修复（A/B 证实必要：去掉后重启托盘右键须等很久才有反应）：
    // explorer 对刚 NIM_ADD 的图标建立右键路由很慢 → 启动 ~2.5s 重注册一次（delete+add）强制绑定，右键随即可用。
    if(!dumpMode){ var rr=new System.Windows.Forms.Timer(); rr.Interval=2500; rr.Tick+=(s,e)=>{ rr.Stop(); rr.Dispose(); try{ icon.Visible=false; icon.Visible=true; }catch{} }; rr.Start(); }
    clock=new System.Windows.Forms.Timer(); clock.Interval=1000; clock.Tick+=(s,e)=>Tick(); clock.Start();
  }

  string Cfg(){ return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dsh-tray.cfg"); }
  void LoadCfg(){
    try{ if(File.Exists(Cfg())) foreach(var l in File.ReadAllLines(Cfg())){
      if(l.StartsWith("paramMode=")){ int m; if(int.TryParse(l.Substring(10),out m)&&m>=0&&m<=2) paramMode=m; }
      else if(l.StartsWith("gpu=")) gpuSel=GpuSelection.FromCfg(l.Substring(4));
      else if(l.StartsWith("ctx=")){ int v; if(int.TryParse(l.Substring(4),out v)&&Array.IndexOf(LaunchArgs.CtxOptions,v)>=0) ctxVal=v; }
      else if(l.StartsWith("split=")){ string s2=l.Substring(6).Trim().ToLowerInvariant(); splitMode = (s2=="tensor"||s2=="row") ? 1 : 0; } // row=旧值兼容
      else if(l.StartsWith("tsGpu1=")){ int v; if(int.TryParse(l.Substring(7),out v)&&v>=0&&v<=100) tsGpu1=v; }
      else if(l.StartsWith("kv=")){ int m; if(int.TryParse(l.Substring(3),out m)&&m>=0&&m<=2) kvMode=m; }
      else if(l.StartsWith("cacheRam=")){ int v; if(int.TryParse(l.Substring(9),out v)&&Array.IndexOf(LaunchArgs.CacheRamOptions,v)>=0) cacheRam=v; }
      else if(l.StartsWith("bind=")){ string s2=l.Substring(5).Trim().ToLowerInvariant(); bindAll = (s2!="127.0.0.1" && s2!="local" && s2!="0"); } // 缺省/未知一律按 0.0.0.0（兼容旧 cfg 无 bind 键）
      else if(l.StartsWith("popW=")){ int v; if(int.TryParse(l.Substring(5),out v)&&v>=400&&v<=6000) _popW=v; }
      else if(l.StartsWith("popH=")){ int v; if(int.TryParse(l.Substring(5),out v)&&v>=300&&v<=6000) _popH=v; }
      else if(l.StartsWith("offW=")){ int v; if(int.TryParse(l.Substring(5),out v)&&v>=400&&v<=6000) _offW=v; }
      else if(l.StartsWith("offH=")){ int v; if(int.TryParse(l.Substring(5),out v)&&v>=300&&v<=6000) _offH=v; }
      else if(l.StartsWith("popX=")){ int v; if(int.TryParse(l.Substring(5),out v)) _popX=v; }
      else if(l.StartsWith("popY=")){ int v; if(int.TryParse(l.Substring(5),out v)) _popY=v; }
      else if(l.StartsWith("offX=")){ int v; if(int.TryParse(l.Substring(5),out v)) _offX=v; }
      else if(l.StartsWith("offY=")){ int v; if(int.TryParse(l.Substring(5),out v)) _offY=v; }
    } }catch{}
  }
  void SaveCfg(){ try{ File.WriteAllText(Cfg(),"paramMode="+paramMode+"\r\ngpu="+gpuSel.CfgString()+"\r\nctx="+ctxVal+"\r\nsplit="+(splitMode==0?"layer":"tensor")+"\r\ntsGpu1="+tsGpu1+"\r\nkv="+kvMode+"\r\ncacheRam="+cacheRam+"\r\nbind="+(bindAll?"0.0.0.0":"127.0.0.1")+"\r\npopX="+_popX+"\r\npopY="+_popY+"\r\npopW="+_popW+"\r\npopH="+_popH+"\r\noffX="+_offX+"\r\noffY="+_offY+"\r\noffW="+_offW+"\r\noffH="+_offH+"\r\n"); }catch{} }
  // 弹窗位置/大小持久化：Move/ResizeEnd 都触发，防抖 600ms 写盘一次
  void QueueSavePos(){ _posSaveAct=SaveAllPos; if(_posSaveTimer==null){ _posSaveTimer=new System.Windows.Forms.Timer(); _posSaveTimer.Interval=600; _posSaveTimer.Tick+=(s,e)=>{ _posSaveTimer.Stop(); var a=_posSaveAct; _posSaveAct=null; if(a!=null) try{ a(); }catch{} }; } _posSaveTimer.Stop(); _posSaveTimer.Start(); }
  void SaveAllPos(){
    try{
      if(popup!=null&&!popup.IsDisposed&&popup.WindowState==FormWindowState.Normal){ _popX=popup.Location.X; _popY=popup.Location.Y; _popW=popup.Width; _popH=popup.Height; }
      if(officialForm!=null&&!officialForm.IsDisposed&&officialForm.WindowState==FormWindowState.Normal){ _offX=officialForm.Location.X; _offY=officialForm.Location.Y; _offW=officialForm.Width; _offH=officialForm.Height; }
      SaveCfg();
    }catch{}
  }
  bool PosOnScreen(Point p){ try{ foreach(var sc in Screen.AllScreens) if(sc.WorkingArea.Contains(p)) return true; }catch{} return false; }
  void ToggleAutoStart(ToolStripMenuItem it){ AutoStart.Set(it.Checked); it.Checked=AutoStart.Enabled(); }
  void SetParam(int m){ paramMode=m; RefreshChecks(); SaveCfg(); string label=m==0?"通用思考 (temp1.0/pres1.5)":m==1?"编码思考 (temp0.6/pres0.0)":"Instruct (temp0.7/pres1.5)"; logForm.Append("推理参数组: "+label+"（重启对应服务后生效）\r\n"); }
  void SetSplit(int m){ splitMode=m; RefreshChecks(); SaveCfg(); logForm.Append("切分模式: "+(m==0?"按层切分 layer":"张量并行 tensor")+"（重启对应服务后生效）\r\n"); }
  void SetKv(int m){ kvMode=m; RefreshChecks(); SaveCfg(); logForm.Append("KV 缓存: "+KvLabel(m)+"（重启对应服务后生效）\r\n"); }
  static string KvLabel(int m){ return m==0?"默认":m==1?"8bit q8_0":"16bit f16"; }
  static string CacheRamLabel(int mb){ return mb==0?"禁用":mb<0?"无限制":(mb>=1024&&mb%1024==0)?(mb/1024)+"GB":mb+"M"; }
  void ToggleBind(){ bindAll=!bindAll; RefreshChecks(); SaveCfg(); logForm.Append("模型监听: "+(bindAll?"0.0.0.0（局域网可访问）":"127.0.0.1（仅本机）")+"（重启对应服务后生效）\r\n"); }
  static void KeepOpen(ToolStripDropDown dd){ dd.Closing += (s,e)=>{ if(e.CloseReason==ToolStripDropDownCloseReason.ItemClicked) e.Cancel=true; }; }
  void SetGpuAll(){ gpuSel.UseAll=true; gpuSel.UseCpu=false; gpuSel.Indices.Clear(); RefreshChecks(); SaveCfg(); }
  void SetGpuCpu(){ gpuSel.UseAll=false; gpuSel.UseCpu=true; gpuSel.Indices.Clear(); RefreshChecks(); SaveCfg(); }
  void ToggleGpu(int idx){
    gpuSel.UseCpu=false; gpuSel.UseAll=false;
    if(gpuSel.Indices.Contains(idx)) gpuSel.Indices.Remove(idx); else gpuSel.Indices.Add(idx);
    if(gpuSel.Indices.Count==0) gpuSel.UseAll=true; // 全不选 → 全部（GPU）
    RefreshChecks(); SaveCfg();
  }
  void RefreshChecks(){
    if(gpuAllItem!=null) gpuAllItem.Checked = gpuSel.UseAll && !gpuSel.UseCpu;
    if(gpuCpuItem!=null) gpuCpuItem.Checked = gpuSel.UseCpu;
    foreach(var t in gpuItems) t.item.Checked = !gpuSel.UseCpu && !gpuSel.UseAll && gpuSel.Indices.Contains(t.idx);
    for(int i=0;i<ctxItems.Count;i++) ctxItems[i].Checked = (LaunchArgs.CtxOptions[i]==ctxVal);
    if(pm0!=null) pm0.Checked=(paramMode==0); if(pm1!=null) pm1.Checked=(paramMode==1); if(pm2!=null) pm2.Checked=(paramMode==2);
    if(splitLayerItem!=null) splitLayerItem.Checked=(splitMode==0);
    if(splitRowItem!=null) splitRowItem.Checked=(splitMode==1);
    if(kvDefItem!=null) kvDefItem.Checked=(kvMode==0);
    if(kv8Item!=null) kv8Item.Checked=(kvMode==1);
    if(kv16Item!=null) kv16Item.Checked=(kvMode==2);
    for(int i=0;i<cacheRamItems.Count;i++) cacheRamItems[i].Checked=(LaunchArgs.CacheRamOptions[i]==cacheRam);
    if(bindItem!=null) bindItem.Checked=bindAll;
    if(gpuMenu!=null) gpuMenu.Text="GPU: "+gpuSel.ShortLabel();
    if(ctxMenu!=null) ctxMenu.Text="上下文: "+(ctxVal/1024)+"K";
    if(pm!=null) pm.Text="推理参数组："+ParamLabel(paramMode);
    if(splitMenu!=null) splitMenu.Text="切分模式："+(splitMode==0?"按层切分":"张量并行("+tsGpu1+"%)");
    if(kvMenu!=null) kvMenu.Text="KV 缓存: "+KvLabel(kvMode);
    if(cacheRamMenu!=null) cacheRamMenu.Text="缓存内存: "+CacheRamLabel(cacheRam);
  }
  static string ParamLabel(int m){ return m==0?"通用思考":m==1?"编码思考":"Instruct"; }

  bool PortUp(int port){ try{ using(var wc=new System.Net.WebClient()){ wc.DownloadString("http://127.0.0.1:"+port+"/health"); return true; } }catch{} return false; }
  void Start(Service svc){
    if(svc.Running){ Log(svc,svc.Name+" 已在运行 (端口 "+svc.Port+")\r\n"); return; }
    if(PortUp(svc.Port)){ Log(svc,"端口 "+svc.Port+" 已有服务在跑（非本应用启动），请先停用外部进程。\r\n"); return; }
    var build=LaunchArgs.Build(svc,gpuSel,ctxVal,paramMode,splitMode,kvMode,cacheRam,tsGpu1,gpus.Count,bindAll);
    var psi=new ProcessStartInfo(cfg.LlamaServerExe,string.Join(" ",build.args)){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
    if(build.envCuda.Length>0) psi.Environment["CUDA_VISIBLE_DEVICES"]=build.envCuda;
    if(build.envAllreduce.Length>0) psi.Environment["GGML_CUDA_ALLREDUCE"]=build.envAllreduce; // 多卡张量并行需要内置 CUDA AllReduce
    try{
      svc.proc=Process.Start(psi); svc.proc.OutputDataReceived+=(o,e)=>{if(e.Data!=null)svc.log.AppendLine(e.Data);}; svc.proc.ErrorDataReceived+=(o,e)=>{if(e.Data!=null)svc.log.AppendLine("[err] "+e.Data);}; svc.proc.BeginOutputReadLine(); svc.proc.BeginErrorReadLine();
      Log(svc,">>> 启动 "+svc.Name+" (端口 "+svc.Port+")\r\n");
      Log(svc,"    GPU="+gpuSel.Describe(gpus)+" | ctx="+(ctxVal/1024)+"K | CUDA_VISIBLE_DEVICES="+(build.envCuda.Length>0?build.envCuda:"-")+" | 参数组="+(paramMode==0?"通用思考":paramMode==1?"编码思考":"Instruct")+" | 切分="+(splitMode==0?"按层 layer":splitMode==1?"张量并行 tensor("+tsGpu1+"%)":"-")+" | KV="+KvLabel(kvMode)+" | 缓存内存="+CacheRamLabel(cacheRam)+" | MTP="+(svc.SpecDecode?"on":"off")+" | 监听="+(bindAll?"0.0.0.0":"127.0.0.1")+"\r\n");
      if(gpuSel.UseCpu) Log(svc,"    注意: CPU 模式（-ngl 0），速度会显著变慢\r\n");
      else if(ctxVal>=196608 && LaunchArgs.EffectiveGpus(gpuSel,gpus.Count).Count==1 && (svc.Model.Contains("35B")||svc.Model.Contains("27B"))) Log(svc,"    注意: "+(ctxVal/1024)+"K 上下文 + 单 GPU 可能显存不足（OOM），建议 GPU=全部（多卡，切分模式=按层 layer）或降低 ctx\r\n");
      Log(svc,"    首次加载约 30-60s\r\n");
      string cmdLine = cfg.LlamaServerExe + " " + string.Join(" ", build.args) + (build.envCuda.Length>0?" [CUDA_VISIBLE_DEVICES="+build.envCuda+"]":"") + (build.envAllreduce.Length>0?" [GGML_CUDA_ALLREDUCE="+build.envAllreduce+"]":"");
      Log(svc,"    命令: "+cmdLine+"\r\n");
      try{ File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"model-start.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" ["+svc.Name+"] "+cmdLine+"\r\n"); }catch{}
    }
    catch(Exception ex){ Log(svc,">>> 启动 "+svc.Name+" 失败: "+ex.Message+"\r\n"); }
  }

  void Stop(Service svc){ if(svc.proc!=null&&!svc.proc.HasExited){ Log(svc,">>> 停止 "+svc.Name+" ...\r\n"); try{svc.proc.Kill();}catch{} svc.proc=null; } else Log(svc,svc.Name+" 未运行.\r\n"); }
  void StopAll(){ foreach(var s in services) Stop(s); }
  void RestartAll(){ StopAll(); foreach(var s in services) Start(s); }

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
    string outLog=string.IsNullOrEmpty(cfg.DshOutLog)?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dsh-web-out.log"):cfg.DshOutLog;
    string errLog=string.IsNullOrEmpty(cfg.DshErrLog)?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dsh-web-err.log"):cfg.DshErrLog;
    dshState=1; dshStartMs=Environment.TickCount; dshTimeoutLogged=false; Ui(()=>RefreshDshUi());
    try{
      var psi=new ProcessStartInfo(cfg.DshNodeExe){ WorkingDirectory=cfg.DshWorkDir, UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden, RedirectStandardOutput=true, RedirectStandardError=true, Arguments="\""+cfg.DshCliBinJs+"\" web" };
      var p=Process.Start(psi);
      if(p==null){ logForm.Append(">>> 启动 DSH 失败（Process.Start 返回 null）\r\n"); dshState=0; Ui(()=>RefreshDshUi()); return; }
      p.OutputDataReceived+=(o,e)=>{ if(!string.IsNullOrEmpty(e.Data)){ try{ File.AppendAllText(outLog,e.Data+Environment.NewLine); }catch{} } };
      p.ErrorDataReceived+=(o,e)=>{ if(!string.IsNullOrEmpty(e.Data)){ try{ File.AppendAllText(errLog,e.Data+Environment.NewLine); }catch{} } };
      p.BeginOutputReadLine(); p.BeginErrorReadLine();
      logForm.Append(">>> 启动 DSH：\""+cfg.DshNodeExe+"\" \""+cfg.DshCliBinJs+"\" web（工作目录 "+cfg.DshWorkDir+"），等待端口 3080 就绪 ...\r\n");
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
  // 后台执行（菜单点启停/重启不阻塞 UI 线程）；UI 更新一律经 Ui() 回 UI 线程
  void Bg(Action a){ try{ Task.Run(a); }catch{} }
  void Ui(Action a){ try{ if(logForm!=null&&logForm.InvokeRequired){ logForm.BeginInvoke(a); } else a(); }catch{ try{a();}catch{} } }
  void ShowLogWin(){ try{ if(logForm==null||logForm.IsDisposed) logForm=new LogForm(); logForm.Show(); if(logForm.WindowState==FormWindowState.Minimized) logForm.WindowState=FormWindowState.Normal; logForm.BringToFront(); logForm.Activate(); }catch(Exception ex){ MessageBox.Show("打开日志窗口失败: "+ex.Message); } }
  void OpenDir(string path,string label){ try{ if(Directory.Exists(path)){ Process.Start(new ProcessStartInfo(path){UseShellExecute=true}); } else logForm.Append(label+" 目录不存在："+path+"\r\n"); }catch(Exception ex){ logForm.Append("打开 "+label+" 失败: "+ex.Message+"\r\n"); } }
  string DshProgDir(){
    if(!string.IsNullOrEmpty(cfg.DshWorkDir)&&Directory.Exists(cfg.DshWorkDir)) return cfg.DshWorkDir;
    if(!string.IsNullOrEmpty(cfg.DshCliBinJs)){ try{ var f=new FileInfo(cfg.DshCliBinJs); var d=f.Directory?.Parent?.Parent?.Parent; if(d!=null&&d.Exists) return d.FullName; }catch{} }
    return AppDomain.CurrentDomain.BaseDirectory;
  }
  string DshHomeDir(){ if(!string.IsNullOrEmpty(cfg.DshHomeDir)) return cfg.DshHomeDir; return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"); }
  string DshOutPath(){ return string.IsNullOrEmpty(cfg.DshOutLog)?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dsh-web-out.log"):cfg.DshOutLog; }
  string DshErrPath(){ return string.IsNullOrEmpty(cfg.DshErrLog)?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dsh-web-err.log"):cfg.DshErrLog; }
  public void OpenDshLog(){
    if(dshLogForm==null||dshLogForm.IsDisposed){ dshLogForm=new LogForm("DSH 日志"); dshLogForm.Size=new Size(760,520); }
    dshLogForm.Show(); if(dshLogForm.WindowState==FormWindowState.Minimized) dshLogForm.WindowState=FormWindowState.Normal; dshLogForm.BringToFront(); dshLogForm.Activate();
    lastOutPos=0; lastErrPos=0;
    if(!File.Exists(DshOutPath())&&!File.Exists(DshErrPath())){
      dshLogForm.Append("（暂无 DSH 日志文件）\r\n输出日志: "+DshOutPath()+"\r\n错误日志: "+DshErrPath()+"\r\n提示：DSH 由托盘启动/重启后会自动写入上述文件；当前 DSH 若由外部（控制台/脚本）启动，日志在它的启动方式里。\r\n");
      return;
    }
    DshTailLoad(true);
  }
  void TailLog(string path,string label,ref long pos,bool initial,LogForm win){
    try{
      var fi=new FileInfo(path); if(!fi.Exists) return;
      long len=fi.Length; long start = initial ? Math.Max(0,len-150000) : pos;
      if(len<=start) return;
      using(var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){
        fs.Seek(start,SeekOrigin.Begin); var buf=new byte[len-start]; int rd=fs.Read(buf,0,buf.Length);
        string txt=System.Text.Encoding.UTF8.GetString(buf,0,rd);
        if(!string.IsNullOrEmpty(txt)) win.Append("── "+label+" ──\r\n"+txt+(txt.EndsWith("\n")?"":"\r\n"));
      }
      pos=len;
    }catch{}
  }
  void DshTailLoad(bool initial){
    if(dshLogForm==null||dshLogForm.IsDisposed) return;
    TailLog(DshOutPath(),"dsh-web-out.log",ref lastOutPos,initial,dshLogForm);
    TailLog(DshErrPath(),"dsh-web-err.log",ref lastErrPos,initial,dshLogForm);
  }
  public string DshLogSnapshot(){ if(dshLogForm==null||dshLogForm.IsDisposed) return "NO WINDOW"; try{ string t=dshLogForm.box.Text; return "visible="+dshLogForm.Visible+" chars="+t.Length+"\r\n"+t.Substring(0,Math.Min(500,t.Length)); }catch(Exception ex){ return "snapshot err: "+ex.Message; } }
  void Log(Service svc,string s){ svc.log.Append(s); }
  string Short(Service s){ int i=s.Name.IndexOf(" ("); return i>0?s.Name.Substring(0,i):s.Name; }

  Icon WhaleIcon(){ try{ var p=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"whale-girl.ico"); if(File.Exists(p)) return new Icon(p); }catch{} return SystemIcons.Application; }

  string ExtractBlock(string y){ int i=y.IndexOf("agent-default-model:"); if(i<0)return null; int h=y.IndexOf("\n",i); if(h<0)h=i; int be=y.Length; for(int id=h+1; id<y.Length; id++){ if(y[id]=='\n' && id+1<y.Length && y[id+1]!=' '){ be=id+1; break; } } return y.Substring(i, be-i); }
  string ReplaceBlock(string y, string block){ int i=y.IndexOf("agent-default-model:"); if(i<0)return y; int h=y.IndexOf("\n",i); if(h<0)h=i; int be=y.Length; for(int id=h+1; id<y.Length; id++){ if(y[id]=='\n' && id+1<y.Length && y[id+1]!=' '){ be=id+1; break; } } return y.Substring(0,i)+block+y.Substring(be); }
  void OpenSession(Service svc){
    string orig=null; string yp=cfg.SettingsYamlPath;
    try{ string y=File.ReadAllText(yp); orig=ExtractBlock(y);
      string prov=string.IsNullOrEmpty(svc.Provider)? "llama-local" : svc.Provider;
      File.WriteAllText(yp, ReplaceBlock(y, "agent-default-model:\r\n  provider: "+prov+"\r\n  model: '"+svc.Model+"'\r\n"));
    }catch{}
    OpenPop();
    if(orig!=null){ var rt=new System.Windows.Forms.Timer(); rt.Interval=8000; rt.Tick+=(s,e)=>{ try{ File.WriteAllText(yp, ReplaceBlock(File.ReadAllText(yp), orig)); }catch{} rt.Stop(); rt.Dispose(); }; rt.Start(); }
  }
  void OpenPop(){ EnsurePopupNav(cfg.DshUrl,false); }
  void OpenSessionById(string sessionId){ try{ EnsurePopupNav(BuildSessionUrl(sessionId),true); }catch(Exception ex){ MessageBox.Show("打开会话失败: "+ex.Message); } }
  string BuildSessionUrl(string sessionId){ var ub=new UriBuilder(cfg.DshUrl); ub.Query="session="+Uri.EscapeDataString(sessionId); return ub.Uri.ToString(); }
  string TokenUrl(){ var ub=new UriBuilder(cfg.DshUrl); ub.Query="token="+Uri.EscapeDataString(cfg.DshWebToken??""); return ub.Uri.ToString(); }
  // 弹窗导航：dsh web 需要登录。首次/登录态未知且已配置 DshWebToken 时，先导航 token URL 种 cookie，
  // 完成后由 NavigationCompleted 续跳到真正目标，避免带 ?session 裸跳遇 401。
  void EnsurePopupNav(string url,bool force){
    bool created=false;
    if(popup==null || popup.IsDisposed){
      created=true;
      popup=new Form{Text="DSH托盘",Icon=WhaleIcon(),FormBorderStyle=FormBorderStyle.SizableToolWindow,StartPosition=FormStartPosition.CenterScreen,Size=new Size(_popW,_popH),ShowInTaskbar=false,MinimizeBox=true};
      if(_popX!=int.MinValue&&PosOnScreen(new Point(_popX+_popW/2,_popY+20))){ popup.StartPosition=FormStartPosition.Manual; popup.Location=new Point(_popX,_popY); } // 记住上次位置；已移出屏幕则居中
      popup.FormClosing+=(s,e)=>{ if(e.CloseReason==CloseReason.UserClosing){ e.Cancel=true; popup.Hide(); } };
      popup.ResizeEnd+=(s,e)=>QueueSavePos();
      popup.Move+=(s,e)=>QueueSavePos();
      wv=new WebView2{Dock=DockStyle.Fill}; popup.Controls.Add(wv);
      wv.NavigationCompleted+=(s,e)=>{ if(!string.IsNullOrEmpty(_pendingAfterLogin)){ var u=_pendingAfterLogin; _pendingAfterLogin=""; try{ wv.Source=new Uri(u); }catch{} } };
    }
    popup.Show(); popup.Activate(); if(popup.WindowState==FormWindowState.Minimized) popup.WindowState=FormWindowState.Normal;
    bool hasToken=!string.IsNullOrEmpty(cfg.DshWebToken);
    bool needLogin=(created||force)&&hasToken&&!_popupLogged;
    if(needLogin){ _pendingAfterLogin=url; try{ wv.Source=new Uri(TokenUrl()); }catch{} }
    else if(created||force){ try{ wv.Source=new Uri(url); }catch{} }
    if(hasToken) _popupLogged=true;
  }
  // —— 最近会话：读 .dsh/storages 明文投影（免解压 zstd），按 max(createdAt,lastPromptAt) 倒序、排除归档取前 10 ——
  void ScheduleRecentRefresh(){
    if(Interlocked.Exchange(ref recentBusy,1)!=0) return;
    Task.Run(()=>{
      try{ var list=ReadRecent(); Ui(()=>{ recentList=list; RebuildRecentMenu(); }); }
      catch{} finally{ Interlocked.Exchange(ref recentBusy,0); }
    });
  }
  string StoragesDir(){ return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "storages"); }
  List<RecentSession> ReadRecent(){
    var found=new List<RecentSession>();
    try{
      string dir=StoragesDir();
      // 归档集合：workspace.json 实时缓存（mtime 未变不重解析）
      var archived=_archCache;
      try{ string wsPath=Path.Combine(dir,"workspace.json"); if(File.Exists(wsPath)){
          long mt=File.GetLastWriteTimeUtc(wsPath).Ticks;
          if(mt!=_wsMt){ _wsMt=mt; _archCache=new HashSet<string>();
            using(var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(wsPath))){ var r=doc.RootElement;
              if(r.TryGetProperty("global",out var g)&&g.TryGetProperty("archivedSessionIds",out var a)) foreach(var x in a.EnumerateArray()){ var s=x.GetString(); if(s!=null) _archCache.Add(s); } } }
          archived=_archCache; } }catch{}
      // 权威会话源：storages/session_projcache/sessions/<id>.json（per-session 实时投影，含标题与最后消息时间）。
      // 文件 mtime 未变则不重读（只 stat），避免每轮全读触发防病毒扫描拖慢系统。
      string pdir=Path.Combine(dir,"session_projcache","sessions");
      if(Directory.Exists(pdir)){
        var cur=new HashSet<string>();
        foreach(var f in Directory.EnumerateFiles(pdir,"*.json")){
          string id=Path.GetFileNameWithoutExtension(f); cur.Add(id);
          long mt=File.GetLastWriteTimeUtc(f).Ticks;
          if(!_sessCache.TryGetValue(id,out var c)||c.mt!=mt) _sessCache[id]=ReadProjRecord(f,mt);
        }
        if(_sessCache.Count!=cur.Count){ var dead=new List<string>(); foreach(var k in _sessCache.Keys) if(!cur.Contains(k)) dead.Add(k); foreach(var k in dead) _sessCache.Remove(k); }
        foreach(var kv in _sessCache){
          if(archived.Contains(kv.Key)) continue;
          var c=kv.Value;
          if(c.sub) continue; // 子代理会话（并行 agent 任务）不进「最近会话」直达
          found.Add(new RecentSession{Id=kv.Key,Title=c.title,Cwd=c.cwd,Last=Math.Max(c.last,c.created)});
        }
      }
      found.Sort((a,b)=>b.Last.CompareTo(a.Last));
      if(found.Count>10) found=found.GetRange(0,10);
    }catch{}
    return found;
  }
  // 读单个 per-session 投影：record.identity.{createdAt,cwd} + record.rows.{title.val, sessionListMetadata.val.lastPromptAt}
  // sub=true 当 rows.subagent.val.identity 存在 → 该会话是并行子代理会话（agent 批量任务），非用户主会话，最近会话列表排除
  (long mt,string title,long last,long created,string cwd,bool sub) ReadProjRecord(string f,long mt){
    string title=""; long last=0,created=0; string cwd=""; bool sub=false;
    try{
      using(var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(f))){ var root=doc.RootElement;
        if(!root.TryGetProperty("record",out var rec)) return (mt,title,last,created,cwd,sub);
        if(rec.TryGetProperty("identity",out var idt)){ if(idt.TryGetProperty("createdAt",out var ca)){ try{created=ca.GetInt64();}catch{} } if(idt.TryGetProperty("cwd",out var cw)) cwd=cw.GetString()??""; }
        if(rec.TryGetProperty("rows",out var rows)){
          if(rows.TryGetProperty("title",out var tt)&&tt.TryGetProperty("val",out var tv)&&tv.ValueKind==System.Text.Json.JsonValueKind.String) title=tv.GetString()??"";
          if(rows.TryGetProperty("sessionListMetadata",out var slm)&&slm.TryGetProperty("val",out var sv)&&sv.TryGetProperty("lastPromptAt",out var lp)){ try{last=lp.GetInt64();}catch{} }
          if(rows.TryGetProperty("subagent",out var sa)&&sa.TryGetProperty("val",out var sav)&&sav.ValueKind==System.Text.Json.JsonValueKind.Object&&sav.TryGetProperty("identity",out _)) sub=true;
        }
      }
    }catch{}
    return (mt,title,last,created,cwd,sub);
  }
  // 菜单动态内容唯一重建入口（模式）：读内存快照 recentList，仅菜单不可见时同步重建，零磁盘/零网络。
  // 触发只能来自：采集完成回调 / menu.Closed（都保证菜单不可见）；严禁在 Opening/显示期间调用或做慢 IO。
  void RebuildRecentMenu(){
    if(menu==null||dshMenu==null||recentHeader==null) return;
    if(menu.Visible) return; // 核心守卫：显示中绝不动结构（RemoveAt/Insert 会静默破坏弹出），交给下次 Closed/采集回调
    int idx=dshMenu.DropDownItems.IndexOf(recentHeader); if(idx<0) return;
    while(dshMenu.DropDownItems.Count>idx+1) dshMenu.DropDownItems.RemoveAt(idx+1);
    var list=recentList;
    if(list==null||list.Count==0){ dshMenu.DropDownItems.Insert(idx+1,new ToolStripMenuItem("（暂无未归档会话）"){Enabled=false}); return; }
    for(int i=0;i<list.Count;i++){ var rr=list[i];
      var it=new ToolStripMenuItem(RecentLabel(rr),null,(s,e)=>OpenSessionById(rr.Id));
      it.ToolTipText=(rr.Cwd.Length>0?("工作区: "+rr.Cwd+"\r\n"):"")+"会话: "+rr.Id;
      dshMenu.DropDownItems.Insert(idx+1+i,it);
    }
  }
  string RecentLabel(RecentSession r){
    string t=r.Title; if(string.IsNullOrWhiteSpace(t)){ t="(无标题 "+ShortId(r.Id)+")"; }
    else if(t.Length>26) t=t.Substring(0,26)+"…";
    if(r.Last<=0) return t;
    long diff=Math.Max(0,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-r.Last);
    string rel = diff<60_000? "刚刚" : diff<3_600_000? (diff/60_000)+"分钟前" : diff<86_400_000? (diff/3_600_000)+"小时前" : (diff/86_400_000)+"天前";
    return t+"  ·  "+rel;
  }
  string ShortId(string id){ int i=id.LastIndexOf('-'); return i>=0&&i+1<id.Length? id.Substring(i+1,Math.Min(8,id.Length-i-1)) : id; }
  void OpenOfficial(){
    try{
      if(officialForm==null || officialForm.IsDisposed){
        officialForm=new Form{Text="DeepSeek 官方会话",Icon=WhaleIcon(),FormBorderStyle=FormBorderStyle.SizableToolWindow,StartPosition=FormStartPosition.CenterScreen,Size=new Size(_offW,_offH),ShowInTaskbar=false,MinimizeBox=true};
        if(_offX!=int.MinValue&&PosOnScreen(new Point(_offX+_offW/2,_offY+20))){ officialForm.StartPosition=FormStartPosition.Manual; officialForm.Location=new Point(_offX,_offY); } // 记住上次位置；已移出屏幕则居中
        officialForm.FormClosing+=(s,e)=>{ if(e.CloseReason==CloseReason.UserClosing){ e.Cancel=true; officialForm.Hide(); } };
        officialForm.ResizeEnd+=(s,e)=>QueueSavePos();
        officialForm.Move+=(s,e)=>QueueSavePos();
        string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"webview-userdata","deepseek-chat");
        try{ Directory.CreateDirectory(folder); }catch{}
        officialWv=new WebView2{Dock=DockStyle.Fill, CreationProperties=new CoreWebView2CreationProperties{ UserDataFolder=folder }};
        officialForm.Controls.Add(officialWv);
        officialForm.Shown+=async (s,e)=>{ try{ await officialWv.EnsureCoreWebView2Async(); officialWv.CoreWebView2.Navigate(cfg.OfficialDeepSeekUrl); }catch(Exception ex){ MessageBox.Show("打开官方会话失败: "+ex.Message); } };
      }
      officialForm.Show(); officialForm.Activate(); if(officialForm.WindowState==FormWindowState.Minimized) officialForm.WindowState=FormWindowState.Normal;
    }catch(Exception ex){ MessageBox.Show("打开官方会话失败: "+ex.Message); }
  }

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
    foreach(var si in sessionItems) si.item.Visible = si.svc.Running;
    if(sepSess!=null) sepSess.Visible = services.Any(s=>s.Running);
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
    if(dshLogForm!=null&&dshLogForm.Visible) DshTailLoad(false);
    foreach(var svc in services){ long L=svc.log.Length; if(L>svc.lastLen){ string t=svc.log.ToString((int)svc.lastLen,(int)(L-svc.lastLen)); svc.lastLen=L; logForm.Append("["+svc.Name+"] "+t); } }
  }

  void ExitApp(){ StopAll(); icon.Visible=false; if(popup!=null)popup.Close(); Application.Exit(); } // icon.Visible=false → NIM_DELETE，退出干净不留死图标
  // 优雅退出：外部 --exit 信号（走同一 ExitApp，先注销图标再退出，避免 kill /F 在通知区留下死图标槽）
  public void ExitFromSignal(){ try{ Ui(()=>ExitApp()); }catch{} }
  void RestartTray(){ try{ Process.Start(new ProcessStartInfo(Application.ExecutablePath){ UseShellExecute=true, WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory }); }catch{} ExitApp(); }

  // —— 隐藏自检模式：--dump-menu 打印当前菜单结构（供开发验证，不进正式 UI）——
  public void ProbeOnce(){ dshPortUp = DshUp()?1:0; dshState = dshPortUp==1?2:0; RefreshDshUi(); }
  public void DisposeForDump(){ try{ icon.Visible=false; icon.Dispose(); }catch{} try{ clock.Stop(); }catch{} try{ if(dshLogForm!=null){ dshLogForm.Dispose(); } }catch{} }
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
  public string MenuShowProbe(){
    try{ recentList=ReadRecent(); RebuildRecentMenu(); }catch(Exception ex){ return "APPLY_FAIL "+ex.GetType().Name+": "+ex.Message; }
    try{
      int n=menu.Items.Count;
      menu.Show(new Point(60,60));
      System.Threading.Thread.Sleep(200);
      return "SHOW_OK items="+n+" recent="+recentList.Count;
    }catch(Exception ex){ return "SHOW_FAIL "+ex.GetType().Name+": "+ex.Message; }
  }
}

public class RecentSession { public string Id=""; public string Title=""; public string Cwd=""; public long Last=0; }

public static class Program2 {
  static Mutex? _single; static bool _isPrimary;
  static string ExitSignalName(){ return @"Local\DSH托盘_ExitSignal"; }
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
    bool askExit = args!=null && Array.IndexOf(args,"--exit")>=0;
    // 单实例互斥：开机自启双入口/重复启动时后到者退出；诊断模式（--dump-menu/--selftest/--selftest-menushow）不占锁
    if(!dump && !selftest && !menuProbe){
      if(askExit){ // 优雅退出：通知正在运行的主实例自己注销图标再退出（替代 kill /F，避免通知区死图标槽）
        try{ using(var ev=new EventWaitHandle(false,EventResetMode.AutoReset,ExitSignalName())){ ev.Set(); } }catch{}
        return;
      }
      bool createdNew;
      _single = new Mutex(true, @"Local\DSH托盘_SingleInstance", out createdNew);
      if(!createdNew) return;
      _isPrimary=true;
    }
    var app=new TrayApp(dump||selftest);
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
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-logwin.txt"), app.DshLogSnapshot()); }catch{}
      app.DisposeForDump();
      return;
    }
    if(menuProbe){
      string r=app.MenuShowProbe();
      try{ File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selftest-menushow.txt"), r); }catch{}
      app.DisposeForDump();
      return;
    }
    if(_isPrimary){ // 主实例监听优雅退出信号（--exit / 重启托盘旧实例）
      var exitEv=new EventWaitHandle(false,EventResetMode.AutoReset,ExitSignalName());
      var wt=new System.Threading.Thread(()=>{ exitEv.WaitOne(); try{ app.ExitFromSignal(); }catch{} });
      wt.IsBackground=true; wt.Start();
    }
    Application.Run(app);
  }
}
