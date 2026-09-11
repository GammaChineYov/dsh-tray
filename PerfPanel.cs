using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;

namespace QwenTray;

// S1（2026-09-11）拆自 ModelPerf.cs（界面：日志窗口第 3 页签）：零逻辑改动，仅位移。
// —— 界面：日志窗口第三页签「模型性能」 ——
public class PerfPanel : UserControl {
  public PerfStore? Store;
  public Func<int,Action<string>?,Task<PerfBenchResult?>>? BenchAction;   // port, log → result
  public Action<string>? OpenDirAction;
  public Action? FlushAction;

  ListView lv=new();
  RichTextBox detail=new();
  ToolStrip bar=new();
  ToolStripButton btnBench, btnRefresh, btnCsv, btnDir;
  ToolStripComboBox cbScale=new();
  Label status=new();
  SplitContainer split=new();
  bool _busy;

  public PerfPanel(){
    lv.View=View.Details; lv.FullRowSelect=true; lv.GridLines=true; lv.HideSelection=false;
    lv.Font=new Font("Microsoft YaHei UI",9f); lv.Dock=DockStyle.Fill;
    lv.Columns.Add("配置",70);
    lv.Columns.Add("服务",150);
    lv.Columns.Add("参数摘要",330);
    lv.Columns.Add("启动",48,HorizontalAlignment.Right);
    lv.Columns.Add("加载s",55,HorizontalAlignment.Right);
    lv.Columns.Add("pp t/s",60,HorizontalAlignment.Right);
    lv.Columns.Add("tg t/s",60,HorizontalAlignment.Right);
    lv.Columns.Add("基准tg",60,HorizontalAlignment.Right);
    lv.Columns.Add("最近使用",140);
    lv.SelectedIndexChanged+=(s,e)=>ShowDetail();

    detail.ReadOnly=true; detail.Dock=DockStyle.Fill; detail.Font=new Font("Consolas",9f);
    detail.WordWrap=false; detail.ScrollBars=RichTextBoxScrollBars.Both; detail.BackColor=Color.White;

    btnBench=new ToolStripButton("跑基准"){ToolTipText="对选中配置对应的端口发固定规模的基准请求（默认 1K prompt / 128 生成 × 3 次取中位数）；需该服务已就绪"};
    btnRefresh=new ToolStripButton("刷新"){ToolTipText="重新读取 configs.json 台账"};
    btnCsv=new ToolStripButton("导出 CSV"){ToolTipText="把配置台账导出为 CSV（含性能列），便于在 Excel 里排序对比"};
    btnDir=new ToolStripButton("打开目录"){ToolTipText="打开 model-perf 目录（configs.json + samples-YYYY-MM.ndjson）"};
    cbScale=new ToolStripComboBox("基准规模"){DropDownStyle=ComboBoxStyle.DropDownList,AutoSize=false,Width=132};
    cbScale.Items.AddRange(new object[]{ "512 / 64 × 3", "1024 / 128 × 3", "2048 / 256 × 3", "4096 / 128 × 3" });
    cbScale.SelectedIndex=1;
    cbScale.ToolTipText="基准输入规模：prompt token / 生成 token × 次数（次数含 1 次 warmup）";

    btnBench.Click+=async (s,e)=>await DoBench();
    btnRefresh.Click+=(s,e)=>{ try{ FlushAction?.Invoke(); }catch{} Refresh_(); };
    btnCsv.Click+=(s,e)=>DoCsv();
    btnDir.Click+=(s,e)=>{ try{ OpenDirAction?.Invoke(Store?.Dir??""); }catch{} };

    bar.GripStyle=ToolStripGripStyle.Hidden; bar.RenderMode=ToolStripRenderMode.System;
    bar.Items.AddRange(new ToolStripItem[]{ btnBench, btnRefresh, btnCsv, btnDir, new ToolStripSeparator(), cbScale });

    status.AutoSize=false; status.Dock=DockStyle.Bottom; status.Height=22;
    status.TextAlign=ContentAlignment.MiddleLeft; status.Font=new Font("Microsoft YaHei UI",8.5f);
    status.Text=" 尚未加载";

    split.Dock=DockStyle.Fill; split.Orientation=Orientation.Horizontal; split.SplitterDistance=300;
    split.Panel1.Controls.Add(lv); split.Panel2.Controls.Add(detail);
    Controls.Add(split); Controls.Add(status); Controls.Add(bar);
  }

