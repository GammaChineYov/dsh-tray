using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace QwenTray;

public class LogForm : Form {
  public RichTextBox box=new();
  public LogForm(){ Text="DSH托盘 日志"; Size=new Size(680,460); box.Dock=DockStyle.Fill; box.ReadOnly=true; box.Font=new Font("Consolas",9); Controls.Add(box); FormClosing+=(s,e)=>{ e.Cancel=true; this.Hide(); }; }
  public void Append(string s){ try{ if(string.IsNullOrEmpty(s))return; if(box.IsDisposed)return; if(box.InvokeRequired){ box.BeginInvoke((MethodInvoker)(()=>{ if(!box.IsDisposed) box.AppendText(s); })); } else box.AppendText(s); }catch{} }
}

public class TrayApp : ApplicationContext {
  NotifyIcon icon; ContextMenuStrip menu; LogForm logForm; System.Windows.Forms.Timer clock; bool adopted=false;
  ToolStripMenuItem? itemOpenChat; ToolStripSeparator? sepSess; ToolStripMenuItem? pm,pm0,pm1,pm2;
  ToolStripMenuItem? gpuMenu,gpuAllItem,gpuCpuItem,ctxMenu; List<(ToolStripMenuItem item,int idx)> gpuItems=new(); List<ToolStripMenuItem> ctxItems=new();
  Form? popup; WebView2? wv; Form? officialForm; WebView2? officialWv;
  string gpuTip=""; int gpuTick=0;
  List<Gpu> gpus=new(); GpuSelection gpuSel=GpuSelection.FromCfg("all"); int ctxVal=196608;
  int paramMode=1; // 0=通用思考 1=编码思考 2=Instruct
  AppConfig cfg; List<Service> services=new(); List<(ToolStripMenuItem item,Service svc)> sessionItems=new(); List<(ToolStripMenuItem item,Service svc)> startItems=new();

  public TrayApp(){
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
    icon=new NotifyIcon{Icon=WhaleIcon(),Text="DSH托盘",Visible=true};
    menu=new ContextMenuStrip();
    var items=new List<ToolStripItem>();
    // 打开 DSH 会话（每服务一条，按运行态显隐）
    foreach(var s2 in services){ var it=new ToolStripMenuItem("打开 DSH 会话（"+s2.Name+"）",null,(e,a)=>OpenSession(s2)); sessionItems.Add((it,s2)); items.Add(it); }
    itemOpenChat=new ToolStripMenuItem("打开官方会话 chat",null,(s,e)=>OpenOfficial());
    items.Add(itemOpenChat);
    sepSess=new ToolStripSeparator(); items.Add(sepSess); items.Add(new ToolStripSeparator());
    // 启动（每服务一条）
    foreach(var s2 in services){ var it=new ToolStripMenuItem("启动 "+s2.Name+" ("+s2.Port+")",null,(e,a)=>Start(s2)); startItems.Add((it,s2)); items.Add(it); }
    items.Add(new ToolStripSeparator());
    var m4=new ToolStripMenuItem("停止所选服务",null,(s,e)=>StopSel());
    var m5=new ToolStripMenuItem("停止全部",null,(s,e)=>StopAll());
    var m6=new ToolStripMenuItem("重启全部",null,(s,e)=>RestartAll());
    items.Add(m4); items.Add(m5); items.Add(m6);
    items.Add(new ToolStripSeparator());
    // 推理参数组（radio）
    pm=new ToolStripMenuItem("推理参数组");
    pm0=new ToolStripMenuItem("通用思考 (temp1.0/pres1.5)",null,(s,e)=>SetParam(0));
    pm1=new ToolStripMenuItem("编码思考 (temp0.6/pres0.0)",null,(s,e)=>SetParam(1));
    pm2=new ToolStripMenuItem("Instruct (temp0.7/pres1.5)",null,(s,e)=>SetParam(2));
    pm0.CheckOnClick=false; pm1.CheckOnClick=false; pm2.CheckOnClick=false;
    pm.DropDownItems.AddRange(new ToolStripItem[]{pm0,pm1,pm2});
    items.Add(pm); items.Add(new ToolStripSeparator());
    // GPU 选择（复选框：全部（GPU）/ CPU / GPUn 可多选）
    gpuMenu=new ToolStripMenuItem("GPU: 全部");
    gpuMenu.ToolTipText="启动服务时按所选 GPU 部署：多卡=张量并行(--split-mode row)、单卡=--split-mode none、CPU=-ngl 0";
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
    // 复选/单选二级菜单：点击后不隐藏（点外部/ESC 才关闭）
    KeepOpen(pm.DropDown); KeepOpen(gpuMenu.DropDown); KeepOpen(ctxMenu.DropDown);
    RefreshChecks();
    items.Add(new ToolStripSeparator());
    var m7=new ToolStripMenuItem("查看日志",null,(s,e)=>{logForm.Show();logForm.BringToFront();});
    var openCfg=new ToolStripMenuItem("打开配置文件",null,(s,e)=>{ try{ Process.Start(new ProcessStartInfo(Config.Path_){UseShellExecute=true}); }catch{} });
    items.Add(m7); items.Add(openCfg); items.Add(new ToolStripSeparator());
    var m8=new ToolStripMenuItem("退出",null,(s,e)=>ExitApp());
    items.Add(m8);
    menu.Items.AddRange(items.ToArray());
    icon.ContextMenuStrip=menu; icon.DoubleClick+=(s,e)=>OpenPop();
    clock=new System.Windows.Forms.Timer(); clock.Interval=1000; clock.Tick+=(s,e)=>Tick(); clock.Start();
  }

