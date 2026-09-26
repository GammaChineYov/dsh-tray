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
  ToolStripMenuItem? tsLabelItem; TrackBar? tsBarCtl;   // 滑块/标签提升为字段：RefreshChecks 让滑块位置跟随当前模型生效值
  ToolStripMenuItem? splitMenu,splitLayerItem,splitRowItem;
  ToolStripMenuItem? vram0Label,vram1Label; // 切分模式滑块旁：张量并行时 GPU0/GPU1 两卡显存估算（各 1 位小数，分两行）
  ToolStripMenuItem? kvMenu,kvDefItem,kv8Item,kv16Item;
  ToolStripMenuItem? cacheRamMenu; List<ToolStripMenuItem> cacheRamItems=new();
  ToolStripMenuItem? bindItem; bool bindAll=true; // 模型监听地址：true=0.0.0.0（局域网可访问，默认）；false=127.0.0.1（仅本机）
  ToolStripMenuItem? mtpMenu; List<ToolStripMenuItem> mtpItems=new(); int mtpLevel=0; // MTP 投机解码档位：0=无 1=MTP 2=MTP2 … 8=MTP8（--spec-draft-n-max），共 9 个可选项
  // —— 内存回收（2026-09-13，用户拍板"手动 + 空闲自动 + 模型就绪后自动一次"）——
  // 判据与文本在 Core 的 MemTrim（单测穷举）；这里只做菜单装配 + 在 Tick 上执行。
  ToolStripMenuItem? trimMenu,trimNowItem,trimOnReadyItem,trimIdleItem,trimStatusItem;
  List<ToolStripMenuItem> trimIntervalItems=new();
  bool trimOnReady=true, trimIdle=true; int trimIntervalSec=MemTrim.DefaultIdleIntervalSec;
  long lastTrimMs=0;            // 最近一次回收（全局节拍，Env.TickCount）；0=从未
  int trimTick=0;               // 空闲判定的降频计数器（不用 1s 去打 P/Invoke）
  // —— L3 硬盘缓存层（--slot-save-path）：勾选后模型重启才带上该参数（不带 ⇒ /slots action 端点 501）——
  ToolStripMenuItem? slotSaveItem; bool slotSaveOn=true; string slotSavePath=LaunchArgs.DefaultSlotSavePath;
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
  // —— 当前选中的模型（2026-09-26）——
  // 交互语义变了：一级菜单里的模型名**从"点一下就启动"改成"点一下就选中"**（互斥单选，
  // 默认保留上次选中的那个）。菜单下方那组「模型启动配置 / 运行状态 / 启停」全部跟随它。
  // 持久化落点：cfg（dsh-tray-config.json）是**服务清单**（模型会被增删/改名），不适合记"上次选中谁"；
  // 故写在 dsh-tray.cfg 的 selectedModel=<服务 Name>。解析不出来（首次运行 / cfg 手改坏）⇒
  // 回落**配置里的第一个模型** ⇒ selected 永不为 null ⇒ 下面那组操作项永远有归属（不会指向空气）。
  Service? selSvc;
  string selectedModelName="";   // dsh-tray.cfg 的 selectedModel=（上次的选中态）
  ToolStripMenuItem? curHeader,curStatus,curCfgLine;                       // 选中模型的信息区（禁用项）
  ToolStripMenuItem? curStart,curRestart,curStop;                          // 选中模型的启停（②类）
  ToolStripMenuItem? curOpenDsh,curOpenBuiltin;                            // 选中模型的会话入口（③类）
  // 「点了关不关」的唯一裁决器（三件套：先拦后判 + 500ms 闸门 + ③类 BeginInvoke 补关，见 MenuKeepOpen.cs）；
  // ⚠️ 判据载体是 _keepItems 清单，**不用 Tag**（Tag 在 decision-tray/cmd-tray 已被业务占用）。
  readonly MenuKeepOpen _keep=new(); readonly List<ToolStripItem> _keepItems=new();
  int svcTick=0;                                         // 状态巡检节拍
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
    // 🔴 **关掉**平台自带的 item tooltip（2026-09-12；纪律唯一源 = skill `tray-menu-keepopen` 铁律 8）。
    //    起因：平台 tooltip 会**压住鼠标/被悬停的那一行** ⇒ 底下元素丢 hover ⇒ 提示自己收起 ⇒ hover 恢复 ⇒
    //    再弹 = **自振荡（闪）**。它的摆位在 ToolStrip 内部算，只保证"不出屏"、不保证"不压光标"，
    //    我们改不了 ⇒ 换载体：关平台渲染 + 位置交 `TipPlace` 自己算（见 TrayApp.Tips.cs / TipPlace.cs）。
    //    ⚠️ 各菜单项的 `ToolTipText` **照旧要设**：它从"渲染源"降级为**数据源**（自绘提示与自检都读它），别删。
    //    ⚠️ ShowItemToolTips 是**每个下拉各自**的开关（父级设了不继承）⇒ 子下拉由 AttachMenuTips() 递归逐个关。
    menu.ShowItemToolTips=false;
    var items=new List<ToolStripItem>();
    // —— DSH（本机 DSH Web 服务，端口 3080）控制：状态图标 绿=运行中 黄=启动中 红=未启动；启动/重启/停止 DSH ——
    dshMenu=new ToolStripMenuItem("DSH");
    dshMenu.ToolTipText="DSH 服务状态：绿色=运行中、黄色=启动中、红色=未启动。可启动 / 重启 / 停止本机 DSH Web（端口 3080）。未启动（红点）时直接点本项 = 快捷启动 DSH，不必展开子菜单。";
    dshStatusItem=new ToolStripMenuItem("状态：未启动"){ Enabled=false };
    dshStartItem=new ToolStripMenuItem("启动 DSH",null,(s,e)=>Bg(()=>DshStart()));
    dshRestartItem=new ToolStripMenuItem("重启 DSH",null,(s,e)=>Bg(()=>DshRestart()));
    dshStopItem=new ToolStripMenuItem("停止 DSH",null,(s,e)=>Bg(()=>DshStop()));
    // ② 类（就地控制）：动作结果 = 同菜单状态行 + dshMenu 圆点（RefreshDshUi）⇒ 点完不关、可连点。
    _keepItems.Add(dshStartItem); _keepItems.Add(dshRestartItem); _keepItems.Add(dshStopItem);
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
    builtinRenderItem.ToolTipText=SessionEntryLabels.ToggleTooltip();
    builtinRenderItem.CheckOnClick=false;
    safeModeItem=new ToolStripMenuItem("安全模式启动",null,(s,e)=>ToggleSafeMode());
    safeModeItem.ToolTipText="勾选后启动/重启 DSH 附加 --patch overlay：仅保留官方插件白名单，屏蔽全部外部 bundle 与注入插件（临时屏蔽正在修改、导致 dsh 崩溃的插件）。下次启动 DSH 生效。";
    safeModeItem.CheckOnClick=false;
    // ① 类（状态切换）：勾选项，CheckOnClick=false + RefreshChecks() 手动刷 ⇒ 点完不关、勾选立刻可见。
    // ⚠️ dsh 的项**全部 CheckOnClick=false**，照抄 CheckOnClick 兜底会全判③类 → 必须显式声明（路径 B）。
    _keepItems.Add(builtinRenderItem); _keepItems.Add(safeModeItem);
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
    // 一级项「DSH」：未启动（红点）时直接点它 = 快捷启动 DSH（不必展开子菜单）。
    //   判据 dshState==0 与子菜单里「启动 DSH」项的 Enabled 是**同一个表达式**（见 RefreshDshUi）
    //   ⇒ 用户看到红点就敢点，看到黄/绿点只展开子菜单（原行为不变）。
    WireQuickStart(dshMenu,()=>dshState==0,()=>Bg(()=>DshStart()),"DSH");
    items.Add(dshMenu); items.Add(new ToolStripSeparator());
    itemOpenChat=new ToolStripMenuItem("打开官方会话 chat",null,(s,e)=>OpenOfficial());
    items.Add(itemOpenChat);
    items.Add(new ToolStripSeparator());
    // —— 每模型二级菜单（2026-09-11 改造）：一级项 = <状态圆点><名称> (端口)，二级 = 会话入口 + 模型启停 + 运行状态 + 运行时配置 ——
    // 原一级项「启动 <模型> (端口)」与「打开 DSH 会话（<模型>）」已合并进这里（一级不再单列每模型的会话入口）。
    foreach(var s2 in services) items.Add(BuildSvcMenu(s2));
    // —— 选中模型的操作组（2026-09-26「取消模型二级菜单 / 功能上移一级」）——
    // 这一组的**每一项都作用于 selSvc**：点上面的模型行换选中 ⇒ 整组（状态行 / 启停可用性 /
    // 会话入口 / 运行时配置行）立刻跟着换。所以它的 Enabled 与文本不能各算一套，统一由
    // RefreshCurGroup() 从一个Service 算出（同源判据 = 用户看到的与真正启动的必须是同一个模型）。
    items.Add(new ToolStripSeparator());
    curHeader=new ToolStripMenuItem("当前模型：—"){Enabled=false};
    curHeader.ToolTipText="当前选中的模型（点上面任意一个模型名即切换；互斥单选，选择会被记住，下次启动托盘仍在）。下面的参数面板与启停都作用到它。";
    curStatus=new ToolStripMenuItem("状态：—"){Enabled=false};
    curStart=new ToolStripMenuItem("启动模型",null,(s,e)=>Bg(()=>StartCur()));
    curRestart=new ToolStripMenuItem("重启模型",null,(s,e)=>Bg(()=>RestartCur()));
    curStop=new ToolStripMenuItem("停止模型",null,(s,e)=>Bg(()=>StopCur()));
    curOpenDsh=new ToolStripMenuItem(SessionEntryLabels.OpenDshText(false),null,(s,e)=>OpenCurSession());
    curOpenBuiltin=new ToolStripMenuItem(SessionEntryLabels.OpenDshText(true),null,(s,e)=>OpenCurSessionBuiltin());
    curCfgLine=new ToolStripMenuItem("运行时配置：—"){Enabled=false};
    curStart.ToolTipText="按当前模型自身的参数（未覆盖的项用托盘全局默认）启动它。";
    curRestart.ToolTipText="重读 dsh-tray-config.json（本模型配置）与 dsh-tray.cfg（全局默认）后重启本模型：只重启 llama-server，不重启 DSH(3080) ⇒ dsh 会话不中断。";
    curStop.ToolTipText="停止本模型服务（Kill 进程并释放显存）；进程句柄丢失时按端口兜底回收。";
    curCfgLine.ToolTipText="本模型生效的启动参数（跟随上面的参数面板；本模型自己覆盖过的项优先）。运行中会叠加服务端 /props 实测值。";
    // ② 类（就地控制）：调参→重启→看效果→再重启 是连续操作，结果（状态行）就在同一菜单里可见 ⇒ 点完不关。
    // 会话入口 = ③类（不声明 ⇒ fail-safe 自动补关，与原先每模型二级菜单里的两条同归类）。
    _keepItems.Add(curStart); _keepItems.Add(curRestart); _keepItems.Add(curStop);
    items.Add(curHeader); items.Add(curStatus);
    items.Add(curStart); items.Add(curRestart); items.Add(curStop);
    items.Add(curOpenDsh); items.Add(curOpenBuiltin);
    items.Add(curCfgLine);
    items.Add(new ToolStripSeparator());
    var m5=new ToolStripMenuItem("停止全部",null,(s,e)=>StopAll());
    var m6=new ToolStripMenuItem("重启全部",null,(s,e)=>RestartAll());
    // ② 类：逐模型重启，结果（各模型一级圆点）就在同一菜单里可见 ⇒ 点完不关。
    // ⚠️ m5「停止全部」**不加**：第二次点是空操作 ⇒ fail-safe 归③（不声明即自动补关）。
    _keepItems.Add(m6);
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
    // ⚠️ 标签必须先拷进循环体内的局部变量再进 lambda：for 的 i 是单个共享变量，
    //    直接写 ctxLabels[i] 会在点击时（循环早已结束，i==Length）越界抛异常 ——
    //    异常发生在 SetCtx 实参求值阶段，函数体一行不执行 ⇒「点了没效果」（2026-09-26 实测踩坑）。
    for(int i=0;i<LaunchArgs.CtxOptions.Length;i++){ int v=LaunchArgs.CtxOptions[i]; string lb=ctxLabels[i]; var it=new ToolStripMenuItem(ctxLabels[i],null,(s,e)=>SetCtx(v,lb)); it.CheckOnClick=false; ctxItems.Add(it); ctxMenu.DropDownItems.Add(it); }
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
    string[] cacheRamLabels={"512M","1GB","2GB","4GB","8GB","16GB","无限制","禁用"};
    for(int i=0;i<LaunchArgs.CacheRamOptions.Length;i++){ int v=LaunchArgs.CacheRamOptions[i]; string lb=cacheRamLabels[i]; var it=new ToolStripMenuItem(cacheRamLabels[i],null,(s,e)=>SetCacheRam(v,lb)); it.CheckOnClick=false; cacheRamItems.Add(it); cacheRamMenu.DropDownItems.Add(it); }
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
    tsBarCtl=tsBar; tsLabelItem=tsLabel;   // 提升为字段：RefreshChecks 让滑块/标签跟随当前模型的生效值
    tsBar.ValueChanged+=(s,e)=>SetTsGpu1(tsBar.Value,tsLabel); // RefreshChecks: 同步刷新一级菜单「切分模式：张量并行(N%)」标题当前值（含 VramSplitUpdate）
    VramSplitUpdate(ctxVal,tsGpu1);
    splitMenu.DropDownItems.Add(tsLabel);
    splitMenu.DropDownItems.Add(new ToolStripControlHost(tsBar));
    splitMenu.DropDownItems.Add(vram0Label);
    splitMenu.DropDownItems.Add(vram1Label);
    // 不锁死子菜单宽度：加长滑块(270px)已是最宽项，子菜单按内容自适应即可（两行显存文本短于滑块），滚动时宽度稳定不抖。
    items.Add(splitMenu);
    // MTP 投机解码档位（单选）：无 / MTP / MTP2 … MTP8 → --spec-type draft-mtp + --spec-draft-n-max N（重启对应服务后生效）
    mtpMenu=new ToolStripMenuItem("MTP: "+MtpLabel(mtpLevel));
    mtpMenu.ToolTipText="llama.cpp MTP 投机解码档位：无=关闭；MTP..MTP8（9 个可选项）=--spec-type draft-mtp 且 --spec-draft-n-max 为 1..8（预测 token 数，越高提速越明显、收益边际递减）。改的是**当前选中模型**的配置，重启该模型后生效。";
    string[] mtpLabels={"无","MTP","MTP2","MTP3","MTP4","MTP5","MTP6","MTP7","MTP8"};
    for(int i=0;i<mtpLabels.Length;i++){ int v=i; var it=new ToolStripMenuItem(mtpLabels[i],null,(s,e)=>SetMtp(v)); it.CheckOnClick=false; mtpItems.Add(it); mtpMenu.DropDownItems.Add(it); }
    items.Add(mtpMenu);
    // 模型监听地址（复选）：勾选=--host 0.0.0.0 局域网可访问（默认）；取消=--host 127.0.0.1 仅本机（下次启动服务生效）
    bindItem=new ToolStripMenuItem("模型监听 0.0.0.0（局域网可访问）",null,(s,e)=>ToggleBind());
    bindItem.ToolTipText="勾选后启动/重启模型服务时绑定所有网卡（--host 0.0.0.0，局域网设备可直接访问模型端口 808x）；取消勾选则仅本机可访问（--host 127.0.0.1）。下次启动服务生效。";
    bindItem.CheckOnClick=false;
    // ① 类（状态切换）：CheckOnClick=false + RefreshChecks() 手动刷 ⇒ 不声明就是当前缺陷（点了必关）。
    _keepItems.Add(bindItem);
    items.Add(bindItem);
    // —— 内存与缓存（2026-09-13）——
    // 上半是**工作集主动回收**（用户拍板的三条路径：手动 / 模型就绪后自动一次 / 空闲自动）；
    // 下半是 **L3 硬盘 KV 缓存层**（--slot-save-path）的开关 —— 两者同属"管住模型占的资源"，
    // 合在一个下拉里（也顺带让 ① 类声明走"路径 A 整段保持"，不必逐项点名）。
    trimMenu=new ToolStripMenuItem("内存与缓存");
    trimMenu.ToolTipText="模型进程的内存回收与硬盘 KV 缓存层。\n"
      +"· 内存回收：把 llama-server 的工作集页（权重 mmap/文件缓存）推回 standby —— 实测释放十余 GiB 且推理速度不变。\n"
      +"· 硬盘 KV 缓存：给 llama-server 加 --slot-save-path，使 /slots 的 save|restore 端点可用（三级降级缓存的硬盘层）。";
    trimNowItem=new ToolStripMenuItem("立即回收运行中模型内存",null,(s,e)=>Bg(()=>TrimAll(TrimKind.Manual)));
    trimNowItem.ToolTipText="对全部运行中的模型执行一次 EmptyWorkingSet。空闲时做这件事零代价（页仍在 standby，可再取）；正在生成时请等它跑完。";
    trimOnReadyItem=new ToolStripMenuItem("模型就绪后自动回收一次",null,(s,e)=>ToggleTrimOnReady());
    trimOnReadyItem.ToolTipText="勾选后：llama-server 每次从「启动中」转为就绪时自动回收一次。此时工作集刚达峰值（加载把整个 gguf 读了一遍），回收收益最大。";
    trimOnReadyItem.CheckOnClick=false;
    trimIdleItem=new ToolStripMenuItem("空闲自动回收",null,(s,e)=>ToggleTrimIdle());
    trimIdleItem.ToolTipText="勾选后：按下面的间隔自动回收，但**必须当前没有请求在处理**（/slots 全部 is_processing=false）才动手 —— 推理中回收只会换来缺页抖动。";
    trimIdleItem.CheckOnClick=false;
    trimStatusItem=new ToolStripMenuItem("回收：—"){Enabled=false};
    string[] trimIvLabels={"5分钟","15分钟","30分钟","1小时","2小时","仅手动"};
    for(int i=0;i<MemTrim.IdleIntervalOptions.Length;i++){
      int v=MemTrim.IdleIntervalOptions[i];
      var it=new ToolStripMenuItem("回收间隔 · "+trimIvLabels[i],null,(s,e)=>{ trimIntervalSec=v; RefreshChecks(); SaveCfg(); logForm.Append("内存回收间隔: "+MemTrim.IntervalLabel(v)+"（空闲自动回收）\r\n"); });
      it.CheckOnClick=false; trimIntervalItems.Add(it);
    }
    slotSaveItem=new ToolStripMenuItem("硬盘 KV 缓存（--slot-save-path）",null,(s,e)=>ToggleSlotSave());
    slotSaveItem.ToolTipText="勾选后：启动/重启模型时带上 --slot-save-path "+slotSavePath+"，llama-server 才会挂 POST /slots/{id}?action=save|restore 端点（不给 ⇒ 501 not_supported_error）。\n"
      +"⚠️ 该端点只是**原语**：上游明确不做「自动落盘/自动恢复」（FR #17107 关为 not planned），策略由外部驱动（E:\\kv_cache\\kvctl.py）。";
    slotSaveItem.CheckOnClick=false;
    _keepItems.Add(slotSaveItem);
    trimMenu.DropDownItems.Add(trimNowItem);
    trimMenu.DropDownItems.Add(new ToolStripSeparator());
    trimMenu.DropDownItems.Add(trimOnReadyItem);
    trimMenu.DropDownItems.Add(trimIdleItem);
    foreach(var it in trimIntervalItems) trimMenu.DropDownItems.Add(it);
    trimMenu.DropDownItems.Add(new ToolStripSeparator());
    trimMenu.DropDownItems.Add(slotSaveItem);
    var slotPathLabel=new ToolStripMenuItem("    路径: "+slotSavePath){Enabled=false};
    slotPathLabel.ToolTipText="改路径请编辑 dsh-tray.cfg 的 slotSavePath= 后点「重启模型」（或直接改 LaunchArgs.DefaultSlotSavePath 重新构建）。";
    trimMenu.DropDownItems.Add(slotPathLabel);
    trimMenu.DropDownItems.Add(new ToolStripSeparator());
    // —— 启动预热（L3 硬盘缓存）三件套：开关 / 立即 / 状态。实现见 TrayApp.Warmup.cs ——
    warmupOnReadyItem=new ToolStripMenuItem("模型就绪后预热最近 KV",null,(s,e)=>ToggleWarmupOnReady());
    warmupOnReadyItem.ToolTipText="勾选后：llama-server 每次从「启动中」转为就绪时，把 L3 硬盘缓存里最近的条目 restore 回**空闲槽**。"
      +"\n为什么有用：重启后显存 KV 全清，长前缀要从零重算（实测 1651 token：prompt_ms 532.6 → restore 后 48.1，11×）。"
      +"\n命中判定不用我们做 —— restore 回槽后 llama-server 自己做 LCP 前缀匹配（它掌握真 token 序列）。"
      +"\n安全闸门：有请求在处理 / 没有空闲槽 / 条目与当前模型或 KV 类型不一致 ⇒ 一律不预热。";
    warmupOnReadyItem.CheckOnClick=false;
    warmupNowItem=new ToolStripMenuItem("立即预热（恢复到空闲槽）",null,(s,e)=>Bg(()=>{ foreach(var sv in services) if(sv.Running) WarmupOne(sv,false); }));
    warmupNowItem.ToolTipText="手动跑一次预热（不重启、不动模型）。等价于：DSHTray.exe --warmup [port]";
    warmupStatusItem=new ToolStripMenuItem("预热：—"){Enabled=false};
    trimMenu.DropDownItems.Add(warmupOnReadyItem);
    trimMenu.DropDownItems.Add(warmupNowItem);
    trimMenu.DropDownItems.Add(warmupStatusItem);
    trimMenu.DropDownItems.Add(new ToolStripSeparator());
    trimMenu.DropDownItems.Add(trimStatusItem);
    trimMenu.DropDown.ShowItemToolTips=false;
    items.Add(trimMenu);
    // 路径 A（整段都是①②类）：8 个参数面板下拉，逐项都是勾选/单选 ⇒ 整段保持。
    _keep.DeclareWholeState(pm.DropDown,gpuMenu.DropDown,ctxMenu.DropDown,splitMenu.DropDown,kvMenu.DropDown,cacheRamMenu.DropDown,mtpMenu.DropDown,trimMenu.DropDown);
    // 2026-09-26：启动即恢复上次选中（或默认第一个）—— **没有"无选中"态**。
    //   ⚠️ 此前 SelectInitial 是死代码（零调用点）：启动后 selSvc=null，参数面板显示全局值、
    //   编辑走全局分支，用户点的"选中"语义完全没生效（"切换模型配置不跟着变"的另一半根因）。
    //   物化（MaterializeConfig）也挂在这里：默认选中的模型启动即补齐自己的配置。
    SelectInitial();
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
    // ① 类（状态切换）：唯一 CheckOnClick=true 的项（本已兜底），仍显式声明以保证"清单=全部①②项"可断言。
    _keepItems.Add(autoStartItem);
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
    // —— 统一挂载（必须早于下面的 if(dumpMode)：dumpMode 会调 RebuildRecentMenu→Hook/WireNew）——
    // 先声明 ①②类清单（路径 B），再给整棵树挂裁决（① Closing 先拦 / ② AppFocusChange 500ms 闸门 / ③ 未声明项补关）。
    _keep.DeclareStateItems(_keepItems);
    _keep.Hook(menu);
    // 落点护栏：根菜单 + 所有子菜单（`NotifyIcon` 把右下角贴在光标上、不钳工作区 ⇒ 菜单最后一行被任务栏吃）
    MenuPlace.GuardAll(menu);
    // 悬停提示改自绘（关平台渲染 + 自己摆位；铁律 8）。必须晚于整棵树建完（项都齐了），
    // 且**早于**下面的 dumpMode 分支（它会调 RebuildRecentMenu → 本方法）。
    AttachMenuTips();
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
      else if(l.StartsWith("mtpLevel=")){ int m; if(int.TryParse(l.Substring(9),out m)&&m>=0&&m<=8) mtpLevel=m; }   // 9 个可选项：无 + MTP..MTP8
      else if(l.StartsWith("selectedModel=")) selectedModelName=l.Substring(14).Trim();   // 上次选中的模型（一级菜单互斥选中态，重启托盘后仍在）
      else if(l.StartsWith("trimOnReady=")) trimOnReady = l.Substring(12).Trim()!="0";
      else if(l.StartsWith("trimIdle=")) trimIdle = l.Substring(9).Trim()!="0";
      else if(l.StartsWith("trimIntervalSec=")){ int v; if(int.TryParse(l.Substring(16),out v)) trimIntervalSec=MemTrim.ClampInterval(v); }
      else if(l.StartsWith("slotSave=")) slotSaveOn = l.Substring(9).Trim()!="0";
      else if(l.StartsWith("slotSavePath=")){ string p=l.Substring(13).Trim(); if(p.Length>0) slotSavePath=p; }
      else if(l.StartsWith("warmupOnReady=")) warmupOnReady = l.Substring(14).Trim()!="0";
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
  void SaveCfg(){ try{ File.WriteAllText(Cfg(),"paramMode="+paramMode+"\r\ngpu="+gpuSel.CfgString()+"\r\nctx="+ctxVal+"\r\nsplit="+(splitMode==0?"layer":"tensor")+"\r\ntsGpu1="+tsGpu1+"\r\nkv="+kvMode+"\r\ncacheRam="+cacheRam+"\r\nbind="+(bindAll?"0.0.0.0":"127.0.0.1")+"\r\nmtpLevel="+mtpLevel+"\r\ntrimOnReady="+(trimOnReady?"1":"0")+"\r\ntrimIdle="+(trimIdle?"1":"0")+"\r\ntrimIntervalSec="+trimIntervalSec+"\r\nslotSave="+(slotSaveOn?"1":"0")+"\r\nslotSavePath="+slotSavePath+"\r\nwarmupOnReady="+(warmupOnReady?"1":"0")+"\r\npopX="+_popX+"\r\npopY="+_popY+"\r\npopW="+_popW+"\r\npopH="+_popH+"\r\noffX="+_offX+"\r\noffY="+_offY+"\r\noffW="+_offW+"\r\noffH="+_offH+"\r\nbuiltinRender="+(builtinRender?"1":"0")+"\r\nsafeMode="+(safeMode?"1":"0")+"\r\ndisabledEntries="+string.Join(";",disabledEntries)+"\r\ndisabledDev="+string.Join(";",disabledDevPlugins)+"\r\nselectedModel="+(selSvc!=null?selSvc.Name:"")+"\r\n"); }catch{} }
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
  // ⚠️ 2026-09-26：这些 setter 除了写全局默认，还会把值**落到当前选中模型的配置**里（持久化）。
  //    全局默认仍要写（保住"新模型/未覆盖模型用同一个基线"这条既有行为），不是被删掉，而是退居"默认"。
  // 2026-09-26 语义定稿：面板 = 当前选中模型的**配置视图+编辑器**。
  //   有选中模型 → 编辑只写该模型的持久化配置（PersistParam：config.json + 内存 Spec），
  //               **不再动全局基线**（否则 A 上的改动会泄漏成 B/C/D 未覆盖项的"默认"，切换模型时
  //               用户会看到别人的改动 —— 正是"配置没有跟着变"的反面）。
  //   无选中模型（探针/极端态）→ 退回旧全局行为，直接改基线变量。
  //   显示层（RefreshChecks）读 CurEff() 生效值：覆盖优先、未覆盖回落全局 —— 切换模型自动跟随。
  void SetParam(int m){ if(selSvc==null) paramMode=m; RefreshChecks(); SaveCfg(); string label=m==0?"通用思考 (temp1.0/pres1.5)":m==1?"编码思考 (temp0.6/pres0.0)":"Instruct (temp0.7/pres1.5)"; logForm.Append("推理参数组: "+label+"（重启对应模型后生效）\r\n"); PersistParam(sc=>sc.ParamMode=m,"推理参数组: "+label); }
  void SetCtx(int v,string label){ if(selSvc==null) ctxVal=v; RefreshChecks(); SaveCfg(); PersistParam(sc=>sc.Ctx=v,"上下文: "+label+"（重启对应模型后生效）"); }
  void SetCacheRam(int v,string label){ if(selSvc==null) cacheRam=v; RefreshChecks(); SaveCfg(); PersistParam(sc=>sc.CacheRam=v,"缓存内存: "+label+"（重启对应模型后生效）"); }
  // 滑块是拖拽连续量：每次 ValueChanged 都落盘会在拖的过程中刷爆 config.json，所以只在**值真的变了**时写。
  // tsSync：RefreshChecks 程序化同步滑块位置（切换模型跟随）时置位，ValueChanged 由此触发的事件不当作用户输入。
  bool tsSync;
  void SetTsGpu1(int v,ToolStripMenuItem label){
    if(tsSync) return;
    if(selSvc==null) tsGpu1=v;
    if(label!=null) label.Text="GPU1 占比: "+v+"%（张量并行）";
    RefreshChecks(); SaveCfg();
    if(_lastTsWritten==v) return; _lastTsWritten=v;
    PersistParam(sc=>sc.TsGpu1=v,"GPU1 占比: "+v+"%（重启对应模型后生效）");
  }
  int _lastTsWritten=-1;
  void SetMtp(int v){ if(selSvc==null) mtpLevel=v; RefreshChecks(); SaveCfg(); PersistParam(sc=>sc.MtpLevel=v,"MTP: "+MtpLabel(v)+"（重启对应模型后生效）"); }
  void SetSplit(int m){ if(selSvc==null) splitMode=m; RefreshChecks(); SaveCfg(); logForm.Append("切分模式: "+(m==0?"按层切分 layer":"张量并行 tensor")+"（重启对应模型后生效）\r\n"); PersistParam(sc=>sc.SplitMode=m,"切分模式: "+(m==0?"按层切分 layer":"张量并行 tensor")); }
  void SetKv(int m){ if(selSvc==null) kvMode=m; RefreshChecks(); SaveCfg(); logForm.Append("KV 缓存: "+KvLabel(m)+"（重启对应模型后生效）\r\n"); PersistParam(sc=>sc.KvMode=m,"KV 缓存: "+KvLabel(m)); }
  // S5-3（2026-09-12）：档位标签的**实现**已搬进 QwenTray.Core/SvcLines.cs（SvcLinesTests 钉住越界回落）。
  // 下面两行是**迁移期兼容层** —— 与 Service 上那 10 个转发属性同一惯例：保住既有调用点一行不改，
  // JIT 会把转发内联掉，无性能损耗。新代码请直接写 `SvcLines.KvLabel(...)` / `SvcLines.CacheRamLabel(...)`。
  static string KvLabel(int m){ return SvcLines.KvLabel(m); }
  static string CacheRamLabel(int mb){ return SvcLines.CacheRamLabel(mb); }
  void ToggleBind(){
    bool cur = selSvc!=null ? CurEff().BindAll : bindAll;   // 翻转的基准 = 当前**生效值**（面板显示的就是它）
    bool next = !cur;
    if(selSvc==null) bindAll=next;                          // 无模型 → 改全局基线；有模型 → 只走 PersistParam
    RefreshChecks(); SaveCfg();
    string v=(next?"0.0.0.0（局域网可访问）":"127.0.0.1（仅本机）")+"（重启对应模型后生效）";
    logForm.Append("模型监听: "+v+"\r\n");
    PersistParam(sc=>sc.BindAll=next,"模型监听: "+v);
  }

  // ——————————————— 内存与缓存（2026-09-13）———————————————
  // L3 硬盘层的**生效路径**：开关关掉 ⇒ 空串 ⇒ LaunchArgs.Build 不加 --slot-save-path（= 旧行为，端点 501）。
  string SlotSavePath(){ return slotSaveOn ? slotSavePath : ""; }
  // 三条回收路径（判定在 Core/MemTrim，单测穷举）：
  //   手动   = 菜单「立即回收…」/ CLI `DSHTray.exe --trim <port>`  —— 人的意图，恒放行
  //   启动后 = 探活线程在 Starting→就绪时置 svc.pendingTrimOnReady，由 TrimTick 消费一次
  //   空闲   = TrimTick 每 30s 评估一次间隔 + 全局无请求
  // ⚠️ 回收一律**离开 UI 线程**执行：里面既有 HTTP 探测（/slots）又有 P/Invoke，
  //    放在 1s 心跳上会把右键菜单拖住。_trimBusy 保证同一时刻只有一次回收在跑。
  int _trimBusy=0;
  void ToggleTrimOnReady(){ trimOnReady=!trimOnReady; RefreshChecks(); SaveCfg(); logForm.Append("模型就绪后自动回收内存: "+(trimOnReady?"开":"关")+"\r\n"); }
  void ToggleTrimIdle(){ trimIdle=!trimIdle; RefreshChecks(); SaveCfg(); logForm.Append("空闲自动回收内存: "+(trimIdle?("开（每 "+MemTrim.IntervalLabel(trimIntervalSec)+"）"):"关")+"\r\n"); }
  void ToggleSlotSave(){ slotSaveOn=!slotSaveOn; RefreshChecks(); SaveCfg(); logForm.Append("硬盘 KV 缓存（--slot-save-path "+slotSavePath+"）: "+(slotSaveOn?"开":"关")+"（重启对应模型后生效）\r\n"); }

  // 对**一个**服务做一次工作集回收。返回 (是否成功, 释放字节)。
  // 进程句柄优先用自己 Process.Start 起的；句柄丢了（外部实例/接管失败）按端口兜底。
  (bool ok,long freed) TrimOne(TrimKind kind, Service svc){
    int pid=0; try{ if(svc.Running&&svc.proc!=null) pid=svc.proc.Id; }catch{}
    if(pid<=0) pid=WinMem.PidByPort(svc.Port);
    if(pid<=0){ string f0=MemTrim.ResultLine(kind,svc.Name,false,0,0); svc.lastTrimLine=f0; svc.lastTrimMs=Environment.TickCount; logForm.Append(svc.lastTrimLine+"\r\n"); return (false,0); }
    long b=0,a=0; bool ok=false;
    try{ ok=WinMem.TrimWorkingSet(pid,out b,out a); }catch{ ok=false; }
    long freed=(ok&&b>0&&a>0&&b>a)?(b-a):0;
    svc.lastTrimMs=Environment.TickCount; svc.lastTrimLine=MemTrim.ResultLine(kind,svc.Name,ok,b,a);
    logForm.Append(svc.lastTrimLine+"\r\n");
    if(ok) AppendTrimLog(kind,svc.Name,pid,b,a);
    return (ok,freed);
  }
  // 对全部运行中的模型回收一次。返回回收成功的服务数。
  int TrimAll(TrimKind kind){
    int n=0;
    foreach(var svc in services){ if(!svc.Running) continue; try{ if(TrimOne(kind,svc).ok) n++; }catch{} }
    lastTrimMs=Environment.TickCount;
    Ui(()=>{ RefreshChecks(); RefreshSvcMenus(); });
    if(kind==TrimKind.Manual) logForm.Append("内存回收完成：成功 "+n+" 个服务\r\n");
    return n;
  }
  // 回收台账（ndjson，一行一次）：用户要用它判断"是不是每次都能松手"、以及自动回收有没有真的在跑。
  readonly object _trimLogLock=new object();
  void AppendTrimLog(TrimKind kind,string name,int pid,long before,long after){
    try{
      string dir=Path.GetDirectoryName(MemTrim.DefaultLogPath) ?? "";
      if(dir.Length==0) return;
      Directory.CreateDirectory(dir);
      var line=System.Text.Json.JsonSerializer.Serialize(new Dictionary<string,object>{
        ["ts"]=DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        ["event"]="mem-trim", ["kind"]=kind.ToString(), ["service"]=name, ["pid"]=pid,
        ["beforeBytes"]=before, ["afterBytes"]=after, ["freedBytes"]=before>after?before-after:0,
      });
      lock(_trimLogLock) File.AppendAllText(MemTrim.DefaultLogPath, line+"\n");
    }catch{}
  }
  // 内存回收节拍（在 1s 心跳上调用；重活都丢给后台线程）
  void TrimTick(){    // ① 模型就绪后一次
    if(trimOnReady){
      foreach(var svc in services){
        if(!svc.pendingTrimOnReady) continue;
        if(System.Threading.Interlocked.Exchange(ref _trimBusy,1)==1) return;   // 已有回收在跑 ⇒ 下个节拍再来（标志不清，不会丢）
        svc.pendingTrimOnReady=false; var t=svc;
        Bg(()=>{ try{ if(MemTrim.AllowOnReady(trimOnReady, SvcProbe.Busy(t.Port))) TrimOne(TrimKind.OnReady,t); } finally{ System.Threading.Interlocked.Exchange(ref _trimBusy,0); } });
      }
    }
    // ② 空闲自动：每 30s 评估一次（最小档间隔 5 分钟 ⇒ 30s 粒度足够，也不必每秒打 HTTP）
    if(++trimTick<30) return;
    trimTick=0;
    if(!trimIdle) return;
    if(System.Threading.Interlocked.Exchange(ref _trimBusy,1)==1) return;
    long since = lastTrimMs>0 ? (Environment.TickCount-lastTrimMs) : -1;
    Bg(()=>{ try{
      bool busy=false; foreach(var s2 in services){ if(s2.Running && SvcProbe.Busy(s2.Port)){ busy=true; break; } }
      if(MemTrim.AllowIdle(trimIdle,busy,since,trimIntervalSec)) TrimAll(TrimKind.Idle);
    } finally{ System.Threading.Interlocked.Exchange(ref _trimBusy,0); } });
  }
  // 旧的两套「点击不收起菜单」实现已删除：其一只拦 ItemClicked（漏 AppFocusChange ⇒ 真鼠标下留孤儿子菜单窗口），
  // 其二用标志位法基于被实测推翻的"Click 先于 Closing"假设、且标志跨点击泄漏会造成语义反转（点③类项反被留开）。
  // 两者都已被 MenuKeepOpen 统一裁决器（严格超集）取代，见 MenuKeepOpen.cs 与 DEVELOPMENT.md §11.8。
  // GPU 编辑（2026-09-26）：有选中模型 → 在**生效选择的深拷贝**上改、只写该模型配置；
  //   ⛔ 不改全局 gpuSel（那是其他未覆盖模型的基线，改了 = 泄漏）。无模型 → 原样改全局。
  GpuSelection GpuEditTarget(){ return selSvc!=null ? CurEff().GpuOrDefault(gpuSel).Clone() : gpuSel; }
  void SetGpuAll(){ var g=GpuEditTarget(); g.UseAll=true; g.UseCpu=false; g.Indices.Clear(); PersistGpuSel(g,"GPU 全部（GPU）"); }
  void SetGpuCpu(){ var g=GpuEditTarget(); g.UseAll=false; g.UseCpu=true; g.Indices.Clear(); PersistGpuSel(g,"GPU CPU（CPU 卸载全部层）"); }
  void ToggleGpu(int idx){
    var g=GpuEditTarget();
    g.UseCpu=false; g.UseAll=false;
    if(g.Indices.Contains(idx)) g.Indices.Remove(idx); else g.Indices.Add(idx);
    if(g.Indices.Count==0) g.UseAll=true; // 全不选 → 全部（GPU）
    PersistGpuSel(g,g.ShortLabel());
  }
  // GPU 落盘：值 = 编辑后的副本（有模型时≠gpuSel）；标签统一 ShortLabel()（菜单标题/CfgString 同源）。
  void PersistGpuSel(GpuSelection g,string label){
    if(selSvc==null) gpuSel=g;
    RefreshChecks(); SaveCfg();
    PersistParam(sc=>sc.GpuSel=g.CfgString(),"GPU 分配: "+label+"（重启对应模型后生效）");
  }
  // 会话入口文案必须能预测行为：builtinRender 勾选后，「启动 DSH 会话」与「最近会话」里的会话实际都走
  // 托盘内置渲染（门控见 OpenPop / OpenSessionById）。标签不跟着变 = 说一套做一套（执行鸿沟）——
  // 用户无法从标签预判，只能先点坏一次。文案/后缀/tooltip 的唯一源 = Core 的 SessionEntryLabels（带单测）。
  // 会话入口文案必须能预测行为：builtinRender 勾选后，两个会话入口实际都走托盘内置渲染。
  // ⚠️ 文案唯一源 = Core 的 SessionEntryLabels；2026-09-26 起入口从"每模型二级菜单"上移为
  //    「当前模型」组里的两条（作用于当前选中模型），这里改刷这两个，不再遍历 svcMenus。
  void ApplySessionEntryLabels(){
    if(curOpenDsh!=null){
      curOpenDsh.Text=SessionEntryLabels.OpenDshText(builtinRender);
      curOpenDsh.ToolTipText=SessionEntryLabels.OpenDshTooltip(builtinRender);
    }
    if(curOpenBuiltin!=null) curOpenBuiltin.Text=SessionEntryLabels.OpenBuiltinText();   // 与开关无关：这条恒走内置渲染
    if(recentHeader!=null) recentHeader.Text=SessionEntryLabels.RecentHeaderText(builtinRender);
  }
  // 当前选中模型生效的那套参数（渲染菜单标题用：显示的是"它现在会按什么启动"，不是全局默认）
  SvcParams CurEff(){
    var sp=(selSvc!=null?selSvc.Spec:null) ?? new ServiceSpec();
    return SvcParams.Resolve(sp,ctxVal,kvMode,cacheRam,splitMode,tsGpu1,mtpLevel,paramMode,bindAll,gpuSel.CfgString());
  }
  ServiceConfig? FindServiceConfig(string name,int port){
    foreach(var sc in cfg.Services) if(string.Equals(sc.Name,name,StringComparison.OrdinalIgnoreCase)) return sc;
    foreach(var sc in cfg.Services) if(sc.Port==port) return sc;
    return null;
  }
  // —— 参数面板的落盘语义（2026-09-26）——
  // 旧：菜单改的是"启动时覆盖所有模型"的全局值（只落在 dsh-tray.cfg，重启托盘还在，换模型照样生效）。
  // 新：**改的就是当前选中模型的配置**，并立刻写回 dsh-tray-config.json 对应的服务项 ⇒
  //     语义从"启动时覆盖"变成"修改对应模型配置的持久化配置"。
  // ⚠️ 传 cfg 本体（不是重建的默认配置）—— Config.Save 是**整份**覆盖写，传错会抹掉手工配置。
  // 探针专用（A25）：程序化点击参数项时不落盘（CurSvc() 有 services[0] 兜底，"置空 selSvc"已拦不住 PersistParam）
  internal bool PersistSuppressed;
  void PersistParam(Action<ServiceConfig> apply,string label){
    if(PersistSuppressed) return;
    var s=CurSvc(); if(s==null) return;
    var sc=FindServiceConfig(s.Name,s.Port); if(sc==null) return;
    try{ apply(sc); }catch{}
    // 同步内存 Spec（9 项一起搬）：显示层（面板标题/勾选/滑块/「运行时配置」行/头部覆盖数）读的是
    // Spec，不搬的话要等重启托盘重读 config 才可见 —— 用户改完立即就该看到（2026-09-26）。
    s.Spec.Ctx=sc.Ctx; s.Spec.KvMode=sc.KvMode; s.Spec.CacheRam=sc.CacheRam; s.Spec.SplitMode=sc.SplitMode;
    s.Spec.TsGpu1=sc.TsGpu1; s.Spec.MtpLevel=sc.MtpLevel; s.Spec.ParamMode=sc.ParamMode; s.Spec.BindAll=sc.BindAll;
    s.Spec.GpuSel=sc.GpuSel??"";
    Config.Save(cfg);
    // ⚠️ 必须在 Spec 同步**之后**再刷显示（2026-09-26 用户实测「点 MTP4 没反应、点 MTP3 才跳到 MTP4」）：
    //   setter 里的 RefreshChecks 跑在本方法之前，读的是同步前的旧 Spec ⇒ 面板永远显示**上一次**点击的值
    //   （慢一拍）。这里补一次刷新，显示立即 = 本次写入的值。幂等，多刷一次无副作用。
    RefreshChecks();
    try{ logForm.Append(label+" → 已存到模型「"+s.Name+"」的配置（dsh-tray-config.json）；重启该模型生效\r\n"); }catch{}
  }
  void RefreshChecks(){
    // 2026-09-26：面板显示 = 当前选中模型的**生效值**（本模型覆盖优先、未覆盖回落托盘默认）。
    // 切换模型 ⇒ SelectSvc→RefreshChecks ⇒ 面板自动跟随（用户令：配置跟着模型走）。
    // 没选中模型（仅探针存在；运行态恒选中，默认第一个）⇒ CurEff() 就是全局默认本身，同一代码路径。
    var pEff=CurEff();
    int dCtx=pEff.Ctx, dKv=pEff.KvMode, dRam=pEff.CacheRam, dSplit=pEff.SplitMode, dTs=pEff.TsGpu1, dMtp=pEff.MtpLevel, dPm=pEff.ParamMode;
    bool dBind=pEff.BindAll;
    var dGpu=pEff.GpuOrDefault(gpuSel);
    if(gpuAllItem!=null) gpuAllItem.Checked = dGpu.UseAll && !dGpu.UseCpu;
    if(gpuCpuItem!=null) gpuCpuItem.Checked = dGpu.UseCpu;
    foreach(var t in gpuItems) t.item.Checked = !dGpu.UseCpu && !dGpu.UseAll && dGpu.Indices.Contains(t.idx);
    for(int i=0;i<ctxItems.Count;i++) ctxItems[i].Checked = (LaunchArgs.CtxOptions[i]==dCtx);
    if(pm0!=null) pm0.Checked=(dPm==0); if(pm1!=null) pm1.Checked=(dPm==1); if(pm2!=null) pm2.Checked=(dPm==2);
    if(splitLayerItem!=null) splitLayerItem.Checked=(dSplit==0);
    if(splitRowItem!=null) splitRowItem.Checked=(dSplit==1);
    if(kvDefItem!=null) kvDefItem.Checked=(dKv==0);
    if(kv8Item!=null) kv8Item.Checked=(dKv==1);
    if(kv16Item!=null) kv16Item.Checked=(dKv==2);
    for(int i=0;i<cacheRamItems.Count;i++) cacheRamItems[i].Checked=(LaunchArgs.CacheRamOptions[i]==dRam);
    for(int i=0;i<mtpItems.Count;i++) mtpItems[i].Checked=(i==dMtp);
    if(mtpMenu!=null) mtpMenu.Text="MTP: "+MtpLabel(dMtp);
    // —— 内存与缓存 ——
    if(trimOnReadyItem!=null) trimOnReadyItem.Checked=trimOnReady;
    if(trimIdleItem!=null) trimIdleItem.Checked=trimIdle;
    for(int i=0;i<trimIntervalItems.Count;i++){ trimIntervalItems[i].Checked=(MemTrim.IdleIntervalOptions[i]==trimIntervalSec); trimIntervalItems[i].Enabled=trimIdle; }
    if(slotSaveItem!=null) slotSaveItem.Checked=slotSaveOn;
    if(warmupOnReadyItem!=null) warmupOnReadyItem.Checked=warmupOnReady;
    if(warmupStatusItem!=null) warmupStatusItem.Text="预热："+WarmupStatusText();
    if(trimStatusItem!=null) trimStatusItem.Text="回收："+(lastTrimMs>0 ? ("上次 "+((Environment.TickCount-lastTrimMs)/60000)+" 分钟前") : "尚未执行")
      +"  |  策略 "+MemTrim.PolicyText(trimOnReady,trimIdle,trimIntervalSec);
    if(vram0Label!=null) VramSplitUpdate(dCtx,dTs);
    if(bindItem!=null) bindItem.Checked=dBind;
    if(builtinRenderItem!=null) builtinRenderItem.Checked=builtinRender;
    ApplySessionEntryLabels();   // 会话入口文案随开关变：标签必须能预测行为（否则=执行鸿沟）
    if(safeModeItem!=null) safeModeItem.Checked=safeMode;
    if(gpuMenu!=null) gpuMenu.Text="GPU: "+dGpu.ShortLabel();
    if(ctxMenu!=null) ctxMenu.Text="上下文: "+(dCtx/1024)+"K";
    if(pm!=null) pm.Text="推理参数组："+ParamLabel(dPm);
    if(splitMenu!=null) splitMenu.Text="切分模式："+(dSplit==0?"按层切分":"张量并行("+dTs+"%)");
    if(kvMenu!=null) kvMenu.Text="KV 缓存: "+KvLabel(dKv);
    if(cacheRamMenu!=null) cacheRamMenu.Text="缓存内存: "+CacheRamLabel(dRam);
    // 滑块位置与「GPU1 占比」标签跟随生效值（tsSync 防程序化 SetValue 被当成用户拖拽）
    if(tsLabelItem!=null) tsLabelItem.Text="GPU1 占比: "+dTs+"%（张量并行）";
    if(tsBarCtl!=null && tsBarCtl.Value!=dTs){
      tsSync=true;
      try{ tsBarCtl.Value=Math.Min(Math.Max(dTs,tsBarCtl.Minimum),tsBarCtl.Maximum); }catch{}
      tsSync=false;
    }
    if(svcMenus.Count>0) RefreshSvcMenus();   // 参数变化时同步刷新每模型二级菜单的「运行时配置」行
  }
  // 同上：实现已搬进 SvcLines.cs，这里是迁移期兼容层（调用点零改动）
  static string ParamLabel(int m){ return SvcLines.ParamLabel(m); }
  static string MtpLabel(int m){ return SvcLines.MtpLabel(m); }
  // 张量并行时两卡显存估算（GPU0/GPU1 分两行各 1 位小数）：模型权重(GGUF 大小) + KV(按 ctx) + 每卡计算缓冲(≈1.1GB) 按 -ts 比例分摊。
  // 细节（参考模型/ctx/占比）放 ToolTip；可见文本保持短且固定宽度，避免拖动滑块时子菜单宽度抖动（布局震荡）。仅供预览，实际以 llama-server 加载报告为准。
  void VramSplitUpdate(int ctxEff,int tsEff){
    try{
      Service s = services.FirstOrDefault(x=>x.Running) ?? services.FirstOrDefault();
      string txt0="GPU0≈—", txt1="GPU1≈—", tip="张量并行两卡显存估算（1 位小数）：模型权重+KV(按ctx)+每卡计算缓冲按 -ts 比例分摊；实际以 llama-server 加载报告为准。";
      if(s!=null && !string.IsNullOrEmpty(s.Model) && File.Exists(s.Model)){
        double modelGiB = new FileInfo(s.Model).Length / 1073741824.0;
        double kvGiB = 3.0 * ctxEff / 262144.0;   // 35B 实测 256K(q8_0) KV≈3GB，按 ctx 线性近似
        double compute = 1.1;                     // 计算缓冲每卡约 1.1GB（35B 量级）
        double g0 = modelGiB*(100-tsEff)/100.0 + kvGiB*(100-tsEff)/100.0 + compute;
        double g1 = modelGiB*tsEff/100.0 + kvGiB*tsEff/100.0 + compute;
        txt0="GPU0≈"+g0.ToString("0.0")+"GB";
        txt1="GPU1≈"+g1.ToString("0.0")+"GB";
        tip="参考: "+Short(s)+" "+(ctxEff/1024)+"K | 模型权重(按GGUF)+KV(按ctx)+每卡计算缓冲≈1.1GB，按 -ts "+(100-tsEff)+"/"+tsEff+" 分摊；实际以 llama-server 加载报告为准。";
      }
      if(vram0Label!=null){ vram0Label.Text=txt0; vram0Label.ToolTipText=tip; }
      if(vram1Label!=null){ vram1Label.Text=txt1; vram1Label.ToolTipText=tip; }
    }catch{}
  }

}

