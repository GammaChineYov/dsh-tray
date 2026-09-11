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

// S1（2026-09-11）拆自 ModelPerf.cs（模块总说明 + 配置指纹）：零逻辑改动，仅位移。
// ============================================================================
// 配置粒度模型性能日志（2026-09-11）
//
// 要回答的问题：改了 -ts / ctx / MTP 档 / KV 类型 / cache-ram 之后，到底哪套更快？
//
// 四条设计约束（对应"怕无限膨胀、怕冗余、但要方便分析"）：
//   1. 配置指纹 cfgId = sha1(排序后的启动参数 + llama 目录名)[:10]。
//      剔除 --port/--host（不影响性能，改了不该算"新配置"）。llama 版本目录名参与指纹 ——
//      升级 llama.cpp 会让同参数配置的性能变化，不带上就会把"编译器变了"误读成"配置回退了"。
//   2. 两层存储：configs.json（配置台账，条数 = 你改过多少种参数组合，天然有界，不随时间增长）
//                samples-YYYY-MM.ndjson（样本明细，按月分片）
//      配置正文只在 configs.json 里存一份，样本里只放 cfgId → 不冗余。
//   3. 三道降噪：run 样本按小时聚合成中位数（不是每请求一行）；同配置重复启动只累加 runs；
//                生成 token 数 < minRunTokens 的请求丢弃（排除探针/极短生成污染）。
//   4. 体积闸门：总量超上限时把最老分片压成 .gz（永久保留，只压不删）。
//
// 数据源（均已实测，见 docs/2026-09-11-model-perf-log.md）：
//   src=start : 容器启动 → 加载耗时（Tick 里已算）+ /props 实测 ctx/build_info
//   src=run   : llama stdout 的 "prompt eval time / eval time" 行（托盘已经在收 stdout，零额外开销）
//   src=bench : 手动「跑基准」→ 固定 prompt/生成数，读响应体 timings（跨配置严格可比）
// ============================================================================

// —— 配置指纹 ——
public static class PerfFingerprint {
  static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase){ "--port", "--host" };

  // 是否是参数名而非参数值。注意 "--cache-ram -1" 里的 -1 是值不是选项。
  static bool IsOpt(string s){
    if(s==null||s.Length<2||s[0]!='-') return false;
    return s[1]=='-' ? (s.Length>2 && char.IsLetter(s[2])) : char.IsLetter(s[1]);
  }

  // 归一化：参数名/值配对 → 剔除无关项 → 按名排序（顺序无关）
  public static List<KeyValuePair<string,string>> Normalize(IEnumerable<string> args){
    var list=new List<KeyValuePair<string,string>>();
    string? pend=null;
    foreach(var raw in args){
      if(raw==null) continue;
      if(IsOpt(raw)){ if(pend!=null) list.Add(new KeyValuePair<string,string>(pend,"")); pend=raw; }   // 上一个没跟值 = flag
      else if(pend!=null){ list.Add(new KeyValuePair<string,string>(pend,raw)); pend=null; }
    }
    if(pend!=null) list.Add(new KeyValuePair<string,string>(pend,""));
    return list.Where(kv=>!Skip.Contains(kv.Key)).OrderBy(kv=>kv.Key,StringComparer.Ordinal).ToList();
  }

  public static string Compute(IEnumerable<string> args, string exeFullPath){
    var sb=new StringBuilder();
    foreach(var kv in Normalize(args)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
    sb.Append("exe=").Append(ExeTag(exeFullPath)).Append('\n');
    using var sha=SHA1.Create();
    var h=sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
    var o=new StringBuilder(10);
    for(int i=0;i<5;i++) o.Append(h[i].ToString("x2"));
    return o.ToString();
  }

  // llama-server.exe 所在目录名，如 llama-b10797-cuda12.4（= 版本标识，启动时即可得）
  public static string ExeTag(string exeFullPath){
    try{
      if(string.IsNullOrEmpty(exeFullPath)) return "?";
      var d=Path.GetDirectoryName(exeFullPath);
      return string.IsNullOrEmpty(d) ? Path.GetFileName(exeFullPath) : new DirectoryInfo(d).Name;
    }catch{ return "?"; }
  }

  // 人类可读参数摘要（进 configs.json.opt，供界面一眼对比；原始参数另存 args）
  public static SortedDictionary<string,string> Summary(IEnumerable<string> args){
    var m=new SortedDictionary<string,string>(StringComparer.Ordinal);
    foreach(var kv in Normalize(args)){
      switch(kv.Key){
        case "-m": m["model"]=Path.GetFileName(kv.Value); break;
        case "--mmproj": m["mmproj"]=Path.GetFileName(kv.Value); break;
        case "-c": m["ctx"]=FmtCtx(kv.Value); break;
        case "-ngl": m["ngl"]=kv.Value; break;
        case "--split-mode": m["split"]=kv.Value; break;
        case "-ts": m["ts"]=kv.Value; break;
        case "--flash-attn": m["flash"]=kv.Value; break;
        case "--cache-type-k": m["kvK"]=kv.Value; break;
        case "--cache-type-v": m["kvV"]=kv.Value; break;
        case "-b": m["batch"]=kv.Value; break;
        case "-ub": m["ubatch"]=kv.Value; break;
        case "--cache-ram": m["cacheRam"]=FmtRam(kv.Value); break;
        case "--spec-type": m["spec"]=kv.Value; break;
        case "--spec-draft-n-max": m["mtpN"]=kv.Value; break;
      }
    }
    return m;
  }

  static string FmtCtx(string v){
    if(int.TryParse(v,out int n)&&n>0&&n%1024==0&&n>=1024) return (n/1024)+"K";
    return v;
  }
  static string FmtRam(string v){
    if(int.TryParse(v,out int n)){
      if(n<0) return "不限";
      if(n==0) return "关";
      if(n%1024==0) return (n/1024)+"G";
      return n+"M";
    }
    return v;
  }

  // 环境变量也进指纹：CUDA_VISIBLE_DEVICES / GGML_CUDA_ALLREDUCE 改了就是另一套部署
  public static string EnvTag(string cuda, string allreduce){
    var p=new List<string>();
    if(!string.IsNullOrEmpty(cuda)) p.Add("cvd="+cuda);
    if(!string.IsNullOrEmpty(allreduce)) p.Add("alr="+allreduce);
    return string.Join(";", p);
  }
}
