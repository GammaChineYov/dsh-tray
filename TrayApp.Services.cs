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
// S5（2026-09-12）：本文件连挨两刀，都是「把可判定的东西送进 Core」——
//   ① S5-2 探活（PortUp/HealthUp/KillByPort）与状态映射（SvcState/SvcDot/SvcEnable）→ SvcProbe / SvcStatus；
//   ② S5-3/4 **文本合成**（档位标签 / 文件名截断 / 时长 / 「运行时配置」6 行 / 状态悬浮）→ SvcLines，
//      「重读配置」的判定内核（Find + Apply）→ SvcConfigDiff。
//   本文件从此只剩「每模型二级菜单」的 WinForms 装配 + 启停流程编排 —— 装配搬不进 Core：
//   Core 工程 UseWindowsForms=false，出现 ToolStripMenuItem 会直接 CS0234（编译器强制，不是约定）。
public partial class TrayApp {
  // 把 Service + 当前托盘参数拍成 Core 的渲染视图（SvcLines 的输入）。
  // Core 不能引用 Service（那是带 Process 句柄的运行时袋），所以在这一个口子收拢；
  // proc 的两次取用都按"取不到就不写进去"处理 —— SvcLines 靠 Pid>0 / StartedAt.HasValue 决定要不要打 pid 行。
  SvcView ToView(Service svc){
    var v=new SvcView{ Model=svc.Model, UseMmproj=svc.UseMmproj, Mmproj=svc.Mmproj, Port=svc.Port,
      Provider=svc.Provider, Batch=svc.Batch, Ubatch=svc.Ubatch,
      Starting=svc.Starting, Running=svc.Running, PortBusy=svc.PortBusy,
      RamGb=svc.RamGb, RunCtx=svc.runCtx, RunVision=svc.runVision,
      Gpu=gpuSel, GpuCount=gpus.Count, SplitMode=splitMode, TsGpu1=tsGpu1, KvMode=kvMode,
      CtxVal=ctxVal, CacheRam=cacheRam, MtpLevel=mtpLevel, ParamMode=paramMode, BindAll=bindAll };
    if(svc.Running){ try{ if(svc.proc!=null){ v.Pid=svc.proc.Id; try{ v.StartedAt=svc.proc.StartTime; }catch{} } }catch{} }
    return v;
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
    var v=ToView(svc);   // 一屏的全部渲染输入；文本合成在 Core 的 SvcLines 里（SvcLinesTests 钉住）
    // —— 状态圆点 + 运行状态行（悬浮给全量）：黄=启动中 绿=运行中 橙=运行中(未托管) 红=未运行 ——
    m.root.Image=DotFor(SvcStatus.Dot(starting,running,busy));
    m.root.ToolTipText=SvcLines.StatusTip(v);
    var sb=new System.Text.StringBuilder("状态："+SvcStatus.State(starting,running,busy));
    if(running){
      if(v.Pid>0) sb.Append(" · pid "+v.Pid);
      if(!starting&&v.StartedAt.HasValue) sb.Append(" · 已运行 "+SvcLines.Dur(DateTime.Now-v.StartedAt.Value));
      string ram=svc.RamGb; if(ram!="-") sb.Append(" · 内存 "+ram);
      sb.Append(" · 端口 "+svc.Port);
    } else if(starting){ sb.Append(" · 端口 "+svc.Port+" · 正在加载模型，约 30-60s"); }
    else if(busy){ sb.Append(" · 端口 "+svc.Port+"（点「停止模型」按端口回收）"); }
    else { sb.Append(" · 端口 "+svc.Port); }
    m.status.Text=sb.ToString();
    m.status.ToolTipText=SvcLines.StatusTip(v);
    var en=SvcStatus.Enable(starting,running,busy);
    m.start.Enabled=en.start; m.stop.Enabled=en.stop; m.restart.Enabled=en.restart;
    // —— 运行时配置：环境配置 + llama.cpp 配置（按当前参数实时算；运行中叠加 /props 实测）——
    var b=LaunchArgs.Build(svc.Spec,gpuSel,ctxVal,paramMode,splitMode,kvMode,cacheRam,tsGpu1,gpus.Count,bindAll,mtpLevel);
    m.envCuda.Text="    CUDA_VISIBLE_DEVICES = "+(b.envCuda.Length>0?b.envCuda:"（未设置）");
    m.envAllreduce.Text="    GGML_CUDA_ALLREDUCE = "+(b.envAllreduce.Length>0?b.envAllreduce:"（未设置）");
    var lines=SvcLines.CfgLines(v);
    for(int i=0;i<m.cfgLines.Count;i++){ var it=m.cfgLines[i]; if(i<lines.Length){ it.Text=lines[i]; it.Visible=true; } else it.Visible=false; }
    m.cfgLines[0].ToolTipText="完整模型路径: "+svc.Model+(svc.UseMmproj?("\r\nmmproj: "+svc.Mmproj):"");
    m.cppHeader.ToolTipText="完整命令行（点「重启模型」即用它执行）：\r\n"+cfg.LlamaServerExe+" "+string.Join(" ",b.args);
  }
  // —— 下面三段 S5（2026-09-12）已移出本文件，改由 Core + dotnet test 承担 ——
  //   状态 / 圆点色 / 启停可用性映射 → QwenTray.Core/SvcStatus.cs    （SvcStatusTests 穷举 8 组合 × 3 函数）
  //   「运行时配置」6 行 + 状态悬浮全量 → QwenTray.Core/SvcLines.cs   （SvcLinesTests）
  //   「重读配置」的差异判定（Find / Apply）→ QwenTray.Core/SvcConfigDiff.cs（SvcConfigDiffTests）
  //   它们原先都是 TrayApp 的私有 static ⇒ 只有 `--selftest-svcmenu` 能间接枚举到，
  //   而那个探针**不在** dotnet test 链上（要手动跑托盘自检才会发现回归）。
  //   顺带清掉一个哑参数：原 SvcCfgLines(…, LaunchResult b) 的 `b` 函数体从未读过（首行自己算 EffectiveGpus）。
  void Start(Service svc){
    if(svc.Running){ Log(svc,svc.Name+" 已在运行 (端口 "+svc.Port+")\r\n"); Ui(()=>RefreshSvcMenus()); return; }
    if(SvcProbe.PortUp(svc.Port)){ Log(svc,"端口 "+svc.Port+" 已有服务在跑（非本应用启动），请先「停止模型」或停用外部进程。\r\n"); Ui(()=>RefreshSvcMenus()); return; }
    var build=LaunchArgs.Build(svc.Spec,gpuSel,ctxVal,paramMode,splitMode,kvMode,cacheRam,tsGpu1,gpus.Count,bindAll,mtpLevel);
    var psi=new ProcessStartInfo(cfg.LlamaServerExe,string.Join(" ",build.args)){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
    if(build.envCuda.Length>0) psi.Environment["CUDA_VISIBLE_DEVICES"]=build.envCuda;
    if(build.envAllreduce.Length>0) psi.Environment["GGML_CUDA_ALLREDUCE"]=build.envAllreduce; // 多卡张量并行需要内置 CUDA AllReduce
    try{
      svc.proc=Process.Start(psi); svc.proc.OutputDataReceived+=(o,e)=>{if(e.Data!=null){svc.log.AppendLine(e.Data); try{ perf?.OnLlamaLine(svc.Spec,e.Data); }catch{}}}; svc.proc.ErrorDataReceived+=(o,e)=>{if(e.Data!=null)svc.log.AppendLine("[err] "+e.Data);}; svc.proc.BeginOutputReadLine(); svc.proc.BeginErrorReadLine();
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
        if(perf!=null) cfgId=perf.OnStart(svc.Spec,build,cfg.LlamaServerExe,out isNewCfg);
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
    if(SvcProbe.PortUp(svc.Port)){
      int pid=SvcProbe.KillByPort(svc.Port);
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
    for(int i=0;i<12;i++){ if(!SvcProbe.PortUp(svc.Port)) break; System.Threading.Thread.Sleep(500); }  // 等端口释放（最多 6s）
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
      // 挑项 + 逐字段差异判定都走 Core（SvcConfigDiff）—— 三条守卫（Batch/Ubatch 只在 >0 时覆盖、
      // Provider 只在非空时覆盖、Mmproj 双条件）与"差异顺序 = 日志显示顺序"有穷举单测钉住。
      var sc=SvcConfigDiff.Find(fresh,svc.Name,svc.Port);
      if(sc!=null){
        var diffs=SvcConfigDiff.Apply(sc,svc.Spec);   // 传 Spec：Apply 就地写穿到服务项
        Log(svc,(diffs.Count>0? "    配置已更新: "+string.Join("；",diffs)+"\r\n" : "    配置无变化\r\n"));
      } else Log(svc,"    dsh-tray-config.json 中无同名/同端口服务项（沿用内存参数）\r\n");
      LoadCfg(); RefreshChecks();   // 托盘参数（ctx/KV/切分/MTP/GPU/缓存内存/监听…）即时重读
      Log(svc,"    当前参数: ctx="+(ctxVal/1024)+"K | KV="+SvcLines.KvLabel(kvMode)+" | 切分="+(splitMode==0?"layer":"tensor "+(100-tsGpu1)+","+tsGpu1)+" | MTP="+SvcLines.MtpLabel(mtpLevel)+" | 缓存内存="+SvcLines.CacheRamLabel(cacheRam)+" | GPU="+gpuSel.ShortLabel()+"\r\n");
    }catch(Exception ex){ Log(svc,"    重读配置失败（沿用内存参数）: "+ex.Message+"\r\n"); }
  }

}
