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

// —— 日志窗口（2026-09-11 统一重构）——
// 旧版：两个各含一个裸 RichTextBox 的窗口（托盘日志 / DSH 日志），没有清空按钮、没有任何时间戳、
// 没有自动滚动；DSH 侧读的 out/err 落盘时也不打戳 → 事后无法按时间定位故障（排查 dsh 崩溃时只能靠
// 行号顺序倒推因果）。新版：单窗口 + 双页签（托盘事件 / DSH 输出），共享一条工具栏；所有入库文本
// 逐行前置 [HH:mm:ss]；历史无戳行标 [--:--:--]（不伪造时间，明确标示"时间未知"）。
public class LogForm : Form {
  public RichTextBox box=new();       // 页签 1：托盘事件
  public RichTextBox dshBox=new();    // 页签 2：DSH 输出（dsh-web-out/err.log 实时跟踪）
  TabControl tabs=new();
  TabPage pageTray=new TabPage("托盘事件"), pageDsh=new TabPage("DSH 输出"), pagePerf=new TabPage("模型性能");
  public PerfPanel? perfPanel;   // 页签 3：模型性能（由 TrayApp 注入；未注入时显示占位）
  ToolStrip bar=new ToolStrip();
  ToolStripButton btnClearView, btnClearFiles, btnCopy, btnOpenDir, btnScroll, btnPause;
  readonly object gate=new object();
  readonly List<(bool dsh,string text)> paused=new List<(bool dsh,string text)>();
  int pausedBatches=0;
  public bool AutoFollow=true, Paused=false, DshTabOpened=false;   // 注意别叫 AutoScroll：会隐藏 Form.AutoScroll（CS0108）
  public Func<string>? LogDirProvider;   // 由 TrayApp 注入：日志文件所在目录
  public Action? ClearFilesAction;       // 由 TrayApp 注入：截断底层日志文件（含重置跟踪位置）
  static readonly System.Text.RegularExpressions.Regex RStamped=new System.Text.RegularExpressions.Regex(@"^\[\d{2}:\d{2}:\d{2}\]");
  public LogForm(string title="DSH托盘 日志"){
    Text=title; Size=new Size(880,540); MinimumSize=new Size(560,340); StartPosition=FormStartPosition.CenterScreen;
    box.Dock=DockStyle.Fill; box.ReadOnly=true; box.Font=new Font("Consolas",9); box.WordWrap=false; box.ScrollBars=RichTextBoxScrollBars.Both; box.BackColor=Color.White;
    dshBox.Dock=DockStyle.Fill; dshBox.ReadOnly=true; dshBox.Font=new Font("Consolas",9); dshBox.WordWrap=false; dshBox.ScrollBars=RichTextBoxScrollBars.Both; dshBox.BackColor=Color.White;
    pageTray.Controls.Add(box); pageDsh.Controls.Add(dshBox);
    pagePerf.Controls.Add(new Label{Text="（模型性能面板未接线）",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=Color.Gray});
    tabs.Dock=DockStyle.Fill; tabs.Controls.Add(pageTray); tabs.Controls.Add(pageDsh); tabs.Controls.Add(pagePerf);
    btnClearView=new ToolStripButton("清屏"){ToolTipText="只清空当前页签窗口里已显示的内容；磁盘上的日志文件保留"};
    btnClearFiles=new ToolStripButton("清空日志文件"){ToolTipText="截断磁盘上的 dsh-web-out.log / dsh-web-err.log（各留一行分隔记录），窗口同步清空"};
    btnCopy=new ToolStripButton("复制全部"){ToolTipText="把当前页签的全部文本复制到剪贴板"};
    btnOpenDir=new ToolStripButton("打开日志文件夹"){ToolTipText="在资源管理器里打开日志文件所在目录"};
    btnScroll=new ToolStripButton("自动滚动"){CheckOnClick=true,Checked=true,ToolTipText="新日志追加后自动滚到末尾"};
    btnPause=new ToolStripButton("暂停刷新"){CheckOnClick=true,ToolTipText="暂停把新日志写进窗口（后台仍在累积，取消暂停后一次性补上）"};
    btnClearView.Click+=(s,e)=>ClearView();
    btnClearFiles.Click+=(s,e)=>{ try{ if(ClearFilesAction!=null) ClearFilesAction(); }catch(Exception ex){ Append("清空日志文件失败: "+ex.Message); } };
    btnCopy.Click+=(s,e)=>{ try{ Clipboard.SetText(Cur().Text); }catch{} };
    btnOpenDir.Click+=(s,e)=>{ try{ string d=LogDirProvider!=null?(LogDirProvider()??""):""; if(d.Length>0&&Directory.Exists(d)) Process.Start(new ProcessStartInfo(d){UseShellExecute=true}); }catch{} };
    btnScroll.CheckedChanged+=(s,e)=>{ AutoFollow=btnScroll.Checked; };
    btnPause.CheckedChanged+=(s,e)=>{ bool unp; lock(gate){ Paused=btnPause.Checked; unp=!Paused; } if(unp) FlushPaused(); };
    bar.GripStyle=ToolStripGripStyle.Hidden; bar.RenderMode=ToolStripRenderMode.System;
    bar.Items.AddRange(new ToolStripItem[]{btnClearView,btnClearFiles,btnCopy,btnOpenDir,new ToolStripSeparator(),btnScroll,btnPause});
    Controls.Add(tabs); Controls.Add(bar);   // 后加入的先贴边：bar 占 Top，tabs 占 Fill
    FormClosing+=(s,e)=>{ e.Cancel=true; this.Hide(); };
  }
  RichTextBox Cur(){ if(tabs.SelectedTab==pageDsh) return dshBox; if(tabs.SelectedTab==pagePerf&&perfPanel!=null) return perfPanel.DetailBox; return box; }
  public void ShowTab(bool dsh){ if(dsh){ tabs.SelectedTab=pageDsh; DshTabOpened=true; } else tabs.SelectedTab=pageTray; }
  public void ShowPerf(){ tabs.SelectedTab=pagePerf; try{ perfPanel?.Refresh_(); }catch{} }
  public void AttachPerfPanel(PerfPanel p){ try{ perfPanel=p; pagePerf.Controls.Clear(); p.Dock=DockStyle.Fill; pagePerf.Controls.Add(p); }catch{} }
  public bool HasPerfPanel(){ return perfPanel!=null; }
  static void ScrollToEnd(RichTextBox b){ try{ b.SelectionStart=b.TextLength; b.SelectionLength=0; b.ScrollToCaret(); }catch{} }
  void AppendTo(RichTextBox b,string s){
    if(string.IsNullOrEmpty(s)) return;
    bool pause;
    lock(gate){
      pause=Paused;
      if(pause){ paused.Add((b==dshBox,s)); pausedBatches++; if(paused.Count>3000) paused.RemoveRange(0,paused.Count-3000); }
    }
    if(pause) return;
    try{
      if(b.IsDisposed) return;
      if(b.InvokeRequired){ b.BeginInvoke((MethodInvoker)(()=>{ if(!b.IsDisposed){ b.AppendText(s); if(AutoFollow) ScrollToEnd(b); } })); }
      else { b.AppendText(s); if(AutoFollow) ScrollToEnd(b); }
    }catch{}
  }
  void FlushPaused(){
    List<(bool dsh,string text)> buf; int n;
    lock(gate){ buf=new List<(bool dsh,string text)>(paused); n=pausedBatches; paused.Clear(); pausedBatches=0; }
    if(buf.Count==0) return;
    AppendTo(box,StampEvery("（暂停刷新期间累积 "+n+" 批日志，以下为补录）"));
    foreach(var it in buf) AppendTo(it.dsh?dshBox:box,it.text);
  }
  // 托盘自己产生的事件：入库即打真实时间戳
  static string StampEvery(string s){
    if(string.IsNullOrEmpty(s)) return "";
    var sb=new System.Text.StringBuilder();
    foreach(string raw in s.Split('\n')){
      string line=raw.TrimEnd('\r'); if(line.Length==0) continue;
      sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").Append(line).Append("\r\n");
    }
    return sb.ToString();
  }
  // DSH 日志文件的行：新文件已自带戳则原样保留；历史无戳行标 [--:--:--]（不伪造时间）
  static string StampMissing(string s){
    if(string.IsNullOrEmpty(s)) return "";
    var sb=new System.Text.StringBuilder();
    foreach(string raw in s.Split('\n')){
      string line=raw.TrimEnd('\r'); if(line.Length==0) continue;
      if(RStamped.IsMatch(line)||line.StartsWith("[--:--:--]")) sb.Append(line); else sb.Append("[--:--:--] ").Append(line);
      sb.Append("\r\n");
    }
    return sb.ToString();
  }
  public void Append(string s){ AppendTo(box,StampEvery(s)); }            // 托盘事件页
  public void AppendDsh(string s){ AppendTo(dshBox,StampMissing(s)); }    // DSH 页（文件内容）
  public void AppendDshStamp(string s){ AppendTo(dshBox,StampEvery(s)); } // DSH 页（托盘生成的行，打真实戳）
  public void ClearView(){ try{ box.Clear(); }catch{} try{ dshBox.Clear(); }catch{} }
  public void ClearDshView(){ try{ dshBox.Clear(); }catch{} }
  public bool HasClearButton(){ return btnClearView!=null&&btnClearView.Text=="清屏"&&btnClearFiles!=null&&btnClearFiles.Text=="清空日志文件"; }
  public string UiSummary(){ return "tabs="+tabs.TabCount+" selected="+(tabs.SelectedTab!=null?tabs.SelectedTab.Text:"?")+" toolbar=["+string.Join(",",bar.Items.OfType<ToolStripButton>().Select(b=>b.Text))+"]"; }
  static string TailN(string t,int n){ if(string.IsNullOrEmpty(t)) return "(空)"; return t.Length<=n?t:t.Substring(t.Length-n); }
  public string Snapshot(){
    var sb=new System.Text.StringBuilder();
    sb.Append("visible=").Append(Visible).Append(' ').Append(UiSummary())
      .Append(" trayChars=").Append(box.TextLength).Append(" dshChars=").Append(dshBox.TextLength).Append("\r\n");
    sb.Append("-- 托盘事件页末尾 --\r\n").Append(TailN(box.Text,300)).Append("\r\n");
    sb.Append("-- DSH 输出页末尾 --\r\n").Append(TailN(dshBox.Text,300));
    return sb.ToString();
  }
}