  string Cfg(){ return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dsh-tray.cfg"); }
  void LoadCfg(){
    try{ if(File.Exists(Cfg())) foreach(var l in File.ReadAllLines(Cfg())){
      if(l.StartsWith("paramMode=")){ int m; if(int.TryParse(l.Substring(10),out m)&&m>=0&&m<=2) paramMode=m; }
      else if(l.StartsWith("gpu=")) gpuSel=GpuSelection.FromCfg(l.Substring(4));
      else if(l.StartsWith("ctx=")){ int v; if(int.TryParse(l.Substring(4),out v)&&Array.IndexOf(LaunchArgs.CtxOptions,v)>=0) ctxVal=v; }
    } }catch{}
  }
  void SaveCfg(){ try{ File.WriteAllText(Cfg(),"paramMode="+paramMode+"\r\ngpu="+gpuSel.CfgString()+"\r\nctx="+ctxVal+"\r\n"); }catch{} }
  void SetParam(int m){ paramMode=m; RefreshChecks(); SaveCfg(); string label=m==0?"通用思考 (temp1.0/pres1.5)":m==1?"编码思考 (temp0.6/pres0.0)":"Instruct (temp0.7/pres1.5)"; logForm.Append("推理参数组: "+label+"（重启对应服务后生效）\r\n"); }
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
    if(gpuMenu!=null) gpuMenu.Text="GPU: "+gpuSel.ShortLabel();
    if(ctxMenu!=null) ctxMenu.Text="上下文: "+(ctxVal/1024)+"K";
  }