  public void Refresh_(){
    try{
      var store=Store; if(store==null) return;
      _runCache=null;                       // 重扫样本，否则刚采的 run 数据不显示
      string? keep=SelectedId();
      lv.BeginUpdate(); lv.Items.Clear();
      var all=store.All().OrderByDescending(x=>x.lastSeen).ToList();
      foreach(var e in all){
        var it=new ListViewItem(new[]{
          e.id, e.name, e.OptLine,
          e.runs.ToString(), e.loadLast>0?e.loadLast.ToString(): "-",
          RunTg(e.id,"pp"), RunTg(e.id,"tg"),
          e.benchBest!=null?e.benchBest.tgTps.ToString("0.0"):"-",
          FmtSeen(e.lastSeen)
        });
        it.Tag=e;
        lv.Items.Add(it);
      }
      lv.EndUpdate();
      if(keep!=null){ foreach(ListViewItem it in lv.Items){ var e=it.Tag as PerfConfigEntry; if(e!=null&&e.id==keep){ it.Selected=true; it.EnsureVisible(); break; } } }
      status.Text=" 配置 "+all.Count+" 条 · 样本 "+store.TotalBytes()/1024+" KB · "+store.Dir;
    }catch(Exception ex){ status.Text=" 刷新失败: "+ex.Message; }
  }
  // 从当月样本里取该配置最近一条 run 样本的 pp/tg（只扫最近 300 行，避免越用越慢）
  Dictionary<string,(double pp,double tg)>? _runCache;
  void BuildRunCache(){
    _runCache=new Dictionary<string,(double,double)>(StringComparer.Ordinal);
    try{
      foreach(var line in Store?.TailSamples(300)??new List<string>()){
        if(line.IndexOf("\"run\"",StringComparison.Ordinal)<0) continue;
        using var doc=JsonDocument.Parse(line);
        var r=doc.RootElement;
        if(!r.TryGetProperty("cfg", out var c)) continue;
        double pp=0, tg=0;
        if(r.TryGetProperty("pp", out var a)&&a.ValueKind==JsonValueKind.Object&&a.TryGetProperty("med", out var av)) pp=av.GetDouble();
        if(r.TryGetProperty("tg", out var b)&&b.ValueKind==JsonValueKind.Object&&b.TryGetProperty("med", out var bv)) tg=bv.GetDouble();
        _runCache[c.GetString()??""]=(pp,tg);
      }
    }catch{}
  }
  string RunTg(string id,string which){
    if(_runCache==null) BuildRunCache();
    if(_runCache!=null && _runCache.TryGetValue(id, out var v)){
      double d= which=="pp"?v.pp:v.tg;
      return d>0? d.ToString("0.0") : "-";
    }
    return "-";
  }

  static string FmtSeen(string iso){
    if(string.IsNullOrEmpty(iso)||iso.Length<16) return iso;
    return iso.Substring(0,16).Replace('T',' ');
  }

  string? SelectedId(){
    if(lv.SelectedItems.Count==0) return null;
    var e=lv.SelectedItems[0].Tag as PerfConfigEntry;
    return e?.id;
  }
  PerfConfigEntry? SelectedEntry(){
    if(lv.SelectedItems.Count==0) return null;
    return lv.SelectedItems[0].Tag as PerfConfigEntry;
  }