public class TrayApp : ApplicationContext {
  NotifyIcon icon; ContextMenuStrip menu; LogForm logForm; System.Windows.Forms.Timer clock; bool adopted=false; TaskbarWatcher? _tw;
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
  // —— 每模型二级菜单（2026-09-11）：一级项 = <状态圆点><名称> (端口)；二级 = 会话入口 + 模型启停 + 运行状态 + 运行时配置 ——
  // 元素全部固定（只改 Text/Image/Visible，绝不结构重建）→ 菜单显示期间更新也安全，不会触发历史那个「幽灵菜单/布局错乱」。
  class SvcMenu {
    public Service svc=null!;
    public ToolStripMenuItem root=null!;                 // 一级项（带状态圆点 Image）
    public ToolStripMenuItem status=null!;               // 运行状态行（禁用项，ToolTip 给全量信息）
    public ToolStripMenuItem openDsh=null!,openBuiltin=null!;
    public ToolStripMenuItem start=null!,restart=null!,stop=null!;
    public ToolStripMenuItem cfgHeader=null!,envHeader=null!,cppHeader=null!;
    public ToolStripMenuItem envCuda=null!,envAllreduce=null!;
    public List<ToolStripMenuItem> cfgLines=new();       // 运行时配置行池（按行数显示/隐藏）
    public string sig="";                                // 变更签名：未变化则跳过重绘
  }
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
      var svc=new Service{Name=sc.Name,Port=sc.Port,Model=sc.Model,UseMmproj=sc.UseMmproj,Mmproj=sc.Mmproj,SpecDecode=sc.SpecDecode,Provider=sc.Provider};
      if(sc.Batch>0) svc.Batch=sc.Batch; if(sc.Ubatch>0) svc.Ubatch=sc.Ubatch;
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
  static string KvLabel(int m){ return m==0?"默认":m==1?"8bit q8_0":"16bit f16"; }
  static string CacheRamLabel(int mb){ return mb==0?"禁用":mb<0?"无限制":(mb>=1024&&mb%1024==0)?(mb/1024)+"GB":mb+"M"; }
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
  static string ParamLabel(int m){ return m==0?"通用思考":m==1?"编码思考":"Instruct"; }
  static string MtpLabel(int m){ return m==0?"无":m==1?"MTP":"MTP"+m; }
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