  bool PortUp(int port){ try{ using(var wc=new System.Net.WebClient()){ wc.DownloadString("http://127.0.0.1:"+port+"/health"); return true; } }catch{} return false; }
  void Start(Service svc){
    if(svc.Running){ Log(svc,svc.Name+" 已在运行 (端口 "+svc.Port+")\r\n"); return; }
    if(PortUp(svc.Port)){ Log(svc,"端口 "+svc.Port+" 已有服务在跑（非本应用启动），请先停用外部进程。\r\n"); return; }
    var build=LaunchArgs.Build(svc,gpuSel,ctxVal,paramMode,gpus.Count);
    var psi=new ProcessStartInfo(cfg.LlamaServerExe,string.Join(" ",build.args)){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
    if(build.envCuda.Length>0) psi.Environment["CUDA_VISIBLE_DEVICES"]=build.envCuda;
    try{
      svc.proc=Process.Start(psi); svc.proc.OutputDataReceived+=(o,e)=>{if(e.Data!=null)svc.log.AppendLine(e.Data);}; svc.proc.ErrorDataReceived+=(o,e)=>{if(e.Data!=null)svc.log.AppendLine("[err] "+e.Data);}; svc.proc.BeginOutputReadLine(); svc.proc.BeginErrorReadLine();
      Log(svc,">>> 启动 "+svc.Name+" (端口 "+svc.Port+")\r\n");
      Log(svc,"    GPU="+gpuSel.Describe(gpus)+" | ctx="+(ctxVal/1024)+"K | CUDA_VISIBLE_DEVICES="+(build.envCuda.Length>0?build.envCuda:"-")+" | 参数组="+(paramMode==0?"通用思考":paramMode==1?"编码思考":"Instruct")+"\r\n");
      if(gpuSel.UseCpu) Log(svc,"    注意: CPU 模式（-ngl 0），速度会显著变慢\r\n");
      else if(ctxVal>=196608 && LaunchArgs.EffectiveGpus(gpuSel,gpus.Count).Count==1 && (svc.Model.Contains("35B")||svc.Model.Contains("27B"))) Log(svc,"    注意: "+(ctxVal/1024)+"K 上下文 + 单 GPU 可能显存不足（OOM），建议多卡张量并行或降低 ctx\r\n");
      Log(svc,"    首次加载约 30-60s\r\n");
    }
    catch(Exception ex){ Log(svc,">>> 启动 "+svc.Name+" 失败: "+ex.Message+"\r\n"); }
  }

  void Stop(Service svc){ if(svc.proc!=null&&!svc.proc.HasExited){ Log(svc,">>> 停止 "+svc.Name+" ...\r\n"); try{svc.proc.Kill();}catch{} svc.proc=null; } else Log(svc,svc.Name+" 未运行.\r\n"); }
  void StopSel(){ string names=string.Join("/", services.Select(s=>s.Name+"("+s.Port+")")); if(MessageBox.Show("停止 "+names+"？","确认",MessageBoxButtons.YesNo)==DialogResult.Yes){ foreach(var s in services) Stop(s); } }
  void StopAll(){ foreach(var s in services) Stop(s); }
  void RestartAll(){ StopAll(); foreach(var s in services) Start(s); }
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
  void OpenPop(){
    try{
      if(popup==null || popup.IsDisposed){
        popup=new Form{Text="DSH托盘",Icon=WhaleIcon(),FormBorderStyle=FormBorderStyle.SizableToolWindow,StartPosition=FormStartPosition.CenterScreen,Size=new Size(460,640),ShowInTaskbar=false,MinimizeBox=true};
        popup.FormClosing+=(s,e)=>{ if(e.CloseReason==CloseReason.UserClosing){ e.Cancel=true; popup.Hide(); } };
        wv=new WebView2{Dock=DockStyle.Fill}; popup.Controls.Add(wv);
        popup.Shown+=(s,e)=>{ try{ wv.Source=new Uri(cfg.DshUrl); }catch{} };
      }
      popup.Show(); popup.Activate(); if(popup.WindowState==FormWindowState.Minimized) popup.WindowState=FormWindowState.Normal;
    }catch(Exception ex){ MessageBox.Show("打开会话失败: "+ex.Message); }
  }
  void OpenOfficial(){
    try{
      if(officialForm==null || officialForm.IsDisposed){
        officialForm=new Form{Text="DeepSeek 官方会话",Icon=WhaleIcon(),FormBorderStyle=FormBorderStyle.SizableToolWindow,StartPosition=FormStartPosition.CenterScreen,Size=new Size(980,700),ShowInTaskbar=false,MinimizeBox=true};
        officialForm.FormClosing+=(s,e)=>{ if(e.CloseReason==CloseReason.UserClosing){ e.Cancel=true; officialForm.Hide(); } };
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
    if(!adopted){ adopted=true; foreach(var s in services) Adopt(s); }
    foreach(var si in sessionItems) si.item.Visible = si.svc.Running;
    if(sepSess!=null) sepSess.Visible = services.Any(s=>s.Running);
    gpuTick++; if(gpuTick%5==0){ gpus=GpuInfo.Discover(); gpuTip=string.Join("\n",gpus.Select(g=>g.Line)); }
    string tip="";
    foreach(var s in services) if(s.Running) tip+=Short(s)+"("+s.Port+"):"+s.State+" RAM "+s.RamGb+"\n";
    if(tip.Length==0)tip="（无运行中的模型）\n";
    tip+=gpuTip.Length>0?gpuTip:(gpus.Count>0?"GPU: 查询中":"GPU: 无");
    icon.Text=tip.TrimEnd();
    foreach(var svc in services){ long L=svc.log.Length; if(L>svc.lastLen){ string t=svc.log.ToString((int)svc.lastLen,(int)(L-svc.lastLen)); svc.lastLen=L; logForm.Append("["+svc.Name+"] "+t); } }
  }

  void ExitApp(){ StopAll(); icon.Visible=false; if(popup!=null)popup.Close(); Application.Exit(); }
}

public static class Program2 { [STAThread] public static void Main(){ Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new TrayApp()); } }
