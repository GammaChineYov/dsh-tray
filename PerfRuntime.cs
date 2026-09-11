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

// S1（2026-09-11）拆自 ModelPerf.cs（门面）：零逻辑改动，仅位移。
// —— 运行时门面：TrayApp 只跟它打交道 ——
public class PerfRuntime {
  public PerfStore Store {get;}
  public PerfSampler Sampler {get;}
  readonly Dictionary<int,string> _pending=new();       // port → cfgId（启动中，尚未落样本）
  readonly object _gate=new object();

  public Action<string>? Log;

  public PerfRuntime(string? dir=null) : this(new PerfStore(dir)) { }
  // 允许注入已存在的 Store：同一进程内必须共用同一个实例，否则内存副本各自为政（写文件同步、读到的却是旧值）
  public PerfRuntime(PerfStore store){
    Store=store;
    Sampler=new PerfSampler(store);
  }

  // 启动瞬间：算指纹 + 登记台账（性能数据等就绪/失败时再落）
  public string OnStart(Service svc, LaunchResult build, string exePath, out bool isNew){
    isNew=false;
    try{
      string id=PerfFingerprint.Compute(build.args, exePath);
      var opt=PerfFingerprint.Summary(build.args);
      string model=""; if(opt.TryGetValue("model", out var mv)) model=mv;
      string env=PerfFingerprint.EnvTag(build.envCuda, build.envAllreduce);
      Store.Upsert(id, ()=>new PerfConfigEntry{ id=id, firstSeen=PerfStore.Now() }, e=>{
        e.name=svc.Name; e.port=svc.Port; e.model=model; e.modelPath=svc.Model;
        e.llamaExe=PerfFingerprint.ExeTag(exePath); e.opt=opt; e.args=new List<string>(build.args); e.env=env;
      }, out isNew);
      lock(_gate){
        _pending[svc.Port]=id;
      }
      return id;
    }catch(Exception ex){ Log?.Invoke("性能台账登记失败: "+ex.Message); return ""; }
  }

  public string? PendingCfg(int port){ lock(_gate){ return _pending.TryGetValue(port, out var s)?s:null; } }

  // 就绪：落 start 样本 + 补加载耗时 / 实测 ctx / build_info
  public void OnReady(Service svc, long loadMs, int servedCtx, string? llamaBuild){
    string? id; lock(_gate){ _pending.TryGetValue(svc.Port, out id); _pending.Remove(svc.Port); }
    if(string.IsNullOrEmpty(id)) return;
    try{
      long sec=loadMs>0?loadMs/1000:0;
      Store.Upsert(id!, ()=>new PerfConfigEntry{ id=id!, firstSeen=PerfStore.Now() }, e=>{
        if(sec>0){ e.loadLast=sec; e.loadMin=(e.loadMin<=0||sec<e.loadMin)?sec:e.loadMin; e.loadMax=Math.Max(e.loadMax,sec); }
        if(servedCtx>0) e.servedCtx=servedCtx;
        if(!string.IsNullOrEmpty(llamaBuild)) e.llamaBuild=llamaBuild!;
        e.runs++;
      }, out _);
      Store.Append(new PerfStartSample{ ts=PerfStore.Now(), cfg=id!, port=svc.Port, result="ok", loadMs=sec, servedCtx=servedCtx, build=llamaBuild??"" });
    }catch(Exception ex){ Log?.Invoke("性能样本写入失败: "+ex.Message); }
  }

  // 启动失败/进程退出：同样落一条，失败经验也要留痕
  public void OnFail(Service svc, string result){
    string? id; lock(_gate){ _pending.TryGetValue(svc.Port, out id); _pending.Remove(svc.Port); }
    if(string.IsNullOrEmpty(id)) return;
    try{
      Store.Upsert(id!, ()=>new PerfConfigEntry{ id=id!, firstSeen=PerfStore.Now() }, e=>{ }, out _);
      Store.Append(new PerfStartSample{ ts=PerfStore.Now(), cfg=id!, port=svc.Port, result=result, loadMs=0 });
    }catch{}
  }

  // llama stdout 每行：解析 perf 行喂给聚合器（小时窗口 → 中位数）
  public void OnLlamaLine(Service svc, string? line){
    if(string.IsNullOrEmpty(line)) return;
    if(line.IndexOf("eval time", StringComparison.OrdinalIgnoreCase)<0) return;   // 快速短路，绝大多数行在这里就返回
    string? id=PendingCfg(svc.Port);
    if(string.IsNullOrEmpty(id)) id=CfgForPort(svc.Port);
    if(string.IsNullOrEmpty(id)) return;
    if(LlamaLogParser.TryParse(line, out double pp, out int ppN, out double tg, out int tgN))
      Sampler.Feed(id!, svc.Port, pp, ppN, tg, tgN);
  }

  // 已就绪的服务：从台账里找该端口最后登记的配置
  public string? CfgForPort(int port){
    try{
      var e=Store.All().Where(x=>x.port==port).OrderByDescending(x=>x.lastSeen).FirstOrDefault();
      return e?.id;
    }catch{ return null; }
  }

  public async Task<PerfBenchResult?> BenchAsync(int port, Action<string>? log=null){
    var s=Store.Settings;
    var r=await BenchRunner.RunAsync(port, s.benchPromptTok, s.benchGenTok, s.benchRuns, log).ConfigureAwait(false);
    if(r==null) return null;
    RecordBenchResult(port, r);
    return r;
  }

  // 基准结果落台账 + 落样本（与 HTTP 解耦，便于单测直接喂假结果验证落盘）。
  // 返回 false = 台账里没有该端口的配置（服务是外部启动的）→ 只输出结果、不记档。
  public bool RecordBenchResult(int port, PerfBenchResult r){
    string? id=CfgForPort(port);
    if(string.IsNullOrEmpty(id)) id=PendingCfg(port);
    if(string.IsNullOrEmpty(id)) return false;
    try{
      Store.Upsert(id!, ()=>new PerfConfigEntry{ id=id!, firstSeen=PerfStore.Now() }, e=>{
        e.benchLast=r;
        if(e.benchBest==null || r.tgTps>e.benchBest.tgTps) e.benchBest=r;
      }, out _);
      Store.Append(new PerfBenchSample{ ts=PerfStore.Now(), cfg=id!, port=port, r=r });
      return true;
    }catch{ return false; }
  }

  public void Flush(){ try{ Sampler.FlushAll(); }catch{} }
}