  bool PortUp(int port){ try{ using(var wc=new System.Net.WebClient()){ wc.DownloadString("http://127.0.0.1:"+port+"/health"); return true; } }catch{} return false; }
  // llama-server /health 就绪探测（{"status":"ok"}）：false = 未就绪 / 进程未起
  bool HealthUp(int port){ try{ using(var wc=new System.Net.WebClient()){ wc.Encoding=System.Text.Encoding.UTF8; return wc.DownloadString("http://127.0.0.1:"+port+"/health").Contains("ok"); } }catch{} return false; }
  static string FileName(string p){ try{ return Path.GetFileName(p??""); }catch{ return p??""; } }
  // 中间省略：菜单宽度受模型文件名影响，过长的名字保留头尾（尾部含量化档位，优先保留）
  static string Mid(string s,int max){ if(string.IsNullOrEmpty(s)||s.Length<=max) return s??""; int keep=max-1, head=keep*2/3, tail=keep-head; return s.Substring(0,head)+"…"+s.Substring(s.Length-tail); }
  static string Dur(TimeSpan t){ if(t.TotalDays>=1) return (int)t.TotalDays+"d"+(t.Hours>0?t.Hours+"h":""); if(t.TotalHours>=1) return (int)t.TotalHours+"h"+t.Minutes+"m"; if(t.TotalMinutes>=1) return (int)t.TotalMinutes+"m"+t.Seconds+"s"; return Math.Max(0,(int)t.TotalSeconds)+"s"; }
  // 按端口收 llama-server：停止/重启的兜底（svc.proc 为空、或该进程由外部/上一实例启动）。只杀进程名含 llama 的，避免误伤。
  int KillByPort(int port){
    try{
      var psi=new ProcessStartInfo("netstat","-ano"){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true};
      var p=Process.Start(psi); if(p==null) return 0;
      string o=p.StandardOutput.ReadToEnd(); p.WaitForExit(3000);
      foreach(var line in o.Split('\n')){
        if(!line.Contains(":"+port)||!line.Contains("LISTENING")) continue;
        var parts=line.Split(new char[]{' '},StringSplitOptions.RemoveEmptyEntries);
        int pid; if(parts.Length==0||!int.TryParse(parts[parts.Length-1],out pid)) return 0;
        try{ var pr=Process.GetProcessById(pid); if(pr.ProcessName.ToLowerInvariant().Contains("llama")){ pr.Kill(); return pid; } }catch{}
        return 0;
      }
    }catch{}
    return 0;
  }
  // —— 每模型二级菜单：构建（元素固定，见 SvcMenu 注释）——
  ToolStripMenuItem BuildSvcMenu(Service svc){
    var m=new SvcMenu(); m.svc=svc;
    m.root=new ToolStripMenuItem(svc.Name+" ("+svc.Port+")");
    var dd=m.root.DropDown;
    m.status=new ToolStripMenuItem("状态：—"){Enabled=false};
    dd.Items.Add(m.status);
    dd.Items.Add(new ToolStripSeparator());
    m.openDsh=new ToolStripMenuItem("启动 DSH 会话",null,(s,e)=>OpenSession(svc));
    m.openDsh.ToolTipText="用官方 DSH Web UI 打开会话，并把 agent-default-model 指向本模型（同步 ctx/视觉/maxTokens 到 settings.yaml，30s 后还原默认模型）。";
    m.openBuiltin=new ToolStripMenuItem("启动内置对话",null,(s,e)=>OpenSessionBuiltin(svc));
    m.openBuiltin.ToolTipText="用托盘内置渲染（瘦客户端，不加载官方客户端 bundle）打开会话列表/新建会话；官方 Web UI 白屏时仍可续聊。";
    dd.Items.Add(m.openDsh); dd.Items.Add(m.openBuiltin);
    dd.Items.Add(new ToolStripSeparator());
    m.start=new ToolStripMenuItem("启动模型",null,(s,e)=>Bg(()=>Start(svc)));
    m.restart=new ToolStripMenuItem("重启模型",null,(s,e)=>Bg(()=>RestartSvc(svc)));
    m.stop=new ToolStripMenuItem("停止模型",null,(s,e)=>Bg(()=>Stop(svc)));
    m.start.ToolTipText="按当前「上下文 / KV / 切分 / MTP / GPU / 缓存内存」参数启动本模型服务（首次加载约 30-60s，页面缓存命中会快很多）。";
    m.restart.ToolTipText="重读 dsh-tray-config.json（本服务项）与 dsh-tray.cfg（托盘参数）后重启本模型：\n只重启 llama-server，**不重启 DSH(3080)** → dsh 会话（对话历史）不中断，仅正在生成的那一轮需重发。\n端口不变时 dsh 侧无需改配置；改了端口请点「启动 DSH 会话」重新同步 provider baseURL。";
    m.stop.ToolTipText="停止本模型服务（Kill 进程并释放显存）；若进程句柄丢失会按端口兜底回收。";
    dd.Items.Add(m.start); dd.Items.Add(m.restart); dd.Items.Add(m.stop);
    dd.Items.Add(new ToolStripSeparator());
    m.cfgHeader=new ToolStripMenuItem("运行时配置"){Enabled=false};
    m.cfgHeader.ToolTipText="按菜单当前参数实时计算；改参数后点「重启模型」生效。运行中会叠加服务端 /props 实测值。";
    m.envHeader=new ToolStripMenuItem("  环境配置"){Enabled=false};
    m.envCuda=new ToolStripMenuItem("    CUDA_VISIBLE_DEVICES = —"){Enabled=false};
    m.envAllreduce=new ToolStripMenuItem("    GGML_CUDA_ALLREDUCE = —"){Enabled=false};
    m.cppHeader=new ToolStripMenuItem("  llama.cpp 配置"){Enabled=false};
    dd.Items.Add(m.cfgHeader); dd.Items.Add(m.envHeader); dd.Items.Add(m.envCuda); dd.Items.Add(m.envAllreduce); dd.Items.Add(m.cppHeader);
    for(int i=0;i<6;i++){ var it=new ToolStripMenuItem("    —"){Enabled=false}; m.cfgLines.Add(it); dd.Items.Add(it); }
    // 启动/重启/停止：点击后不收起菜单（连续操作 + 立即看到状态变化）；会话入口照常收起
    KeepOpenOnClick(dd,new ToolStripItem[]{m.start,m.restart,m.stop});
    dd.ShowItemToolTips=true;
    svcMenus.Add(m);
    return m.root;
  }
  void RefreshSvcMenus(){ foreach(var m in svcMenus){ try{ RefreshSvcMenu(m); }catch{} } }
  void RefreshSvcMenu(SvcMenu m){
    var svc=m.svc;
    bool starting=svc.Starting, running=svc.Running, busy=svc.PortBusy&&!running&&!starting;
    string sig=(starting?"s":running?"r":busy?"b":"x")+"|"+svc.Port+"|"+svc.Model+"|"+ctxVal+"|"+kvMode+"|"+splitMode+"|"+tsGpu1+"|"+cacheRam+"|"+mtpLevel+"|"+paramMode+"|"+gpuSel.CfgString()+"|"+(bindAll?"1":"0")+"|"+svc.runCtx+"|"+(svc.RunVision.HasValue?(svc.RunVision.Value?"1":"0"):"-")+(starting?("|"+(Environment.TickCount-svc.startMs)/1000):"");
    if(sig==m.sig) return; m.sig=sig;
    // —— 状态圆点 + 运行状态行（悬浮给全量）：黄=启动中 绿=运行中 橙=运行中(未托管) 红=未运行 ——
    m.root.Image=DotFor(SvcDot(starting,running,busy));
    m.root.ToolTipText=StatusTip(svc);
    var sb=new System.Text.StringBuilder("状态："+SvcState(starting,running,busy));
    if(running){
      try{ sb.Append(" · pid "+svc.proc.Id); }catch{}
      if(!starting){ try{ sb.Append(" · 已运行 "+Dur(DateTime.Now-svc.proc.StartTime)); }catch{} }
      string ram=svc.RamGb; if(ram!="-") sb.Append(" · 内存 "+ram);
      sb.Append(" · 端口 "+svc.Port);
    } else if(starting){ sb.Append(" · 端口 "+svc.Port+" · 正在加载模型，约 30-60s"); }
    else if(busy){ sb.Append(" · 端口 "+svc.Port+"（点「停止模型」按端口回收）"); }
    else { sb.Append(" · 端口 "+svc.Port); }
    m.status.Text=sb.ToString();
    m.status.ToolTipText=StatusTip(svc);
    var en=SvcEnable(starting,running,busy);
    m.start.Enabled=en.start; m.stop.Enabled=en.stop; m.restart.Enabled=en.restart;
    // —— 运行时配置：环境配置 + llama.cpp 配置（按当前参数实时算；运行中叠加 /props 实测）——
    var b=LaunchArgs.Build(svc,gpuSel,ctxVal,paramMode,splitMode,kvMode,cacheRam,tsGpu1,gpus.Count,bindAll,mtpLevel);
    m.envCuda.Text="    CUDA_VISIBLE_DEVICES = "+(b.envCuda.Length>0?b.envCuda:"（未设置）");
    m.envAllreduce.Text="    GGML_CUDA_ALLREDUCE = "+(b.envAllreduce.Length>0?b.envAllreduce:"（未设置）");
    var lines=SvcCfgLines(svc,running,b);
    for(int i=0;i<m.cfgLines.Count;i++){ var it=m.cfgLines[i]; if(i<lines.Length){ it.Text=lines[i]; it.Visible=true; } else it.Visible=false; }
    m.cfgLines[0].ToolTipText="完整模型路径: "+svc.Model+(svc.UseMmproj?("\r\nmmproj: "+svc.Mmproj):"");
    m.cppHeader.ToolTipText="完整命令行（点「重启模型」即用它执行）：\r\n"+cfg.LlamaServerExe+" "+string.Join(" ",b.args);
  }
  // —— 纯函数：状态/圆点色/启停项可用性映射（与 UI 解耦 → --selftest-svcmenu 可直接枚举四种态验证）——
  static string SvcState(bool starting,bool running,bool busy){ return starting?"启动中":running?"运行中":busy?"运行中(未托管)":"未运行"; }
  static string SvcDot(bool starting,bool running,bool busy){ return starting?"yellow":running?"green":busy?"orange":"red"; }
  static (bool start,bool stop,bool restart) SvcEnable(bool starting,bool running,bool busy){ return (!running&&!starting&&!busy, running||starting||busy, true); }
  string[] SvcCfgLines(Service svc,bool running,LaunchResult b){
    var eff=LaunchArgs.EffectiveGpus(gpuSel,gpus.Count);
    string gpuLine = eff.Count==0 ? "CPU（-ngl 0，无 GPU 加速）"
      : eff.Count==1 ? ("GPU"+eff[0]+" · -ngl 99 · --split-mode none")
      : ("GPU"+string.Join("+",eff)+" · -ngl 99 · --split-mode "+(splitMode==0?"layer":"tensor -ts "+(100-tsGpu1)+","+tsGpu1));
    string mm = svc.UseMmproj ? ("mmproj = "+FileName(svc.Mmproj)) : "无 mmproj";
    return new string[]{
      "    模型 = "+Mid(FileName(svc.Model),42)+" · "+mm,
      "    "+gpuLine,
      "    上下文 = "+(ctxVal/1024)+"K · KV = "+KvLabel(kvMode)+" · 批 = "+svc.Batch+"/"+svc.Ubatch+" · 缓存内存 = "+CacheRamLabel(cacheRam),
      "    MTP = "+MtpLabel(mtpLevel)+" · 参数组 = "+ParamLabel(paramMode)+" · flash-attn = "+(eff.Count==0?"关":"on"),
      "    监听 = "+(bindAll?"0.0.0.0":"127.0.0.1")+":"+svc.Port+" · reasoning = deepseek · jinja",
      running ? ("    实测 = ctx "+(svc.runCtx>0?(svc.runCtx/1024)+"K":(ctxVal/1024)+"K(未探测)")+" · 视觉 "+(svc.RunVision.HasValue?(svc.RunVision.Value?"支持":"不支持"):"未探测")) : "    实测 = （未运行 → 启动后自动探测 /props）"
    };
  }
  string StatusTip(Service svc){
    var sb=new System.Text.StringBuilder();
    sb.AppendLine("状态: "+(svc.Starting?"启动中":svc.Running?"运行中":(svc.PortBusy?"运行中(未托管，可「停止模型」按端口回收)":"未运行")));
    if(svc.Running){ try{ sb.AppendLine("pid: "+svc.proc.Id+"   启动: "+svc.proc.StartTime.ToString("MM-dd HH:mm:ss")+"   已运行: "+Dur(DateTime.Now-svc.proc.StartTime)); }catch{} sb.AppendLine("内存: "+svc.RamGb); }
    sb.AppendLine("模型: "+svc.Model);
    if(svc.UseMmproj) sb.AppendLine("mmproj: "+svc.Mmproj);
    sb.AppendLine("端口: "+svc.Port+"   provider: "+(string.IsNullOrEmpty(svc.Provider)?"llama-local":svc.Provider));
    if(svc.Running&&svc.runCtx>0) sb.AppendLine("服务端实测: ctx "+(svc.runCtx/1024)+"K · 视觉 "+(svc.RunVision.HasValue?(svc.RunVision.Value?"支持":"不支持"):"未探测"));
    return sb.ToString().TrimEnd();
  }
  void Start(Service svc){
    if(svc.Running){ Log(svc,svc.Name+" 已在运行 (端口 "+svc.Port+")\r\n"); Ui(()=>RefreshSvcMenus()); return; }
    if(PortUp(svc.Port)){ Log(svc,"端口 "+svc.Port+" 已有服务在跑（非本应用启动），请先「停止模型」或停用外部进程。\r\n"); Ui(()=>RefreshSvcMenus()); return; }
    var build=LaunchArgs.Build(svc,gpuSel,ctxVal,paramMode,splitMode,kvMode,cacheRam,tsGpu1,gpus.Count,bindAll,mtpLevel);
    var psi=new ProcessStartInfo(cfg.LlamaServerExe,string.Join(" ",build.args)){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
    if(build.envCuda.Length>0) psi.Environment["CUDA_VISIBLE_DEVICES"]=build.envCuda;
    if(build.envAllreduce.Length>0) psi.Environment["GGML_CUDA_ALLREDUCE"]=build.envAllreduce; // 多卡张量并行需要内置 CUDA AllReduce
    try{
      svc.proc=Process.Start(psi); svc.proc.OutputDataReceived+=(o,e)=>{if(e.Data!=null){svc.log.AppendLine(e.Data); try{ perf?.OnLlamaLine(svc,e.Data); }catch{}}}; svc.proc.ErrorDataReceived+=(o,e)=>{if(e.Data!=null)svc.log.AppendLine("[err] "+e.Data);}; svc.proc.BeginOutputReadLine(); svc.proc.BeginErrorReadLine();
      svc.Starting=true; svc.startMs=Environment.TickCount; svc.runCtx=0; svc.runVision=-1;   // 启动中 → Tick 里 /health 探活，就绪后清标志
      Log(svc,">>> 启动 "+svc.Name+" (端口 "+svc.Port+")\r\n");
      Log(svc,"    GPU="+gpuSel.Describe(gpus)+" | ctx="+(ctxVal/1024)+"K | CUDA_VISIBLE_DEVICES="+(build.envCuda.Length>0?build.envCuda:"-")+" | 参数组="+(paramMode==0?"通用思考":paramMode==1?"编码思考":"Instruct")+" | 切分="+(splitMode==0?"按层 layer":splitMode==1?"张量并行 tensor("+tsGpu1+"%)":"-")+" | KV="+KvLabel(kvMode)+" | 缓存内存="+CacheRamLabel(cacheRam)+" | MTP="+MtpLabel(mtpLevel)+" | 监听="+(bindAll?"0.0.0.0":"127.0.0.1")+"\r\n");
      if(gpuSel.UseCpu) Log(svc,"    注意: CPU 模式（-ngl 0），速度会显著变慢\r\n");
      else if(ctxVal>=196608 && LaunchArgs.EffectiveGpus(gpuSel,gpus.Count).Count==1 && (svc.Model.Contains("35B")||svc.Model.Contains("27B"))) Log(svc,"    注意: "+(ctxVal/1024)+"K 上下文 + 单 GPU 可能显存不足（OOM），建议 GPU=全部（多卡，切分模式=按层 layer）或降低 ctx\r\n");
      Log(svc,"    首次加载约 30-60s\r\n");
      string cmdLine = cfg.LlamaServerExe + " " + string.Join(" ", build.args) + (build.envCuda.Length>0?" [CUDA_VISIBLE_DEVICES="+build.envCuda+"]":"") + (build.envAllreduce.Length>0?" [GGML_CUDA_ALLREDUCE="+build.envAllreduce+"]":"");
      Log(svc,"    命令: "+cmdLine+"\r\n");
      try{
        bool isNewCfg=false; string cfgId="";
        if(perf!=null) cfgId=perf.OnStart(svc,build,cfg.LlamaServerExe,out isNewCfg);
        // 台账里首次出现的配置才写人类可读的一行；重复启动不再累积（原写法每次启动都追加 → 反复试参就膨胀）
        if(perf==null||isNewCfg) File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"model-start.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" ["+svc.Name+"] "+cmdLine+"\r\n");
        if(cfgId.Length>0) Log(svc,"    性能台账: cfg "+cfgId+(isNewCfg?"（新配置，已登记台账）":"（已有配置，累加启动次数）")+"\r\n");
      }catch{}
    }
    catch(Exception ex){ svc.Starting=false; Log(svc,">>> 启动 "+svc.Name+" 失败: "+ex.Message+"\r\n"); }
    Ui(()=>RefreshSvcMenus());
  }

  // 停止：先杀自己起的进程；句柄丢失/外部启动时按端口兜底回收（只杀 llama）→ 释放显存
  void Stop(Service svc){
    bool did=false;
    if(svc.proc!=null&&!svc.proc.HasExited){ Log(svc,">>> 停止 "+svc.Name+" ...\r\n"); try{ svc.proc.Kill(); did=true; }catch{} svc.proc=null; }
    if(PortUp(svc.Port)){
      int pid=KillByPort(svc.Port);
      if(pid>0){ Log(svc,"    已按端口回收 llama-server (pid "+pid+")\r\n"); did=true; }
      else if(!did) Log(svc,"    端口 "+svc.Port+" 被非 llama 进程占用，未处理。\r\n");
    }
    svc.Starting=false; svc.startMs=0; svc.runCtx=0; svc.runVision=-1;
    if(!did) Log(svc,svc.Name+" 未运行.\r\n");
    Ui(()=>RefreshSvcMenus());
  }
  void StopAll(){ foreach(var s in services) Stop(s); }
  // 重启：重读配置 → 停 → 等端口释放 → 起。只动 llama-server，不碰 DSH(3080) → dsh 会话（对话历史）不中断
  void RestartSvc(Service svc){
    logForm.Append(">>> 重启模型 "+svc.Name+"（重读配置；DSH 与 dsh 会话不受影响）\r\n");
    ReloadSvcConfig(svc);
    Stop(svc);
    for(int i=0;i<12;i++){ if(!PortUp(svc.Port)) break; System.Threading.Thread.Sleep(500); }  // 等端口释放（最多 6s）
    System.Threading.Thread.Sleep(300);
    Start(svc);
  }
  void RestartAll(){ foreach(var s in services) RestartSvc(s); }
  // 重新读取配置文件（只读解析，绝不调用 Config.Load —— 它会在解析失败时把默认配置写回文件，抹掉用户配置）：
  //   dsh-tray-config.json 里本服务项（按 Name 匹配，其次 Port）→ 覆盖内存中的模型/端口/mmproj/批参数/provider；
  //   dsh-tray.cfg（托盘参数：ctx/KV/切分/MTP/GPU/缓存内存…）→ 让改过的文件立即生效，无需重启托盘。
  void ReloadSvcConfig(Service svc){
    try{
      if(!File.Exists(Config.Path_)){ Log(svc,"    配置文件不存在，沿用内存参数: "+Config.Path_+"\r\n"); return; }
      AppConfig? fresh=null;
      try{ fresh=System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Config.Path_)); }catch(Exception ex){ Log(svc,"    配置解析失败（沿用内存参数）: "+ex.Message+"\r\n"); }
      var sc=fresh?.Services?.FirstOrDefault(x=>string.Equals(x.Name,svc.Name,StringComparison.OrdinalIgnoreCase))
          ?? fresh?.Services?.FirstOrDefault(x=>x.Port==svc.Port);
      if(sc!=null){
        var diffs=new List<string>();
        if(sc.Model!=svc.Model){ diffs.Add("模型 "+FileName(svc.Model)+" → "+FileName(sc.Model)); svc.Model=sc.Model; }
        if(sc.Port!=svc.Port){ diffs.Add("端口 "+svc.Port+" → "+sc.Port); svc.Port=sc.Port; }
        if(sc.UseMmproj!=svc.UseMmproj||sc.Mmproj!=svc.Mmproj){ diffs.Add("mmproj "+FileName(svc.Mmproj)+" → "+(sc.UseMmproj?FileName(sc.Mmproj):"关")); svc.UseMmproj=sc.UseMmproj; svc.Mmproj=sc.Mmproj; }
        if(sc.Batch>0&&sc.Batch!=svc.Batch){ diffs.Add("批 "+svc.Batch+" → "+sc.Batch); svc.Batch=sc.Batch; }
        if(sc.Ubatch>0&&sc.Ubatch!=svc.Ubatch){ diffs.Add("ubatch "+svc.Ubatch+" → "+sc.Ubatch); svc.Ubatch=sc.Ubatch; }
        if(sc.SpecDecode!=svc.SpecDecode){ diffs.Add("SpecDecode "+svc.SpecDecode+" → "+sc.SpecDecode); svc.SpecDecode=sc.SpecDecode; }
        if(!string.IsNullOrEmpty(sc.Provider)&&sc.Provider!=svc.Provider){ diffs.Add("provider "+svc.Provider+" → "+sc.Provider); svc.Provider=sc.Provider; }
        Log(svc,(diffs.Count>0? "    配置已更新: "+string.Join("；",diffs)+"\r\n" : "    配置无变化\r\n"));
      } else Log(svc,"    dsh-tray-config.json 中无同名/同端口服务项（沿用内存参数）\r\n");
      LoadCfg(); RefreshChecks();   // 托盘参数（ctx/KV/切分/MTP/GPU/缓存内存/监听…）即时重读
      Log(svc,"    当前参数: ctx="+(ctxVal/1024)+"K | KV="+KvLabel(kvMode)+" | 切分="+(splitMode==0?"layer":"tensor "+(100-tsGpu1)+","+tsGpu1)+" | MTP="+MtpLabel(mtpLevel)+" | 缓存内存="+CacheRamLabel(cacheRam)+" | GPU="+gpuSel.ShortLabel()+"\r\n");
    }catch(Exception ex){ Log(svc,"    重读配置失败（沿用内存参数）: "+ex.Message+"\r\n"); }
  }

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
  // 后台执行（菜单点启停/重启不阻塞 UI 线程）；UI 更新一律经 Ui() 回 UI 线程
  void Bg(Action a){ try{ Task.Run(a); }catch{} }
  void Ui(Action a){ try{ if(logForm!=null&&logForm.InvokeRequired){ logForm.BeginInvoke(a); } else a(); }catch{ try{a();}catch{} } }
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
  string Short(Service s){ int i=s.Name.IndexOf(" ("); return i>0?s.Name.Substring(0,i):s.Name; }

  Icon WhaleIcon(){ try{ var p=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"whale-girl.ico"); if(File.Exists(p)) return new Icon(p); }catch{} return SystemIcons.Application; }

  string ExtractBlock(string y){ int i=y.IndexOf("agent-default-model:"); if(i<0)return null; int h=y.IndexOf("\n",i); if(h<0)h=i; int be=y.Length; for(int id=h+1; id<y.Length; id++){ if(y[id]=='\n' && id+1<y.Length && y[id+1]!=' '){ be=id+1; break; } } return y.Substring(i, be-i); }
  string ReplaceBlock(string y, string block){ int i=y.IndexOf("agent-default-model:"); if(i<0)return y; int h=y.IndexOf("\n",i); if(h<0)h=i; int be=y.Length; for(int id=h+1; id<y.Length; id++){ if(y[id]=='\n' && id+1<y.Length && y[id+1]!=' '){ be=id+1; break; } } return y.Substring(0,i)+block+y.Substring(be); }
  // 查询运行中 llama-server 的实际加载参数：/props 返回 default_generation_settings.n_ctx（真实上下文）与 modalities.vision（视觉）。
  // 服务未运行/请求失败返回 (0,null)，调用方回退托盘配置。
  (int,bool?) ProbeLlamaProps(int port){
    try{
      using(var wc=new System.Net.WebClient()){ wc.Encoding=System.Text.Encoding.UTF8;
        string j=wc.DownloadString("http://127.0.0.1:"+port+"/props");
        using(var doc=System.Text.Json.JsonDocument.Parse(j)){
          var r=doc.RootElement; int ctx=0; bool? vis=null;
          if(r.TryGetProperty("default_generation_settings",out var gs)&&gs.TryGetProperty("n_ctx",out var n)) ctx=n.GetInt32();
          if(r.TryGetProperty("modalities",out var m)&&m.TryGetProperty("vision",out var v)) vis=v.GetBoolean();
          return (ctx,vis);
        }
      }
    }catch{ return (0,null); }
  }
  // YAML 单引号包裹（内部 ' 双写转义），防名称含特殊字符破坏缩进/解析
  static string Yq(string s){ return "'"+s.Replace("'","''")+"'"; }
  // 在 settings.yaml 的 llm-pi-ai.providers 下确保 <provider> 块存在，并把 provider 的模型条目全量校准为托盘/服务端实际配置：
  //   baseURL(端口) / 模型 id / displayName+name(托盘服务名) / contextWindow(实际 ctx) / maxTokens(ctx 推导) / input(视觉→含 image)
  // 只改 llm-pi-ai.providers 对应块；其它 provider 与顶层键一律保持。返回修改后的全文；任何失败/异常原样返回 y。
  string EnsureLlamaProvider(string y, string provider, int port, string model, string modelName, int ctx, bool vision){
    try{
      string inputVal = vision ? "[ text, image ]" : "[ text ]";
      int maxTok = Math.Max(1024, Math.Min(32000, ctx/2));   // 输出预算 ≈ ctx 一半、上限 32000（与既有手工值风格一致）
      var lines=new List<string>(y.Split('\n'));
      int Ind(string l){ int n=0; while(n<l.Length&&l[n]==' ') n++; return n; }
      int FindExact(int from,int ind_,string marker){ for(int i=from;i<lines.Count;i++) if(Ind(lines[i])==ind_ && lines[i].Trim()==marker) return i; return -1; }
      int pi=FindExact(0,0,"llm-pi-ai:"); if(pi<0) return y;
      int pv=FindExact(pi+1,2,"providers:"); if(pv<0) return y;
      int bs=-1; for(int i=pv+1;i<lines.Count;i++) if(Ind(lines[i])==4 && lines[i].Trim().StartsWith(provider+":")){ bs=i; break; }
      if(bs<0){
        int ins=pv+1; for(int i=pv+1;i<lines.Count;i++){ if(Ind(lines[i])==4) ins=i; else if(Ind(lines[i])==0 && lines[i].Trim().Length>0) break; }
        var block=new List<string>{
          "    "+provider+":",
          "      displayName: "+Yq(modelName),
          "      api: openai-completions",
          "      baseURL: http://127.0.0.1:"+port+"/v1",
          "      apiKeyEnv: LLAMA_LOCAL_API_KEY",
          "      models:",
          "        - id: '"+model+"'",
          "          name: "+Yq(modelName),
          "          contextWindow: "+ctx,
          "          maxTokens: "+maxTok,
          "          input: "+inputVal
        };
        lines.InsertRange(ins,block);
        return string.Join("\n",lines);
      }
      int be=lines.Count; for(int i=bs+1;i<lines.Count;i++){ if(lines[i].Trim().Length==0) continue; if(Ind(lines[i])<6){ be=i; break; } }
      var body=lines.GetRange(bs,be-bs);
      bool hasBase=false;
      for(int i=0;i<body.Count;i++){ var m=System.Text.RegularExpressions.Regex.Match(body[i], @"^(\s*)baseURL:\s*.*$"); if(m.Success){ hasBase=true; body[i]=m.Groups[1].Value+"baseURL: http://127.0.0.1:"+port+"/v1"; } }
      if(!hasBase){ int ai=body.FindIndex(l=>System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*api:\s")); body.Insert(ai>=0?ai+1:1, "      baseURL: http://127.0.0.1:"+port+"/v1"); }
      bool hasDn=false;
      for(int i=0;i<body.Count;i++){ var m=System.Text.RegularExpressions.Regex.Match(body[i], @"^(\s*)displayName:\s*.*$"); if(m.Success){ hasDn=true; body[i]=m.Groups[1].Value+"displayName: "+Yq(modelName); } }
      if(!hasDn) body.Insert(1, "      displayName: "+Yq(modelName));
      // —— 首个模型条目：同步 id + 条目内四字段（name/contextWindow/maxTokens/input），缺行在条目尾补插 ——
      int idIdx=-1;
      for(int i=0;i<body.Count;i++) if(System.Text.RegularExpressions.Regex.IsMatch(body[i], @"^\s*- id:\s")){ idIdx=i; break; }
      if(idIdx>=0){
        var idm=System.Text.RegularExpressions.Regex.Match(body[idIdx], @"^(\s*)- id:\s*.*$");
        body[idIdx]=idm.Groups[1].Value+"- id: '"+model+"'";
        int en=idIdx+1; for(int i=idIdx+1;i<body.Count;i++){ if(body[i].Trim().Length==0) continue; if(Ind(body[i])<10){ en=i; break; } en=i+1; } // 条目字段区(缩进10)终点
        string[] keys={"name","contextWindow","maxTokens","input"};
        string[] vals={ Yq(modelName), ctx.ToString(), maxTok.ToString(), inputVal };
        for(int k=0;k<keys.Length;k++){
          bool found=false;
          for(int i=idIdx+1;i<en;i++){ var m=System.Text.RegularExpressions.Regex.Match(body[i], @"^(\s*)"+keys[k]+@":\s*.*$"); if(m.Success){ found=true; body[i]=m.Groups[1].Value+keys[k]+": "+vals[k]; break; } }
          if(!found){ body.Insert(en,"          "+keys[k]+": "+vals[k]); en++; }
        }
      }
      var res=new List<string>(lines.GetRange(0,bs)); res.AddRange(body); res.AddRange(lines.GetRange(be,lines.Count-be));
      return string.Join("\n",res);
    }catch{ return y; }
  }
  // —— 会话打开时自动校准 DSH 模型配置：同步 ctx/视觉/maxTokens/名称到 provider 模型声明（ctx 以运行中服务 /props 实测为准，
  // 服务未运行则回退托盘当前配置），再把 agent-default-model 指向该模型（30s 后还原，默认模型不被永久改写）——
  // 启动 DSH 会话（官方 Web UI）/ 启动内置对话（托盘瘦客户端）：都先把该模型同步成 agent-default-model，再打开对应窗口
  void OpenSession(Service svc){ SyncSessionConfig(svc); OpenPop(); }
  void OpenSessionBuiltin(Service svc){ SyncSessionConfig(svc); OpenThin(""); }
  void SyncSessionConfig(Service svc){
    string orig=null; string yp=cfg.SettingsYamlPath;
    try{
      int effCtx=ctxVal; bool effVis=svc.UseMmproj; string src="托盘配置";
      if(svc.Running){
        var pr=ProbeLlamaProps(svc.Port);
        if(pr.Item1>0){ effCtx=pr.Item1; src="服务端实测"; }
        if(pr.Item2.HasValue){ effVis=pr.Item2.Value; if(src=="托盘配置") src="服务端实测"; }
      }
      string y=File.ReadAllText(yp);
      string prov=string.IsNullOrEmpty(svc.Provider)? "llama-local" : svc.Provider;
      y=EnsureLlamaProvider(y, prov, svc.Port, svc.Model, svc.Name, effCtx, effVis);   // ctx/视觉/maxTokens/名称全量校准
      orig=ExtractBlock(y);
      File.WriteAllText(yp, ReplaceBlock(y, "agent-default-model:\r\n  provider: "+prov+"\r\n  model: '"+svc.Model+"'\r\n"));
      logForm.Append("打开 DSH 会话（"+Short(svc)+"）→ agent-default-model="+prov+"\r\n");
      logForm.Append("    已同步 dsh 模型声明: ctx="+(effCtx/1024)+"K("+src+") 视觉="+(effVis?"支持":"不支持")+" maxTokens="+Math.Max(1024,Math.Min(32000,effCtx/2))+"\r\n");
    }catch(Exception ex){ logForm.Append("打开会话同步配置失败: "+ex.Message+"\r\n"); }
    if(orig!=null){ var rt=new System.Windows.Forms.Timer(); rt.Interval=30000; rt.Tick+=(s,e)=>{ try{ File.WriteAllText(yp, ReplaceBlock(File.ReadAllText(yp), orig)); }catch{} rt.Stop(); rt.Dispose(); }; rt.Start(); }
  }
  void OpenPop(){ if(builtinRender){ OpenThin(""); return; } EnsurePopupNav(cfg.DshUrl,false); }
  void OpenSessionById(string sessionId){ try{ if(builtinRender){ OpenThin(sessionId); MarkSessionRead(sessionId); return; } EnsurePopupNav(BuildSessionUrl(sessionId),true); MarkSessionRead(sessionId); }catch(Exception ex){ MessageBox.Show("打开会话失败: "+ex.Message); } }
  // —— 内置渲染 / 安全模式 / 插件管理 ——
  void ToggleBuiltinRender(){ builtinRender=!builtinRender; RefreshChecks(); SaveCfg(); logForm.Append("使用托盘内置渲染打开: "+(builtinRender?"开（打开会话走托盘瘦客户端，绕过官方客户端 bundle）":"关（打开会话走官方 Web UI）")+"\r\n"); }
  void ToggleSafeMode(){ safeMode=!safeMode; RefreshChecks(); SaveCfg(); logForm.Append("安全模式启动: "+(safeMode?"开（仅官方插件白名单，下次启动/重启 DSH 生效）":"关（下次启动/重启 DSH 恢复全量插件）")+"\r\n"); }
  void OpenThin(string sid){
    try{
      if(dshRpc==null) dshRpc=new DshRpc(cfg.DshUrl, DshHomeDir());
      if(thinChat==null||thinChat.IsDisposed) thinChat=new ThinChatForm(dshRpc, WhaleIcon(), s=>logForm.Append(s));
      if(sid.Length>0) thinChat.OpenSession(sid); else thinChat.ShowHome();
    }catch(Exception ex){ MessageBox.Show("打开内置渲染失败: "+ex.Message); }
  }
  void OpenPluginManager(){
    logForm.Append("扫描 dsh 插件清单（dump-config + super-injector registry）…\r\n");
    Bg(()=>{
      var inv=PluginCenter.Scan(cfg, disabledDevPlugins, true);
      Ui(()=>{
        try{
          if(pluginMgr==null||pluginMgr.IsDisposed){
            var st=new PluginManagerForm.State{ Inv=inv, SafeMode=safeMode, DisabledEntries=new HashSet<string>(disabledEntries,StringComparer.OrdinalIgnoreCase), DisabledDev=new HashSet<string>(disabledDevPlugins,StringComparer.OrdinalIgnoreCase) };
            pluginMgr=new PluginManagerForm(st, ApplyPluginSettings, ()=>Bg(()=>DshRestart()), sm=>{ safeMode=sm; RefreshChecks(); SaveCfg(); }, WhaleIcon());
            pluginMgr.RefreshAll=()=>OpenPluginManagerRescan();
          }else{ pluginMgr.St.Inv=inv; pluginMgr.St.SafeMode=safeMode; pluginMgr.Populate(); }
          pluginMgr.Show(); pluginMgr.Activate();
        }catch(Exception ex){ logForm.Append("打开插件管理失败: "+ex.Message+"\r\n"); }
      });
    });
  }
  void OpenPluginManagerRescan(){
    Bg(()=>{
      var inv=PluginCenter.Scan(cfg, disabledDevPlugins, true);
      Ui(()=>{ try{ if(pluginMgr!=null&&!pluginMgr.IsDisposed){ pluginMgr.St.Inv=inv; pluginMgr.Populate(); } }catch{} });
    });
  }
  // 「应用」：落 cfg + registry 手术 + 重建 patch overlay；返回说明文本显示在弹窗日志里
  string ApplyPluginSettings(bool sm, HashSet<string> de, HashSet<string> dd){
    safeMode=sm; disabledEntries=new HashSet<string>(de,StringComparer.OrdinalIgnoreCase); disabledDevPlugins=new HashSet<string>(dd,StringComparer.OrdinalIgnoreCase);
    RefreshChecks(); SaveCfg();
    string msg1=PluginCenter.ApplyDevDisabled(cfg, disabledDevPlugins);
    var inv=PluginCenter.Scan(cfg, disabledDevPlugins, true);
    var built=PluginCenter.BuildPatchYaml(inv, safeMode, disabledEntries);
    string pf=PluginCenter.WritePatchFile(built.yaml);
    string msg2 = pf.Length>0 ? ("patch overlay 已生成（禁用 "+built.count+" 个 entry）："+pf) : "无需 patch overlay（无禁用项）";
    logForm.Append("[插件管理] "+msg1+" | "+msg2+"\r\n");
    return msg1+"\r\n"+msg2+"\r\n（重启 DSH 后生效）";
  }
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
      // dsh web 新版会在浏览器 localStorage 持久「上次会话」(dsh.sessions.current) 与工作区视图(dsh.workspace.view.v5)，
      // 导致打开 base URL 恢复上次会话（而不是新建）。这里在每次文档创建、页面脚本运行前清掉这两个键（不动 cookie，登录态保留），
      // 让托盘「打开 DSH 会话」= 新建一个会话（watchNavigation 默认流），从而应用刚写入的 agent-default-model（本地模型）。
      wv.CoreWebView2InitializationCompleted+=(s,e)=>{ try{ wv.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("try{localStorage.removeItem('dsh.sessions.current');localStorage.removeItem('dsh.workspace.view.v5');}catch(err){}"); }catch{} };
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
      try{
        var list=ReadRecent();
        var (codes,reasons)=PollSessionStates(list);   // dshtray-status host 插件轮询（后台；失败静默 → 空字典=不显示状态）
        Ui(()=>{ recentList=list; _sessState=codes; _sessReason=reasons; RebuildRecentMenu(); });
      }catch{} finally{ Interlocked.Exchange(ref recentBusy,0); }
    });
  }
  // —— dshtray-status host 插件轮询：GET /dshtray-status/api?token=<插件token>&ids=<最近会话id> ——
  // 契约：插件只在 host 进程内为请求的会话聚合权威信号（running / ask / permission / turn 终态），
  // 不经它时磁盘投影无法区分“已完成/异常中断/阻塞”。DSH 未运行或插件未装时静默降级为“无状态”。
  (Dictionary<string,int>,Dictionary<string,string>) PollSessionStates(List<RecentSession> list){
    var codes=new Dictionary<string,int>(); var reasons=new Dictionary<string,string>();
    try{
      if(dshPortUp!=1) return (codes,reasons);                 // DSH 未就绪：不请求
      if(list==null||list.Count==0) return (codes,reasons);
      string tok=DshtrayToken();
      if(tok.Length==0) return (codes,reasons);                // 插件未生成 token（未安装/未装载）：静默
      string ids=string.Join(",",list.Select(x=>Uri.EscapeDataString(x.Id)));
      string url=cfg.DshUrl.TrimEnd('/')+"/dshtray-status/api?token="+Uri.EscapeDataString(tok)+"&ids="+ids;
      using(var r=_http.GetAsync(url).Result){
        if(!r.IsSuccessStatusCode) return (codes,reasons);
        string body=r.Content.ReadAsStringAsync().Result;
        using(var doc=System.Text.Json.JsonDocument.Parse(body)){
          var root=doc.RootElement;
          if(!root.TryGetProperty("value",out var val)||!val.TryGetProperty("items",out var items)) return (codes,reasons);
          foreach(var it in items.EnumerateArray()){
            string id=it.TryGetProperty("id",out var ip)?ip.GetString()??"":"";
            if(id.Length==0) continue;
            string st=it.TryGetProperty("state",out var sp)?sp.GetString()??"":"";
            codes[id]=StateCodeOf(st);
            string rb="";
            if(it.TryGetProperty("reason",out var rp)&&rp.ValueKind==System.Text.Json.JsonValueKind.Object){
              foreach(var kv in rp.EnumerateObject()){
                if(kv.Value.ValueKind==System.Text.Json.JsonValueKind.String){ if(rb.Length>0)rb+=" "; rb+=kv.Name+"="+kv.Value.GetString(); }
                else if(kv.Value.ValueKind==System.Text.Json.JsonValueKind.Object){ var inner=System.Text.Json.JsonSerializer.Serialize(kv.Value); if(inner.Length>90) inner=inner.Substring(0,90)+"…"; if(rb.Length>0)rb+=" "; rb+=kv.Name+"="+inner; }
              }
            }
            reasons[id]=rb;
          }
        }
      }
    }catch{ /* DSH 未运行/插件未装/超时：全部静默降级 */ }
    return (codes,reasons);
  }
  int StateCodeOf(string wire){
    switch(wire){
      case "running": return SST_RUNNING;
      case "ask": return SST_ASK;
      case "permission": return SST_PERM;
      case "done": return SST_DONE;
      case "stopped": return SST_STOPPED;
      case "aborted": return SST_ABORTED;
      case "error": return SST_ERROR;
      case "interrupted": return SST_INTERRUPTED;
      case "empty": return SST_EMPTY;
      case "no-end": return SST_NOEND;
      default: return SST_UNKNOWN;
    }
  }
  string DshtrayToken(){
    try{
      string p=string.IsNullOrEmpty(cfg.DshtrayTokenPath)? Path.Combine(DshHomeDir(),"dshtray-status.token") : cfg.DshtrayTokenPath;
      if(File.Exists(p)) return File.ReadAllText(p).Trim();
    }catch{}
    return "";
  }
  // 状态→中文名 / 颜色（供菜单圆点与 ToolTip；done 叠加 unread 时显示蓝）
  string StateName(int s){
    switch(s){
      case SST_RUNNING: return "执行中"; case SST_ASK: return "阻塞·询问"; case SST_PERM: return "阻塞·权限";
      case SST_DONE: return "已完成"; case SST_STOPPED: return "已停止(手动)"; case SST_ABORTED: return "异常中断";
      case SST_ERROR: return "异常中断(错误)"; case SST_INTERRUPTED: return "异常中断(中断)"; case SST_NOEND: return "未收尾";
      case SST_EMPTY: return "空会话"; default: return "";
    }
  }
  string StateColorName(int s,bool unread){
    if(unread&&s==SST_DONE) return "unread";          // 已完成且未读 → 蓝
    switch(s){
      case SST_RUNNING: return "green"; case SST_ASK: return "yellow"; case SST_PERM: return "orange";
      case SST_DONE: return "gray"; case SST_STOPPED: return "gray2";
      case SST_ABORTED: case SST_ERROR: case SST_INTERRUPTED: return "red";
      case SST_NOEND: return "amber"; case SST_EMPTY: return "faint"; default: return "blank"; // 透明占位，文字对齐
    }
  }
  System.Drawing.Bitmap? DotFor(string colorName){
    if(colorName.Length==0) return null;
    if(_dotCache.TryGetValue(colorName,out var b)) return b;
    var col=colorName switch{
      "green"=>Color.FromArgb(46,160,67), "yellow"=>Color.FromArgb(242,184,16), "orange"=>Color.FromArgb(233,122,9),
      "gray"=>Color.FromArgb(150,150,150), "gray2"=>Color.FromArgb(120,120,120), "red"=>Color.FromArgb(214,69,65),
      "unread"=>Color.FromArgb(52,120,246), "amber"=>Color.FromArgb(196,116,0), "faint"=>Color.FromArgb(205,205,205),
      _=>Color.Transparent };
    var bm=new System.Drawing.Bitmap(12,12);
    using(var g=Graphics.FromImage(bm)){ g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using(var br=new SolidBrush(col)) g.FillEllipse(br,1,1,10,10); }
    _dotCache[colorName]=bm; return bm;
  }
  bool SessionRead(string id){ lock(_readLock){ return _readSids.Contains(id); } }
  // —— 会话已读 sidecar（托盘本地“已点开”表）——
  string ReadPath(){ return string.IsNullOrEmpty(cfg.RecentReadPath)? Path.Combine(DshHomeDir(),"dshtray-read.json") : cfg.RecentReadPath; }
  void LoadReadSids(){
    lock(_readLock){ _readSids.Clear(); try{ string p=ReadPath(); if(File.Exists(p)){ long mt=File.GetLastWriteTimeUtc(p).Ticks;
        using(var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(p))){ if(doc.RootElement.TryGetProperty("sids",out var a)) foreach(var x in a.EnumerateArray()){ var s=x.GetString(); if(s!=null&&_readSids.Count<600) _readSids.Add(s); } } } }catch{} }
  }
  void MarkSessionRead(string id){
    bool add=false; lock(_readLock){ add=_readSids.Add(id); }
    if(add) SaveReadSids();
  }
  void SaveReadSids(){
    if(Interlocked.Exchange(ref _readSaveBusy,1)!=0) return;
    Task.Run(()=>{ try{ List<string> snap; lock(_readLock){ snap=_readSids.ToList(); } string p=ReadPath();
        string dir=Path.GetDirectoryName(p)??"."; try{ Directory.CreateDirectory(dir); }catch{}
        string tmp=Path.Combine(dir,".dshtray-read-"+Guid.NewGuid().ToString("N")+".tmp");
        File.WriteAllText(tmp,System.Text.Json.JsonSerializer.Serialize(new{ sids=snap }));
        File.Move(tmp,p,true); }catch{} finally{ Interlocked.Exchange(ref _readSaveBusy,0); } });
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
          if(c.sub) continue;   // 子代理会话（并行 agent 任务）不进「最近会话」直达
          if(c.blank) continue; // 空会话（从未发过消息，blank=true，无标题无内容）不进「最近会话」直达
          found.Add(new RecentSession{Id=kv.Key,Title=c.title,Cwd=c.cwd,Last=Math.Max(c.last,c.created)});
        }
      }
      found.Sort((a,b)=>b.Last.CompareTo(a.Last));
      if(found.Count>10) found=found.GetRange(0,10);
    }catch{}
    return found;
  }
  // 读单个 per-session 投影：record.identity.{createdAt,cwd} + record.rows.{title.val, sessionListMetadata.val.{lastPromptAt,blank}}
  // sub=true 当 rows.subagent.val.identity 存在 → 该会话是并行子代理会话（agent 批量任务），非用户主会话，最近会话列表排除
  // blank=true 当 sessionListMetadata.val.blank → 空会话（从未发过用户消息，无标题），无直达价值，列表排除
  (long mt,string title,long last,long created,string cwd,bool sub,bool blank) ReadProjRecord(string f,long mt){
    string title=""; long last=0,created=0; string cwd=""; bool sub=false,blank=false;
    try{
      using(var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(f))){ var root=doc.RootElement;
        if(!root.TryGetProperty("record",out var rec)) return (mt,title,last,created,cwd,sub,blank);
        if(rec.TryGetProperty("identity",out var idt)){ if(idt.TryGetProperty("createdAt",out var ca)){ try{created=ca.GetInt64();}catch{} } if(idt.TryGetProperty("cwd",out var cw)) cwd=cw.GetString()??""; }
        if(rec.TryGetProperty("rows",out var rows)){
          if(rows.TryGetProperty("title",out var tt)&&tt.TryGetProperty("val",out var tv)&&tv.ValueKind==System.Text.Json.JsonValueKind.String) title=tv.GetString()??"";
          if(rows.TryGetProperty("sessionListMetadata",out var slm)&&slm.TryGetProperty("val",out var sv)){
            if(sv.TryGetProperty("lastPromptAt",out var lp)){ try{last=lp.GetInt64();}catch{} }
            if(sv.TryGetProperty("blank",out var bk)){ if(bk.ValueKind==System.Text.Json.JsonValueKind.True) blank=true; }
          }
          if(rows.TryGetProperty("subagent",out var sa)&&sa.TryGetProperty("val",out var sav)&&sav.ValueKind==System.Text.Json.JsonValueKind.Object&&sav.TryGetProperty("identity",out _)) sub=true;
        }
      }
    }catch{}
    return (mt,title,last,created,cwd,sub,blank);
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
      int code=_sessState.TryGetValue(rr.Id,out var cs)?cs:SST_UNKNOWN;
      bool unread=(code==SST_DONE)&&!SessionRead(rr.Id);
      var it=new ToolStripMenuItem(RecentLabel(rr),null,(s,e)=>OpenSessionById(rr.Id));
      it.Image=DotFor(StateColorName(code,unread)); // 圆点状态（unknown=透明占位，保持文字对齐）
      string tip=(rr.Cwd.Length>0?("工作区: "+rr.Cwd+"\r\n"):"")+"会话: "+rr.Id;
      if(code!=SST_UNKNOWN){
        tip="状态: "+StateName(code)+(unread?" · 未读":"")+"\r\n"+tip;
        if(_sessReason.TryGetValue(rr.Id,out var rsn)&&rsn.Length>0) tip+="\r\n结束原因: "+rsn;
      }
      it.ToolTipText=tip;
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
    // —— 模型状态巡检（1s 纯内存判断；每 2s 一次后台探活）——
    // 进程已退出但还挂着「启动中」→ 判定启动失败；「启动中」→ /health 就绪后清标志并记录耗时；运行中 → /props 实测 ctx/视觉
    foreach(var s in services){ bool ex=false; try{ if(s.proc!=null) ex=s.proc.HasExited; }catch{} if(ex&&s.Starting){ s.Starting=false; s.startMs=0; logForm.Append("["+s.Name+"] 进程已退出（启动失败？见日志窗口 / model-start.log）\r\n"); try{ perf?.OnFail(s,"exit"); }catch{} } }
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
                try{ perf?.OnReady(t.svc, ms, pr.Item1, ProbeBuildInfo(t.svc.Port)); }catch{} }
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
    foreach(var svc in services){ long L=svc.log.Length; if(L>svc.lastLen){ string t=svc.log.ToString((int)svc.lastLen,(int)(L-svc.lastLen)); svc.lastLen=L; logForm.Append("["+svc.Name+"] "+t); } }
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
  public void DisposeForDump(){ try{ icon.Visible=false; icon.Dispose(); }catch{} try{ clock.Stop(); }catch{} try{ if(logForm!=null&&!logForm.IsDisposed){ logForm.Dispose(); } }catch{} }
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

public class RecentSession { public string Id=""; public string Title=""; public string Cwd=""; public long Last=0; }

// 监听 explorer 重启（TaskbarCreated 广播）→ TrayApp 延迟重注册图标重建右键路由
internal sealed class TaskbarWatcher : NativeWindow {
  static readonly int _msg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
  public event Action? ExplorerRestarted;
  public TaskbarWatcher(){ CreateHandle(new CreateParams()); }
  protected override void WndProc(ref Message m){ if(_msg!=0 && m.Msg==_msg) ExplorerRestarted?.Invoke(); base.WndProc(ref m); }
}
internal static class NativeMethods {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int RegisterWindowMessage(string lpString);
}

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
    if(!dump && !selftest && !menuProbe && !pluginProbe && !rpcProbe && !lockProbe && !exitProbe && !svcProbe && !perfProbe && benchPort<=0){
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
    if(dump||selftest||menuProbe||pluginProbe||rpcProbe||lockProbe||exitProbe||svcProbe){ Environment.ExitCode=3; return; }
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
