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

// S1（2026-09-11）拆自 Program.cs（partial 1/5：字段 + 构造 + 配置读写）：零逻辑改动，仅位移。
public partial class TrayApp : ApplicationContext {
  NotifyIcon icon; ContextMenuStrip menu; LogForm logForm; System.Windows.Forms.Timer clock; bool adopted=false; TaskbarWatcher? _tw;
  UiDispatcher uiDisp;   // S0：全进程唯一的 UI 线程 marshal 锚点（见 UiDispatcher.cs）
  int _reporting;        // ReportEx 防"上报自身再抛"导致递归
  ToolStripMenuItem? itemOpenChat,autoStartItem; ToolStripMenuItem? pm,pm0,pm1,pm2;
  ToolStripMenuItem? gpuMenu,gpuAllItem,gpuCpuItem,ctxMenu; List<(ToolStripMenuItem item,int idx)> gpuItems=new(); List<ToolStripMenuItem> ctxItems=new();
  ToolStripMenuItem? splitMenu,splitLayerItem,splitRowItem;
  ToolStripMenuItem? vram0Label,vram1Label; // 切分模式滑块旁：张量并行时 GPU0/GPU1 两卡显存估算（各 1 位小数，分两行）
  ToolStripMenuItem? kvMenu,kvDefItem,kv8Item,kv16Item;
  ToolStripMenuItem? cacheRamMenu; List<ToolStripMenuItem> cacheRamItems=new();
  ToolStripMenuItem? bindItem; bool bindAll=true; // 模型监听地址：true=0.0.0.0（局域网可访问，默认）；false=127.0.0.1（仅本机）
  ToolStripMenuItem? mtpMenu; List<ToolStripMenuItem> mtpItems=new(); int mtpLevel=0; // MTP 投机解码档位：0=无 1=MTP 2=MTP2 3=MTP3 4=MTP4（--spec-draft-n-max）
  ToolStripMenuItem? dshMenu,dshStatusItem,dshStartItem,dshRestartItem,dshStopItem;
  // —— 内置渲染 / 安全模式 / 插件管理（2026-09-09：官方客户端 bundle 崩溃时仍可续会话）——
  ToolStripMenuItem? builtinRenderItem,safeModeItem;
  bool builtinRender=false,safeMode=false;
  HashSet<string> disabledEntries=new(StringComparer.OrdinalIgnoreCase),disabledDevPlugins=new(StringComparer.OrdinalIgnoreCase);
  DshRpc? dshRpc; ThinChatForm? thinChat; PluginManagerForm? pluginMgr;
  PerfRuntime? perf;   // 配置粒度模型性能日志（2026-09-11）：台账与样本存 ~/.dsh/tools/model-perf/
  volatile int dshState=0; // DSH 服务状态：0=未启动(红) 1=启动中(黄) 2=运行中(绿)
  long dshStartMs=0; bool dshTimeoutLogged=false; Bitmap? dotRed,dotYellow,dotGreen;
  volatile int dshPortUp=-1; int _probeBusy=0;
  long lastOutPos=0,lastErrPos=0;   // DSH out/err 文件跟踪位置（日志窗口统一后只剩一个实例）
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
  AppConfig cfg; List<Service> services=new();
  List<SvcMenu> svcMenus=new();
  bool _keepClick=false; int svcTick=0;                  // 「点击不收起菜单」标志 / 状态巡检节拍
  // —— 最近会话（DSH 二级菜单：最近发消息的未归档会话；后台读 .dsh/storages，点击=弹窗 ?session= 直达）——
  ToolStripMenuItem? recentHeader; List<RecentSession> recentList=new(); int recentBusy=0; long recentRefreshMs=0;
  // —— dshtray-status：dsh host 插件（~/.dsh/dev-plugins/dshtray-status）权威会话状态，经 /dshtray-status/api 轮询 ——
  // 状态码：0=unknown 1=empty(空/未开始) 2=running(执行中) 3=ask(阻塞·询问) 4=permission(阻塞·权限)
  //        5=done(已完成) 6=stopped(手动停止) 7=aborted(异常中断·非手动) 8=error(异常中断·错误) 9=interrupted(异常中断·崩溃/遗留) 10=no-end(未收尾)
  const int SST_UNKNOWN=0,SST_EMPTY=1,SST_RUNNING=2,SST_ASK=3,SST_PERM=4,SST_DONE=5,SST_STOPPED=6,SST_ABORTED=7,SST_ERROR=8,SST_INTERRUPTED=9,SST_NOEND=10;
  volatile Dictionary<string,int> _sessState=new(); volatile Dictionary<string,string> _sessReason=new();
  static readonly HttpClient _http = new HttpClient{ Timeout=TimeSpan.FromSeconds(5) };
  Dictionary<string,System.Drawing.Bitmap> _dotCache=new(); // 颜色名→12x12 圆点（菜单 Image），懒建缓存
  HashSet<string> _readSids=new(); readonly object _readLock=new object(); // 本托盘“已点开会话”表（未读徽标依据，sidecar 持久化）
  int _readSaveBusy=0;
  static readonly DateTime UNIX_EPOCH=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc);
  // 采集缓存：JSON 文件 mtime 未变则复用解析结果，避免每次全量读+防病毒扫描拖慢右键/启动
  long _wsMt; HashSet<string> _archCache=new();
  Dictionary<string,(long mt,string title,long last,long created,string cwd,bool sub,bool blank)> _sessCache=new();

  public TrayApp(bool dumpMode=false){
    cfg=Config.Load();
    foreach(var sc in cfg.Services){ if(!sc.Enabled) continue;
      var svc=new Service{ Spec=ServiceSpec.From(sc) };
      services.Add(svc);
    }
    gpus=GpuInfo.Discover();
    gpuTip=string.Join("\n",gpus.Select(g=>g.Line));
    LoadCfg();
    int legacyImp=0;
    try{
      perf=new PerfRuntime();
      legacyImp=perf.Store.ImportLegacy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"model-start.log"));
    }catch{ perf=null; }
    uiDisp=new UiDispatcher();                  // 必须早于任何 logForm.Append / Ui() 调用
    uiDisp.ErrorHandler=(tag,ex)=>ReportEx(tag,ex);
    logForm=MakeLogForm();
    if(perf!=null){
      perf.Log=(m)=>{ try{ logForm.Append("[性能] "+m+"\r\n"); }catch{} };
      if(legacyImp>0) logForm.Append("[性能] 已从 model-start.log 导入 "+legacyImp+" 条历史配置到台账（无性能数据，此后该文件只记首次出现的新配置）\r\n");
    }
    icon=new NotifyIcon{Icon=WhaleIcon(),Text="DSH托盘",Visible=!dumpMode};
    menu=new ContextMenuStrip();
    menu.ShowItemToolTips=true;   // 「运行状态悬浮」等项 ToolTip 生效（默认不保证）
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
    var mDshLog=new ToolStripMenuItem("查看 DSH 日志",null,(s,e)=>OpenDshLog());
    mDshLog.ToolTipText="打开统一日志窗口并切到「DSH 输出」页签：实时跟踪 dsh-web-out.log / dsh-web-err.log，逐行带 [HH:mm:ss]。窗口内有清屏 / 清空日志文件 / 复制全部 / 打开日志文件夹 / 自动滚动 / 暂停刷新。";
    dshMenu.DropDownItems.Add(mDshLog);
    dshMenu.DropDownItems.Add(new ToolStripMenuItem("检查/清除孤儿启动锁（node_modules.lock）",null,(s,e)=>Bg(()=>HealProfileOrphanLock(true))));
    dshMenu.DropDownItems.Add(new ToolStripSeparator());
    // —— 托盘内置渲染 / 安全模式 / 插件管理（官方客户端 bundle 崩溃白屏时的续会话通道）——
    builtinRenderItem=new ToolStripMenuItem("使用托盘内置渲染打开",null,(s,e)=>ToggleBuiltinRender());
    builtinRenderItem.ToolTipText="勾选后「打开 DSH 会话 / 最近会话」走托盘内置瘦客户端（本地页 + RPC 直连 3080，不加载官方客户端 bundle）。官方 Web UI 因插件 bundle 崩溃白屏时用它续会话。";
    builtinRenderItem.CheckOnClick=false;
    safeModeItem=new ToolStripMenuItem("安全模式启动",null,(s,e)=>ToggleSafeMode());
    safeModeItem.ToolTipText="勾选后启动/重启 DSH 附加 --patch overlay：仅保留官方插件白名单，屏蔽全部外部 bundle 与注入插件（临时屏蔽正在修改、导致 dsh 崩溃的插件）。下次启动 DSH 生效。";
    safeModeItem.CheckOnClick=false;
    dshMenu.DropDownItems.Add(builtinRenderItem);
    dshMenu.DropDownItems.Add(safeModeItem);
    dshMenu.DropDownItems.Add(new ToolStripMenuItem("插件管理…",null,(s,e)=>OpenPluginManager()));
    dshMenu.DropDownItems.Add(new ToolStripSeparator());
    dshMenu.DropDownItems.Add(new ToolStripMenuItem("打开 DSH 程序目录",null,(s,e)=>OpenDir(DshProgDir(),"DSH 程序目录")));
    dshMenu.DropDownItems.Add(new ToolStripMenuItem("打开 .dsh 目录",null,(s,e)=>OpenDir(DshHomeDir(),".dsh 目录")));
    // 最近会话（动态重建，见 RebuildRecentMenu；点击=弹窗导航 ?session=<id>）
    dshMenu.DropDownItems.Add(new ToolStripSeparator());
    recentHeader=new ToolStripMenuItem("最近会话"){Enabled=false};
    recentHeader.ToolTipText="会话状态圆点图例：●绿=执行中 ●黄=阻塞·询问 ●橙=阻塞·权限 ●蓝=已完成·未读 ●灰=已完成（已读） ●红=异常中断 ●浅=空/未收尾。（状态来自 dsh host 插件 dshtray-status）";
    dshMenu.DropDownItems.Add(recentHeader);
    dshMenu.DropDownItems.Add(new ToolStripMenuItem("（读取中…）"){Enabled=false});
    items.Add(dshMenu); items.Add(new ToolStripSeparator());
    itemOpenChat=new ToolStripMenuItem("打开官方会话 chat",null,(s,e)=>OpenOfficial());
    items.Add(itemOpenChat);
    items.Add(new ToolStripSeparator());
    // —— 每模型二级菜单（2026-09-11 改造）：一级项 = <状态圆点><名称> (端口)，二级 = 会话入口 + 模型启停 + 运行状态 + 运行时配置 ——
    // 原一级项「启动 <模型> (端口)」与「打开 DSH 会话（<模型>）」已合并进这里（一级不再单列每模型的会话入口）。
    foreach(var s2 in services) items.Add(BuildSvcMenu(s2));
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
    vram0Label=new ToolStripMenuItem("GPU0≈—"){Enabled=false};
    vram1Label=new ToolStripMenuItem("GPU1≈—"){Enabled=false};
    var tsBar=new TrackBar{Minimum=10,Maximum=90,Value=tsGpu1,TickFrequency=10,SmallChange=5,LargeChange=10,Width=270,Height=26,AutoSize=false}; // 滑块 1.5 倍加长(180→270)
    tsBar.ValueChanged+=(s,e)=>{ tsGpu1=tsBar.Value; tsLabel.Text="GPU1 占比: "+tsGpu1+"%（张量并行）"; RefreshChecks(); SaveCfg(); }; // RefreshChecks: 同步刷新一级菜单「切分模式：张量并行(N%)」标题当前值（含 VramSplitUpdate）
    VramSplitUpdate();
    splitMenu.DropDownItems.Add(tsLabel);
    splitMenu.DropDownItems.Add(new ToolStripControlHost(tsBar));
    splitMenu.DropDownItems.Add(vram0Label);
    splitMenu.DropDownItems.Add(vram1Label);
    // 不锁死子菜单宽度：加长滑块(270px)已是最宽项，子菜单按内容自适应即可（两行显存文本短于滑块），滚动时宽度稳定不抖。
    items.Add(splitMenu);
    // MTP 投机解码档位（单选）：无 / MTP / MTP2 / MTP3 / MTP4 → --spec-type draft-mtp + --spec-draft-n-max N（重启对应服务后生效）
    mtpMenu=new ToolStripMenuItem("MTP: "+MtpLabel(mtpLevel));
    mtpMenu.ToolTipText="llama.cpp MTP 投机解码档位：无=关闭；MTP..MTP4=--spec-type draft-mtp 且 --spec-draft-n-max 为 1..4（预测 token 数，越高提速越明显、收益边际递减）。重启对应服务后生效。";
    string[] mtpLabels={"无","MTP","MTP2","MTP3","MTP4"};
    for(int i=0;i<mtpLabels.Length;i++){ int v=i; var it=new ToolStripMenuItem(mtpLabels[i],null,(s,e)=>{ mtpLevel=v; RefreshChecks(); SaveCfg(); }); it.CheckOnClick=false; mtpItems.Add(it); mtpMenu.DropDownItems.Add(it); }
    items.Add(mtpMenu);
    // 模型监听地址（复选）：勾选=--host 0.0.0.0 局域网可访问（默认）；取消=--host 127.0.0.1 仅本机（下次启动服务生效）
    bindItem=new ToolStripMenuItem("模型监听 0.0.0.0（局域网可访问）",null,(s,e)=>ToggleBind());
    bindItem.ToolTipText="勾选后启动/重启模型服务时绑定所有网卡（--host 0.0.0.0，局域网设备可直接访问模型端口 808x）；取消勾选则仅本机可访问（--host 127.0.0.1）。下次启动服务生效。";
    bindItem.CheckOnClick=false;
    items.Add(bindItem);
    // 复选/单选二级菜单：点击后不隐藏（点外部/ESC 才关闭）
    KeepOpen(pm.DropDown); KeepOpen(gpuMenu.DropDown); KeepOpen(ctxMenu.DropDown); KeepOpen(splitMenu.DropDown); KeepOpen(kvMenu.DropDown); KeepOpen(cacheRamMenu.DropDown); KeepOpen(mtpMenu.DropDown);
    RefreshChecks();
    RefreshDshUi();
    items.Add(new ToolStripSeparator());
    var m7=new ToolStripMenuItem("查看日志",null,(s,e)=>ShowLogWin());
    m7.ToolTipText="打开统一日志窗口「托盘事件」页签：托盘点了什么、什么时候点的、启动链路每一步（带 [HH:mm:ss]）。可切到「DSH 输出」页签看 dsh 的 stdout/stderr。";
    var openCfg=new ToolStripMenuItem("打开配置文件",null,(s,e)=>{ try{ Process.Start(new ProcessStartInfo(Config.Path_){UseShellExecute=true}); }catch{} });
    var mPerfLog=new ToolStripMenuItem("模型性能日志",null,(s,e)=>ShowLogWinPerf());
    mPerfLog.ToolTipText="打开统一日志窗口的「模型性能」页签：按配置指纹(cfgId)汇总每套启动参数的加载耗时与实测 pp/tg 速度，可对选中配置跑固定规模基准。数据存 ~/.dsh/tools/model-perf/（configs.json 台账 + samples-YYYY-MM.ndjson 明细）。";
    items.Add(m7); items.Add(mPerfLog); items.Add(openCfg); items.Add(new ToolStripSeparator());
    autoStartItem=new ToolStripMenuItem("开机自启动",null,(s,e)=>ToggleAutoStart(autoStartItem)){CheckOnClick=true, Checked=AutoStart.Enabled()};
    autoStartItem.ToolTipText="在用户启动文件夹创建快捷方式，登录 Windows 自动运行 DSH托盘";
    items.Add(autoStartItem);
    var m8r=new ToolStripMenuItem("重启托盘",null,(s,e)=>RestartTray());
    m8r.ToolTipText="重启托盘进程（不打断模型服务：新实例启动时由 Adopt() 从端口接管已在跑的 llama-server）。";
    items.Add(m8r);
    // 默认语义（2026-09-11 反转）：退出=只退托盘、不停模型（等价 CLI：DSHTray.exe --exit）
    var m8=new ToolStripMenuItem("退出",null,(s,e)=>ExitApp(false));
    m8.ToolTipText="只退出托盘，不停止 llama-server：模型继续在 808x 端口服务，下次启动托盘由 Adopt() 自动接管，省去 30-60s 重载。等价于命令行 DSHTray.exe --exit。";
    items.Add(m8);
    // 要连模型一起停，必须显式指定（等价 CLI：DSHTray.exe --exit --stopall）
    var m8s=new ToolStripMenuItem("退出（同时停止模型服务）",null,(s,e)=>ExitApp(true));
    m8s.ToolTipText="退出托盘并停止本托盘启动/接管的模型服务（释放显存）。等价于命令行 DSHTray.exe --exit --stopall。";
    items.Add(m8s);
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
    LoadReadSids(); // 启动即载入已点开会话表（sidecar），供「已完成·未读」徽标判定
    recentRefreshMs=Environment.TickCount-54000; // 首次采集错峰：启动约 6s 后，避开图标注册/explorer 拥挤（启动瞬间右键必须即时可用）
    if(dumpMode){ try{ recentList=ReadRecent(); RebuildRecentMenu(); }catch{} } // 诊断模式同步填充，供 --dump-menu 核对
    else{ menu.Closed+=(s,e)=>RebuildRecentMenu(); } // 采集完全交给 Tick 低频；Closed 仅内存快照重建（无 IO）
    // 通知区冷启动右键修复（A/B 证实必要：去掉后重启托盘右键须等很久才有反应）：
    // explorer 对刚 NIM_ADD 的图标建立右键路由很慢 → 启动 ~2.5s 重注册一次（delete+add）强制绑定，右键随即可用。
    if(!dumpMode){
      // 弹出前重置菜单布局缓存：慢弹/异常显示会复用坏高度（顶层底部残留空白行，重启托盘才消），AutoSize 翻转强制每次重新度量
      menu.Opening+=(s,e)=>{ try{ if(menu!=null&&!menu.Visible&&menu.AutoSize){ menu.AutoSize=false; menu.AutoSize=true; } }catch{} };
      // 通知区冷启动右键修复（A/B 证实必要：去掉后重启托盘右键须等很久才有反应）：
      // explorer 对刚 NIM_ADD 的图标建立右键路由很慢 → 启动 ~2.5s 重注册一次（delete+add）强制绑定，右键随即可用。
      var rr=new System.Windows.Forms.Timer(); rr.Interval=2500; rr.Tick+=(s,e)=>{ rr.Stop(); rr.Dispose(); try{ icon.Visible=false; icon.Visible=true; }catch{} }; rr.Start();
      // explorer 自身重启（TaskbarCreated）后图标路由同样可能重建很慢 → 收到通知延迟再重注册一次
      try{ _tw=new TaskbarWatcher(); _tw.ExplorerRestarted+=()=>{ var t2=new System.Windows.Forms.Timer(); t2.Interval=900; t2.Tick+=(s2,e2)=>{ t2.Stop(); t2.Dispose(); try{ icon.Visible=false; icon.Visible=true; }catch{} }; t2.Start(); }; }catch{}
    }
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
      else if(l.StartsWith("mtpLevel=")){ int m; if(int.TryParse(l.Substring(9),out m)&&m>=0&&m<=4) mtpLevel=m; }
      else if(l.StartsWith("popW=")){ int v; if(int.TryParse(l.Substring(5),out v)&&v>=400&&v<=6000) _popW=v; }
      else if(l.StartsWith("popH=")){ int v; if(int.TryParse(l.Substring(5),out v)&&v>=300&&v<=6000) _popH=v; }
      else if(l.StartsWith("offW=")){ int v; if(int.TryParse(l.Substring(5),out v)&&v>=400&&v<=6000) _offW=v; }
      else if(l.StartsWith("offH=")){ int v; if(int.TryParse(l.Substring(5),out v)&&v>=300&&v<=6000) _offH=v; }
      else if(l.StartsWith("popX=")){ int v; if(int.TryParse(l.Substring(5),out v)) _popX=v; }
      else if(l.StartsWith("popY=")){ int v; if(int.TryParse(l.Substring(5),out v)) _popY=v; }
      else if(l.StartsWith("offX=")){ int v; if(int.TryParse(l.Substring(5),out v)) _offX=v; }
      else if(l.StartsWith("offY=")){ int v; if(int.TryParse(l.Substring(5),out v)) _offY=v; }
      else if(l.StartsWith("builtinRender=")) builtinRender = l.Substring(14).Trim()=="1";
      else if(l.StartsWith("safeMode=")) safeMode = l.Substring(9).Trim()=="1";
      else if(l.StartsWith("disabledEntries=")) disabledEntries=new HashSet<string>(l.Substring(16).Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries),StringComparer.OrdinalIgnoreCase);
      else if(l.StartsWith("disabledDev=")) disabledDevPlugins=new HashSet<string>(l.Substring(12).Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries),StringComparer.OrdinalIgnoreCase);
    } }catch{}
  }
  void SaveCfg(){ try{ File.WriteAllText(Cfg(),"paramMode="+paramMode+"\r\ngpu="+gpuSel.CfgString()+"\r\nctx="+ctxVal+"\r\nsplit="+(splitMode==0?"layer":"tensor")+"\r\ntsGpu1="+tsGpu1+"\r\nkv="+kvMode+"\r\ncacheRam="+cacheRam+"\r\nbind="+(bindAll?"0.0.0.0":"127.0.0.1")+"\r\nmtpLevel="+mtpLevel+"\r\npopX="+_popX+"\r\npopY="+_popY+"\r\npopW="+_popW+"\r\npopH="+_popH+"\r\noffX="+_offX+"\r\noffY="+_offY+"\r\noffW="+_offW+"\r\noffH="+_offH+"\r\nbuiltinRender="+(builtinRender?"1":"0")+"\r\nsafeMode="+(safeMode?"1":"0")+"\r\ndisabledEntries="+string.Join(";",disabledEntries)+"\r\ndisabledDev="+string.Join(";",disabledDevPlugins)+"\r\n"); }catch{} }
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
  // S5-3（2026-09-12）：档位标签的**实现**已搬进 QwenTray.Core/SvcLines.cs（SvcLinesTests 钉住越界回落）。
  // 下面两行是**迁移期兼容层** —— 与 Service 上那 10 个转发属性同一惯例：保住既有调用点一行不改，
  // JIT 会把转发内联掉，无性能损耗。新代码请直接写 `SvcLines.KvLabel(...)` / `SvcLines.CacheRamLabel(...)`。
  static string KvLabel(int m){ return SvcLines.KvLabel(m); }
  static string CacheRamLabel(int mb){ return SvcLines.CacheRamLabel(mb); }
  void ToggleBind(){ bindAll=!bindAll; RefreshChecks(); SaveCfg(); logForm.Append("模型监听: "+(bindAll?"0.0.0.0（局域网可访问）":"127.0.0.1（仅本机）")+"（重启对应服务后生效）\r\n"); }
  static void KeepOpen(ToolStripDropDown dd){ dd.Closing += (s,e)=>{ if(e.CloseReason==ToolStripDropDownCloseReason.ItemClicked) e.Cancel=true; }; }
  // 只让指定项「点击后不收起菜单」（其它项照常收起）：Click 先于 Closing 触发，用标志位判定是哪个项点开的收起
  void KeepOpenOnClick(ToolStripDropDown dd,ToolStripItem[] keep){
    foreach(var it in keep) if(it is ToolStripMenuItem mi) mi.Click+=(s,e)=>{ _keepClick=true; };
    dd.Closing+=(s,e)=>{ if(e.CloseReason==ToolStripDropDownCloseReason.ItemClicked&&_keepClick){ _keepClick=false; e.Cancel=true; return; } _keepClick=false; };
  }
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
    for(int i=0;i<mtpItems.Count;i++) mtpItems[i].Checked=(i==mtpLevel);
    if(mtpMenu!=null) mtpMenu.Text="MTP: "+MtpLabel(mtpLevel);
    if(vram0Label!=null) VramSplitUpdate();
    if(bindItem!=null) bindItem.Checked=bindAll;
    if(builtinRenderItem!=null) builtinRenderItem.Checked=builtinRender;
    if(safeModeItem!=null) safeModeItem.Checked=safeMode;
    if(gpuMenu!=null) gpuMenu.Text="GPU: "+gpuSel.ShortLabel();
    if(ctxMenu!=null) ctxMenu.Text="上下文: "+(ctxVal/1024)+"K";
    if(pm!=null) pm.Text="推理参数组："+ParamLabel(paramMode);
    if(splitMenu!=null) splitMenu.Text="切分模式："+(splitMode==0?"按层切分":"张量并行("+tsGpu1+"%)");
    if(kvMenu!=null) kvMenu.Text="KV 缓存: "+KvLabel(kvMode);
    if(cacheRamMenu!=null) cacheRamMenu.Text="缓存内存: "+CacheRamLabel(cacheRam);
    if(svcMenus.Count>0) RefreshSvcMenus();   // 参数变化时同步刷新每模型二级菜单的「运行时配置」行
  }
  // 同上：实现已搬进 SvcLines.cs，这里是迁移期兼容层（调用点零改动）
  static string ParamLabel(int m){ return SvcLines.ParamLabel(m); }
  static string MtpLabel(int m){ return SvcLines.MtpLabel(m); }
  // 张量并行时两卡显存估算（GPU0/GPU1 分两行各 1 位小数）：模型权重(GGUF 大小) + KV(按 ctx) + 每卡计算缓冲(≈1.1GB) 按 -ts 比例分摊。
  // 细节（参考模型/ctx/占比）放 ToolTip；可见文本保持短且固定宽度，避免拖动滑块时子菜单宽度抖动（布局震荡）。仅供预览，实际以 llama-server 加载报告为准。
  void VramSplitUpdate(){
    try{
      Service s = services.FirstOrDefault(x=>x.Running) ?? services.FirstOrDefault();
      string txt0="GPU0≈—", txt1="GPU1≈—", tip="张量并行两卡显存估算（1 位小数）：模型权重+KV(按ctx)+每卡计算缓冲按 -ts 比例分摊；实际以 llama-server 加载报告为准。";
      if(s!=null && !string.IsNullOrEmpty(s.Model) && File.Exists(s.Model)){
        double modelGiB = new FileInfo(s.Model).Length / 1073741824.0;
        double kvGiB = 3.0 * ctxVal / 262144.0;   // 35B 实测 256K(q8_0) KV≈3GB，按 ctx 线性近似
        double compute = 1.1;                     // 计算缓冲每卡约 1.1GB（35B 量级）
        double g0 = modelGiB*(100-tsGpu1)/100.0 + kvGiB*(100-tsGpu1)/100.0 + compute;
        double g1 = modelGiB*tsGpu1/100.0 + kvGiB*tsGpu1/100.0 + compute;
        txt0="GPU0≈"+g0.ToString("0.0")+"GB";
        txt1="GPU1≈"+g1.ToString("0.0")+"GB";
        tip="参考: "+Short(s)+" "+(ctxVal/1024)+"K | 模型权重(按GGUF)+KV(按ctx)+每卡计算缓冲≈1.1GB，按 -ts "+(100-tsGpu1)+"/"+tsGpu1+" 分摊；实际以 llama-server 加载报告为准。";
      }
      if(vram0Label!=null){ vram0Label.Text=txt0; vram0Label.ToolTipText=tip; }
      if(vram1Label!=null){ vram1Label.Text=txt1; vram1Label.ToolTipText=tip; }
    }catch{}
  }

}