  void ShowDetail(){
    var e=SelectedEntry();
    if(e==null){ detail.Text=""; return; }
    var sb=new StringBuilder();
    sb.AppendLine("配置指纹  = "+e.id);
    sb.AppendLine("服务      = "+e.name+"   端口 "+(e.port>0?e.port.ToString():"-"));
    sb.AppendLine("模型      = "+e.model);
    if(!string.IsNullOrEmpty(e.modelPath)) sb.AppendLine("模型路径  = "+e.modelPath);
    sb.AppendLine("llama     = "+e.llamaExe+(string.IsNullOrEmpty(e.llamaBuild)?"":"   build "+e.llamaBuild));
    if(!string.IsNullOrEmpty(e.env)) sb.AppendLine("环境      = "+e.env);
    sb.AppendLine("首次/最近 = "+FmtSeen(e.firstSeen)+"  →  "+FmtSeen(e.lastSeen)+"    启动 "+e.runs+" 次");
    sb.AppendLine("加载耗时  = 最近 "+(e.loadLast>0?e.loadLast+"s":"-")+"   最快 "+(e.loadMin>0?e.loadMin+"s":"-")+"   最慢 "+(e.loadMax>0?e.loadMax+"s":"-"));
    sb.AppendLine("实测 ctx  = "+(e.servedCtx>0?(e.servedCtx/1024)+"K":"未探测"));
    sb.AppendLine();
    sb.AppendLine("参数摘要  = "+e.OptLine);
    sb.AppendLine("完整参数  = "+string.Join(" ", e.args));
    if(e.benchLast!=null){
      sb.AppendLine();
      sb.AppendLine("最近基准  = "+FmtSeen(e.benchLast.ts)+"  pp "+e.benchLast.ppTps.ToString("0")+" t/s · tg "+e.benchLast.tgTps.ToString("0.0")+" t/s · TTFT "+e.benchLast.ttftMs.ToString("0")+" ms");
      sb.AppendLine("            prompt "+e.benchLast.promptN+" tok / 生成 "+e.benchLast.genN+" tok × "+e.benchLast.runs+" 次");
    }
    if(e.benchBest!=null) sb.AppendLine("历史最佳  = tg "+e.benchBest.tgTps.ToString("0.0")+" t/s（"+FmtSeen(e.benchBest.ts)+"）");
    sb.AppendLine();
    sb.AppendLine("— 最近 run 样本（按小时聚合的中位数/极值）—");
    AppendRecentRuns(sb, e.id);
    detail.Text="";
    detail.AppendText(sb.ToString());
  }

  void AppendRecentRuns(StringBuilder sb, string id){
    try{
      var lines=(Store?.TailSamples(300)??new List<string>()).Where(l=>l.Contains("\"run\"")&&l.Contains(id)).TakeLast(12).ToList();
      if(lines.Count==0){ sb.AppendLine("（暂无 —— 该配置下还没有产生足够长的生成请求）"); return; }
      foreach(var l in lines){
        using var doc=JsonDocument.Parse(l);
        var r=doc.RootElement;
        string ts=r.TryGetProperty("ts",out var t)?FmtSeen(t.GetString()??""):"";
        string pp="", tg="";
        if(r.TryGetProperty("pp", out var a)&&a.ValueKind==JsonValueKind.Object) pp=Stat1(a,"pp");
        if(r.TryGetProperty("tg", out var b)&&b.ValueKind==JsonValueKind.Object) tg=Stat1(b,"tg");
        sb.AppendLine(ts+"   "+pp+"   "+tg);
      }
    }catch{}
  }
  static string Stat1(JsonElement o,string tag){
    double med=o.TryGetProperty("med",out var m)?m.GetDouble():0;
    double mn=o.TryGetProperty("min",out var a)?a.GetDouble():0;
    double mx=o.TryGetProperty("max",out var b)?b.GetDouble():0;
    int n=o.TryGetProperty("n",out var c)?c.GetInt32():0;
    return string.Format(CultureInfo.InvariantCulture,"{0} {1:0.0} ({2:0.0}~{3:0.0}, n={4})", tag, med, mn, mx, n);
  }

