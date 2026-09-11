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

// S1（2026-09-11）拆自 Program.cs（partial 2/5：探活 + 每模型菜单 + 启停）：零逻辑改动，仅位移。
public partial class TrayApp {
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

}
