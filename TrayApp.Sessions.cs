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

// S1（2026-09-11）拆自 Program.cs（partial 4/5：会话/弹窗/插件 + 最近会话缓存）：零逻辑改动，仅位移。
public partial class TrayApp {
  // —— S0 自检：UI 线程调度（报告 §7 S0 验收第 1 条的用户视角剧本）——
  // 场景刻意选"LogForm 已创建但从未 Show"（句柄未创建）—— 旧实现正是在这里把句柄绑到线程池线程，
  // 之后所有 UI 更新静默失效。核心判据是"排队"语义：消息泵未跑时更新**必须还没生效**。
  public string UiThreadProbe(){
    try{ clock.Stop(); }catch{}   // 冻结周期 Tick，避免它在本探针断言期间改写 box
    var sb=new System.Text.StringBuilder();
    int uiThread=Environment.CurrentManagedThreadId;
    int pass=0, fail=0;
    Action<bool,string> ck=(ok,what)=>{ if(ok)pass++; else fail++; sb.AppendLine("  "+(ok?"PASS":"FAIL")+"  "+what); };
    sb.AppendLine("UITHREAD PROBE  (Ui() 与 LogForm.Append 的 marshal 锚点)");
    sb.AppendLine("uiThread="+uiThread+"  logForm.Visible="+logForm.Visible+"  logForm.HandleCreated="+logForm.IsHandleCreated);
    sb.AppendLine("uiDisp: ready="+uiDisp.Ready+" anchorThread="+uiDisp.AnchorThreadId+" onUi="+uiDisp.OnUiThread);
    ck(uiDisp.Ready, "TrayApp 的调度器锚点句柄已在 UI 线程建立（这是修复的前提）");
    ck(uiDisp.AnchorThreadId==uiThread, "调度器锚定在本（UI）线程");
    ck(logForm.Dispatcher.Ready&&logForm.Dispatcher.AnchorThreadId==uiThread, "LogForm 自带调度器同样锚定在 UI 线程");
    int uiRan=-1; int beforeLen=logForm.box.TextLength;
    Task.Run(()=>{ uiDisp.Post("probe",()=>{ uiRan=Environment.CurrentManagedThreadId; }); logForm.Append("PROBE-LINE\r\n"); }).Wait(5000);
    ck(uiRan==-1, "消息泵未跑时 Ui() 回调尚未执行（= 委托被排队，而非在后台线程裸跑）");
    ck(logForm.box.TextLength==beforeLen&&!logForm.box.Text.Contains("PROBE-LINE"), "消息泵未跑时 Append 未写入 box（= 没有在后台线程直接碰控件）");
    for(int i=0;i<40;i++){ Application.DoEvents(); System.Threading.Thread.Sleep(15); }
    ck(uiRan==uiThread, "经过消息泵后 Ui() 回调在 UI 线程执行（实际 tid="+uiRan+"）");
    ck(logForm.box.Text.Contains("PROBE-LINE")&&logForm.box.TextLength>beforeLen, "经过消息泵后 Append 的内容出现在 box 里且长度增长");
    // RichTextBox 有界化（P1-3）
    int oldMax=logForm.MaxChars; logForm.MaxChars=4096;
    try{
      logForm.Append(new string('x',20000)+"\r\n");
      for(int i=0;i<40;i++){ Application.DoEvents(); System.Threading.Thread.Sleep(10); }
      ck(logForm.box.TextLength<=4096+2000, "box 触发上限裁剪（TextLength="+logForm.box.TextLength+"）");
    }finally{ logForm.MaxChars=oldMax; }
    logForm.Append("AFTER-TRIM\r\n");
    for(int i=0;i<20;i++){ Application.DoEvents(); System.Threading.Thread.Sleep(10); }
    ck(logForm.box.Text.Contains("AFTER-TRIM"), "裁剪后仍能继续追加（窗口未被裁剪逻辑卡死）");
    sb.AppendLine("RESULT "+(fail==0?"PASS":"FAIL")+"  pass="+pass+" fail="+fail);
    return sb.ToString();
  }
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
    // 本方法现在跑在后台线程（由 PluginManagerForm.Apply 派发）→ 菜单勾选必须 marshal 回 UI 线程（S0-⑥）
    Ui(()=>RefreshChecks()); SaveCfg();
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

}
