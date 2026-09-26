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
  SvcView ToView(Service svc, LaunchPlan plan){
    var pp=SvcParams.Resolve(svc.Spec,ctxVal,kvMode,cacheRam,splitMode,tsGpu1,mtpLevel,paramMode,bindAll,gpuSel.CfgString());
    var gp=pp.GpuOrDefault(gpuSel);
    var v=new SvcView{ Model=svc.Model, UseMmproj=svc.UseMmproj, Mmproj=svc.Mmproj, Port=svc.Port,
      Provider=svc.Provider, Batch=svc.Batch, Ubatch=svc.Ubatch,
      Starting=svc.Starting, Running=svc.Running, PortBusy=svc.PortBusy,
      RamGb=svc.RamGb, RunCtx=svc.runCtx, RunVision=svc.runVision,
      // ⚠️ 渲染值一律走**本模型生效参数**（覆盖优先），不是托盘全局 —— 否则会出现
      //    "菜单写着 192K、实际按 128K 启动"这种说一套做一套（执行鸿沟）。
      Gpu=gp, GpuCount=gpus.Count, SplitMode=pp.SplitMode, TsGpu1=pp.TsGpu1, KvMode=pp.KvMode,
      CtxVal=pp.Ctx, CacheRam=pp.CacheRam, MtpLevel=pp.MtpLevel, ParamMode=pp.ParamMode, BindAll=pp.BindAll,
      ArgsCustom=plan.ArgsCustom, Exe=SvcLines.FileName(plan.Exe),
      CustomArgs=string.Join(" ",plan.Args), EnvLine=plan.EnvLine() };
    if(svc.Running){ try{ if(svc.proc!=null){ v.Pid=svc.proc.Id; try{ v.StartedAt=svc.proc.StartTime; }catch{} } }catch{} }
    return v;
  }
  // —— 一级菜单里的模型行（2026-09-26 改造）——
  // ⚠️ 语义变更（用户指令）：**点击 = 选中本模型（互斥单选）**，不再是"点一下就启动"。
  //    ⇒ 一级项不再挂 WireQuickStart（那个是 2026-09-12 立的"快捷启动"语义，本次一并取消；
  //       启动统一走下面的「启动模型」）。下面那组「当前模型」与参数面板都跟随这一行选中的模型。
  // ⚠️ **不带二级菜单**：每模型的二级菜单整体取消（用户指令），功能上移到一级菜单的
  //    「当前模型」操作组 —— 组内的启停/会话入口作用在**当前选中**的那个模型上。
  // 为什么本行要声明成①类（点了不关）：选中是"设置"，不是"执行一条命令"；点完还留在菜单里
  // 才能接着调参数、再点启停（这与 2026-09-12 那批①类勾选项同一个道理）。
  ToolStripMenuItem BuildSvcMenu(Service svc){
    var m=new SvcMenu(); m.svc=svc;
    m.root=new ToolStripMenuItem(svc.Name+" ("+svc.Port+")");
    m.root.Click+=(s,e)=>{ try{ SelectSvc(svc); }catch{} };
    m.root.ToolTipText="点击选中本模型（一级菜单内互斥单选；选择会被记住，下次启动托盘仍选中它）。\n"
      +"状态圆点：红=未运行 黄=启动中 绿=运行中 橙=运行中(端口被外部进程占用)。\n"
      +"选中后：下面「当前模型」那一组与全部参数面板都作用到本模型。";
    m.root.Image=DotFor(SvcStatus.Dot(false,false,false));
    _keepItems.Add(m.root);
    svcMenus.Add(m);
    return m.root;
  }
  // —— 当前选中模型（选中态的唯一入口）——
  void SelectSvc(Service s){
    if(s==null||s==selSvc) return;
    selSvc=s;
    MaterializeConfig(s);
    SaveCfg();   // selectedModel= 必须在选择时就落盘 —— 只选不改参数也得记住（否则重启托盘丢选中）
    try{ logForm?.Append("当前模型: "+s.Name+"（启停、会话入口与下方参数面板都作用到它；选择已记住）\r\n"); }catch{}
    RefreshChecks();
  }
  // 2026-09-26 用户定稿：**每个模型都有自己的参数配置**（不存在"跟随全局"的悬浮态）。
  //   选中模型时把它配置里的**空位**从托盘默认补齐（已设置的值不动 —— 用户调过的就是它的配置，
  //   ⛔ 绝不覆盖，否则会静默改启动行为）。补齐后该模型参数完全自持：改托盘默认/别的模型都不再影响它；
  //   面板编辑永远有落点。全空 = 整份复制；部分有 = 只补空位（config 模板遗留的 GpuSel="all" 不算"调过"）。
  void MaterializeConfig(Service s){
    var sc=FindServiceConfig(s.Name,s.Port); if(sc==null) return;
    bool changed=false;
    if(!sc.Ctx.HasValue)      { sc.Ctx=ctxVal; changed=true; }
    if(!sc.KvMode.HasValue)   { sc.KvMode=kvMode; changed=true; }
    if(!sc.CacheRam.HasValue) { sc.CacheRam=cacheRam; changed=true; }
    if(!sc.SplitMode.HasValue){ sc.SplitMode=splitMode; changed=true; }
    if(!sc.TsGpu1.HasValue)   { sc.TsGpu1=tsGpu1; changed=true; }
    if(!sc.MtpLevel.HasValue) { sc.MtpLevel=mtpLevel; changed=true; }
    if(!sc.ParamMode.HasValue){ sc.ParamMode=paramMode; changed=true; }
    if(!sc.BindAll.HasValue)  { sc.BindAll=bindAll; changed=true; }
    if(sc.GpuSel.Length==0)   { sc.GpuSel=gpuSel.CfgString(); changed=true; }
    if(!changed) return;
    if(!PersistSuppressed) Config.Save(cfg);   // 探针期不落盘（Spec 仍同步，渲染链照常走）
    // 同步内存 Spec：面板显示立即可用（与 PersistParam 同一套搬运，别只写一半）
    s.Spec.Ctx=sc.Ctx; s.Spec.KvMode=sc.KvMode; s.Spec.CacheRam=sc.CacheRam; s.Spec.SplitMode=sc.SplitMode;
    s.Spec.TsGpu1=sc.TsGpu1; s.Spec.MtpLevel=sc.MtpLevel; s.Spec.ParamMode=sc.ParamMode; s.Spec.BindAll=sc.BindAll;
    s.Spec.GpuSel=sc.GpuSel;
    try{ logForm.Append("模型「"+s.Name+"」的参数配置已补齐（空位从托盘默认复制；已设置的值未动）——此后改动只影响它\r\n"); }catch{}
  }
  // 启动时的兜底：cfg 里的 selectedModel= 指向的服务还在 ⇒ 用它；否则第一个（运行态恒有选中，无"无选中"态）。
  void SelectInitial(){
    if(services.Count==0){ selSvc=null; return; }
    Service pick=null!;
    if(selectedModelName.Length>0)
      foreach(var s in services) if(string.Equals(s.Name,selectedModelName,StringComparison.OrdinalIgnoreCase)){ pick=s; break; }
    if(pick==null) pick=services[0];
    selSvc=pick;
    MaterializeConfig(pick);
  }
  Service CurSvc(){ return selSvc ?? (services.Count>0?services[0]:null!); }
  void StartCur(){ var s=CurSvc(); if(s!=null) Bg(()=>Start(s)); }
  void RestartCur(){ var s=CurSvc(); if(s!=null) Bg(()=>RestartSvc(s)); }
  void StopCur(){ var s=CurSvc(); if(s!=null) Stop(s); }
  void OpenCurSession(){ var s=CurSvc(); if(s!=null) OpenSession(s); }
  void OpenCurSessionBuiltin(){ var s=CurSvc(); if(s!=null) OpenSessionBuiltin(s); }
  // 一个模型「从哪几处喂进渲染」的签名：参数改成本模型的覆盖后，这些值仍全部进签名 ⇒
  // 换选中模型 / 改本模型参数都能触发重绘（漏一项就会出现"改了参数但菜单还显示旧的"）。
  string SvcSig(Service svc){
    var p=SvcParams.Resolve(svc.Spec,ctxVal,kvMode,cacheRam,splitMode,tsGpu1,mtpLevel,paramMode,bindAll,gpuSel.CfgString());
    bool busy=svc.PortBusy&&!svc.Running&&!svc.Starting;
    return (svc.Starting?"s":svc.Running?"r":busy?"b":"x")+"|"+svc.Port+"|"+svc.Model
      +"|"+p.Ctx+"|"+p.KvMode+"|"+p.CacheRam+"|"+p.SplitMode+"|"+p.TsGpu1+"|"+p.MtpLevel+"|"+p.ParamMode
      +"|"+(p.BindAll?"1":"0")+"|"+p.GpuCfg+"|"+(slotSaveOn?slotSavePath:"")+"|"+svc.runCtx
      +"|"+(svc.RunVision.HasValue?(svc.RunVision.Value?"1":"0"):"-")
      +(svc.Starting?("|"+(Environment.TickCount-svc.startMs)/1000):"");
  }
  void RefreshSvcMenus(){
    foreach(var m in svcMenus){ try{ RefreshSvcMenu(m); }catch{} }
    try{ RefreshCurGroup(); }catch{}
  }
  void RefreshSvcMenu(SvcMenu m){
    var svc=m.svc;
    // 选中标记**必须无条件刷，不能进签名门控**（2026-09-26 用户实测抓出）：
    //   SvcSig 描述的是"模型状态+生效参数"，**不含选中态** —— 点击切换模型时 sig 一字不变，
    //   门控直接 return ⇒ Checked 永远停在启动那一刻的选中 ⇒ 「点一下没反应（要再点）」+
    //   「旧选中勾残留（两个模型同时带勾）」。选中是托盘自己的 UI 态，与模型状态无关，单独刷。
    m.root.Checked=ReferenceEquals(svc,selSvc);
    string sig=SvcSig(svc);
    if(sig==m.sig) return; m.sig=sig;
    m.root.Image=DotFor(SvcStatus.Dot(svc.Starting,svc.Running,svc.PortBusy&&!svc.Running&&!svc.Starting));
  }
  // —— 当前选中模型那一组（上移一级菜单；一切以 selSvc 为唯一事实源）——
  void RefreshCurGroup(){
    var svc=CurSvc(); if(svc==null) return;
    bool starting=svc.Starting, running=svc.Running, busy=svc.PortBusy&&!running&&!starting;
    var p=SvcParams.Resolve(svc.Spec,ctxVal,kvMode,cacheRam,splitMode,tsGpu1,mtpLevel,paramMode,bindAll,gpuSel.CfgString());
    var gsel=p.GpuOrDefault(gpuSel);
    var b=LaunchArgs.Build(svc.Spec,gsel,p.Ctx,p.ParamMode,p.SplitMode,p.KvMode,p.CacheRam,p.TsGpu1,gpus.Count,p.BindAll,p.MtpLevel,SlotSavePath());
    var plan=LaunchPlan.Compose(svc.Spec,b,cfg.LlamaServerExe);
    var v=ToView(svc,plan);   // 一屏的全部渲染输入；文本合成在 Core 的 SvcLines 里（SvcLinesTests 钉住）
    if(curHeader!=null){
      var diff=p.DiffVs(ctxVal,kvMode,cacheRam,splitMode,tsGpu1,mtpLevel,paramMode,bindAll,gpuSel);
      curHeader.Text="当前模型："+svc.Name+" ("+svc.Port+")"
        +(diff.Count>0?("  · 与托盘默认差 "+diff.Count+" 项"):"  · 配置=托盘默认（尚未单独调整）");
      curHeader.ToolTipText="当前选中的模型：点上面任意一个模型名即切换（互斥单选，选择会被记住）。\n"
        +"本模型在 dsh-tray-config.json 里有自己的参数配置"+(diff.Count>0?("（与托盘默认不同的 "+diff.Count+" 项："+diff.Text+"）"):"（当前与托盘默认相同）")+"。\n"
        +"完整模型路径: "+svc.Model+(svc.UseMmproj?("\r\nmmproj: "+svc.Mmproj):"");
    }
    if(curStart!=null){ var en=SvcStatus.Enable(starting,running,busy); curStart.Enabled=en.start; curStop.Enabled=en.stop; curRestart.Enabled=en.restart; }
    if(curStatus!=null){
      var sb=new System.Text.StringBuilder("状态："+SvcStatus.State(starting,running,busy));
      if(running){
        if(v.Pid>0) sb.Append(" · pid "+v.Pid);
        if(!starting&&v.StartedAt.HasValue) sb.Append(" · 已运行 "+SvcLines.Dur(DateTime.Now-v.StartedAt.Value));
        string ram=svc.RamGb; if(ram!="-") sb.Append(" · 内存 "+ram);
        sb.Append(" · 端口 "+svc.Port);
      } else if(starting){ sb.Append(" · 端口 "+svc.Port+" · 正在加载模型，约 30-60s"); }
      else if(busy){ sb.Append(" · 端口 "+svc.Port+"（点「停止模型」按端口回收）"); }
      else { sb.Append(" · 端口 "+svc.Port); }
      curStatus.Text=sb.ToString();
      curStatus.ToolTipText=SvcLines.StatusTip(v);
    }
    if(curCfgLine!=null){
      // 2026-09-26 语义**终稿**（用户第三轮修正："不要同时存在，在选项那里有显示就行"）：
      //   面板各下拉标题（上下文: 64K / MTP: MTP4 / KV 缓存: …）显示的就是本模型生效值 ⇒
      //   本行**不再复述任何参数值**（差异值清单也与标题同时可见 = 重复，用户圈注打回）。
      //   只报"选项里看不到的信息"：
      //     · 自定义启动命令（ArgsCustom）→ 恒显示（这是面板看不出来的形态事实）；
      //     · 其余 → 整行隐藏（没有非重复内容可说；完整命令行等仍在悬浮 ToolTip 里）。
      //   与托盘默认差几项 → 只留在「当前模型」头部的计数（值清单在其 ToolTip，按需才看）。
      curCfgLine.Visible = plan.ArgsCustom && svc==CurSvc();
      curCfgLine.Text = "运行时配置：自定义启动命令（选项标题显示的是生效值；悬浮看完整命令行）";
      curCfgLine.ToolTipText=string.Join("\r\n",SvcLines.CfgLines(v))+"\r\n生效环境: "+v.EnvLine
        +"\r\n完整命令行（点「重启模型」即用它执行）：\r\n"+plan.CommandLine()
        +(plan.Warnings.Count>0?("\r\n⚠️ "+string.Join("\r\n⚠️ ",plan.Warnings)):"");
    }
    if(curOpenDsh!=null||curOpenBuiltin!=null) ApplySessionEntryLabels();
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
    // 生效参数 = **本模型的**（覆盖优先）+ 未覆盖项回落托盘全局默认 —— 与菜单里显示的是同一份
    // （同一个 SvcParams.Resolve 调用点之外的另一处 ⇒ 两边同源，见 SvcSig / RefreshCurGroup）。
    var pp=SvcParams.Resolve(svc.Spec,ctxVal,kvMode,cacheRam,splitMode,tsGpu1,mtpLevel,paramMode,bindAll,gpuSel.CfgString());
    var gpuEff=pp.GpuOrDefault(gpuSel);
    var cpuModeEff=gpuEff.UseCpu||LaunchArgs.EffectiveGpus(gpuSel,gpus.Count).Count==0;
    var build=LaunchArgs.Build(svc.Spec,gpuEff,pp.Ctx,pp.ParamMode,pp.SplitMode,pp.KvMode,pp.CacheRam,pp.TsGpu1,gpus.Count,pp.BindAll,pp.MtpLevel,SlotSavePath());
    // 生效的 exe / 参数 / 环境由 launch plan 合成（服务项可整体覆盖，见 LaunchPlan 的文件头注释）
    var plan=LaunchPlan.Compose(svc.Spec,build,cfg.LlamaServerExe);
    // --slot-save-path 指向的目录必须**先存在**（llama-server 不会代建；不存在时 save 会失败）
    if(plan.Args.Contains("--slot-save-path")){ try{ Directory.CreateDirectory(slotSavePath); }catch{} }
    var psi=new ProcessStartInfo(plan.Exe,string.Join(" ",plan.Args)){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
    // env 已含派生的 CUDA_VISIBLE_DEVICES / GGML_CUDA_ALLREDUCE，以及服务项自己追加的项（同名后者胜）
    foreach(var kv in plan.Env) psi.Environment[kv.Key]=kv.Value;
    try{
      svc.proc=Process.Start(psi); svc.proc.OutputDataReceived+=(o,e)=>{if(e.Data!=null){svc.log.AppendLine(e.Data); try{ perf?.OnLlamaLine(svc.Spec,e.Data); }catch{} try{ int _p,_u,_l; if(LlamaLogParser.TryParseCacheState(e.Data, out _p, out _u, out _l)){ svc.cacheRamUsedMib=_u; svc.cacheRamLimitMib=_l; } }catch{}}}; svc.proc.ErrorDataReceived+=(o,e)=>{if(e.Data!=null)svc.log.AppendLine("[err] "+e.Data);}; svc.proc.BeginOutputReadLine(); svc.proc.BeginErrorReadLine();
      // —— 运行态登记（L3 绑定校验里「启动方」的那一半，2026-09-13）——
      // 为什么必须由托盘写：/props **不暴露** --cache-type-k/v 与 flash-attn，而 restore
      // 只校验 (magic,version)、不校验模型/KV 类型（src/llama-context.cpp:3274）⇒ 换 KV 类型后
      // 拿旧条目 restore 会走到 GGML_ASSERT，最坏 abort 掉服务。所以「这次用的是哪套参数」
      // 只能由启动方登记，kvctl 的 binding_check 拿它逐项比对。
      try{
        if(build.args.Contains("--slot-save-path")){
          var kvt=Warmup.KvTypes(pp.KvMode,cpuModeEff);
          string rj=Warmup.RuntimeJson(svc.Model,kvt.k,kvt.v,cpuModeEff?"auto":"on",pp.Ctx,
                                       svc.proc!=null?svc.proc.Id:0,svc.Port);
          string rp=Warmup.RuntimePathFor(slotSavePath), werr="";
          if(!Warmup.WriteRuntime(rp,rj,out werr)) Log(svc,"    [warn] 运行态登记失败: "+werr+"（预热会因「无从核对」而拒绝）\r\n");
        }
      }catch{}
      svc.cacheRamUsedMib=0; svc.cacheRamLimitMib=0;   // 新进程：清掉上一实例遗留的缓存读数（-lv 4 才有新值）
      svc.Starting=true; svc.startMs=Environment.TickCount; svc.runCtx=0; svc.runVision=-1;   // 启动中 → Tick 里 /health 探活，就绪后清标志
      Log(svc,">>> 启动 "+svc.Name+" (端口 "+svc.Port+")\r\n");
      if(plan.ArgsCustom){
        // 自定义参数的服务：托盘参数（GPU/切分/KV/MTP/缓存内存）**一条都不适用**，别把它们当成事实报出来
        Log(svc,"    exe="+SvcLines.FileName(plan.Exe)+" | 自定义参数 "+plan.Args.Count+" 项（非内置模板）\r\n");
      } else {
        Log(svc,"    GPU="+gpuEff.Describe(gpus)+" | ctx="+(pp.Ctx/1024)+"K | CUDA_VISIBLE_DEVICES="+(build.envCuda.Length>0?build.envCuda:"-")+" | 参数组="+(pp.ParamMode==0?"通用思考":pp.ParamMode==1?"编码思考":"Instruct")+" | 切分="+(pp.SplitMode==0?"按层 layer":pp.SplitMode==1?"张量并行 tensor("+pp.TsGpu1+"%)":"-")+" | KV="+KvLabel(pp.KvMode)+" | 缓存内存="+CacheRamLabel(pp.CacheRam)+" | MTP="+MtpLabel(pp.MtpLevel)+" | 监听="+(pp.BindAll?"0.0.0.0":"127.0.0.1")+(pp.Overrides>0?(" | 本模型覆盖 "+pp.Overrides+" 项"):"")+"\r\n");
        if(gpuEff.UseCpu) Log(svc,"    注意: CPU 模式（-ngl 0），速度会显著变慢\r\n");
        else if(pp.Ctx>=196608 && LaunchArgs.EffectiveGpus(gpuEff,gpus.Count).Count==1 && (svc.Model.Contains("35B")||svc.Model.Contains("27B"))) Log(svc,"    注意: "+(pp.Ctx/1024)+"K 上下文 + 单 GPU 可能显存不足（OOM），建议 GPU=全部（多卡，切分模式=按层 layer）或降低 ctx\r\n");
      }
      if(plan.Env.Count>0) Log(svc,"    环境: "+plan.EnvLine()+"\r\n");
      // 降级留痕：参数串引号不配对 / 环境项缺 '=' 这类输入不抛异常（抛了会被菜单刷新的 try/catch 吞掉），改在这里出声
      foreach(var w in plan.Warnings) Log(svc,"    [warn] "+w+"\r\n");
      Log(svc,"    首次加载约 30-60s\r\n");
      string cmdLine = plan.CommandLine() + (plan.Env.Count>0?" ["+plan.EnvLine()+"]":"");
      Log(svc,"    命令: "+cmdLine+"\r\n");
      try{
        bool isNewCfg=false; string cfgId="";
        // 台账要记**生效的**那一套：args 用 plan.Args、exe 用 plan.Exe，否则自定义参数的服务会被
        // 当成"内置模板 + 另一个 exe"登记成一条不存在的配置；env 两项由 plan 反查（无则空串）。
        var effBuild=new LaunchResult{ args=plan.Args,
          envCuda      = plan.Env.TryGetValue("CUDA_VISIBLE_DEVICES", out var _ec)?_ec:"",
          envAllreduce = plan.Env.TryGetValue("GGML_CUDA_ALLREDUCE", out var _ea)?_ea:"" };
        if(perf!=null) cfgId=perf.OnStart(svc.Spec,effBuild,plan.Exe,out isNewCfg);
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