  async Task DoBench(){
    if(_busy) return;
    var e=SelectedEntry();
    if(e==null){ status.Text=" 请先选中一行配置"; return; }
    if(e.port<=0){ status.Text=" 该配置没有端口记录（可能是从 model-start.log 导入的）"; return; }
    if(BenchAction==null){ status.Text=" 基准入口未接线"; return; }
    var (pp,gen,runs)=Scale();
    _busy=true; btnBench.Enabled=false;
    status.Text=" 正在跑基准 "+pp+"/"+gen+"×"+runs+" …（服务需已就绪，首轮为 warmup）";
    try{
      var r=await BenchAction(e.port, m=>{ try{ status.Text=" "+m; }catch{} });
      if(r==null) status.Text=" 基准失败（端口 "+e.port+" 无就绪服务，或响应缺少 timings）";
      else status.Text=string.Format(CultureInfo.InvariantCulture," 基准完成：pp {0:0} t/s · tg {1:0.0} t/s · TTFT {2:0} ms（{3} tok prompt）", r.ppTps, r.tgTps, r.ttftMs, r.promptN);
    }catch(Exception ex){ status.Text=" 基准异常: "+ex.Message; }
    finally{ _busy=false; btnBench.Enabled=true; Refresh_(); }
  }

  (int pp,int gen,int runs) Scale(){
    switch(cbScale.SelectedIndex){
      case 0: return (512,64,3);
      case 1: return (1024,128,3);
      case 2: return (2048,256,3);
      case 3: return (4096,128,3);
      default: return (1024,128,3);
    }
  }

  void DoCsv(){
    try{
      var store=Store; if(store==null) return;
      string path=Path.Combine(store.Dir,"perf-export-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".csv");
      var sb=new StringBuilder();
      sb.AppendLine("cfgId,服务,模型,ctx,切分,ts,kvK,kvV,batch,ubatch,cacheRam,mtp,ngl,flash,启动次数,加载最近s,加载最快s,servedCtx,基准pp,基准tg,基准TTFTms,基准promptN,基准genN,最近使用");
      foreach(var e in store.All().OrderByDescending(x=>x.lastSeen)){
        string G(string k){ return e.opt.TryGetValue(k, out var v)?v:""; }
        sb.AppendLine(string.Join(",", new[]{
          e.id, Csv(e.name), Csv(e.model), G("ctx"), G("split"), G("ts"), G("kvK"), G("kvV"),
          G("batch"), G("ubatch"), G("cacheRam"), G("mtpN"), G("ngl"), G("flash"),
          e.runs.ToString(), e.loadLast.ToString(), e.loadMin.ToString(), e.servedCtx.ToString(),
          e.benchBest!=null?e.benchBest.ppTps.ToString("0",CultureInfo.InvariantCulture):"",
          e.benchBest!=null?e.benchBest.tgTps.ToString("0.0",CultureInfo.InvariantCulture):"",
          e.benchBest!=null?e.benchBest.ttftMs.ToString("0",CultureInfo.InvariantCulture):"",
          e.benchBest!=null?e.benchBest.promptN.ToString():"",
          e.benchBest!=null?e.benchBest.genN.ToString():"",
          FmtSeen(e.lastSeen)
        }));
      }
      File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // 带 BOM：Excel 直接认 UTF-8 中文
      status.Text=" 已导出 "+path;
      try{ OpenDirAction?.Invoke(store.Dir); }catch{}
    }catch(Exception ex){ status.Text=" 导出失败: "+ex.Message; }
  }
  static string Csv(string s){ return "\""+(s??"").Replace("\"","\"\"")+"\""; }

  public RichTextBox DetailBox { get { return detail; } }
  public bool HasBenchButton(){ return btnBench!=null; }
  public string UiSummary(){ return "perf cols="+lv.Columns.Count+" rows="+lv.Items.Count+" store="+(Store!=null?"ok":"null"); }
}
