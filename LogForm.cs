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
  // 单页签窗口保留的最大字符数（超出后裁掉最早的内容）：防长驻托盘 RichTextBox 无限增长（P1-3）
  public int MaxChars=1_000_000;
  // UI 线程调度锚点：**构造（= UI 线程）时就建好句柄**，修掉"拿业务控件当锚点、句柄未就绪时静默降级"（P0-1）
  public readonly UiDispatcher Dispatcher;
  public LogForm(string title="DSH托盘 日志"){
    Dispatcher=new UiDispatcher();
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
    // 一律经 UI 调度器投递。不再用 b.InvokeRequired 判断：句柄未创建时它恒为 false，
    // 会让 UI 操作直接跑在线程池线程上、并把该控件的句柄永久绑到那个线程（见 UiDispatcher.cs / 报告 P0-1）
    Dispatcher.Post("log.append",()=>{
      try{
        if(b.IsDisposed) return;
        b.AppendText(s);
        Trim(b);
        if(AutoFollow) ScrollToEnd(b);
      }catch{}
    });
  }
  // 有界化：超出 MaxChars 时一次性裁掉"超出量 + 上限的 20%"，避免此后每次追加都触发一次裁剪
  void Trim(RichTextBox b){
    try{
      if(MaxChars<=0) return;
      int over=b.TextLength-MaxChars;
      if(over<=0) return;
      int cut=over+MaxChars/5;
      if(cut>b.TextLength) cut=b.TextLength;
      bool ro=b.ReadOnly;
      try{
        if(ro) b.ReadOnly=false;   // ReadOnly 会让 EM_REPLACESEL 失效，裁剪前必须先解除
        b.Select(0,cut); b.SelectedText="";
      }finally{ b.SelectionStart=b.TextLength; b.SelectionLength=0; if(ro) b.ReadOnly=true; }
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
