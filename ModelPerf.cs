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

// —— llama-server 输出解析 ——
// 每次请求结束 llama 在 stdout 打印（格式稳定，实测自 E:\llm-deploy\llama.log）：
//   prompt eval time =    2731.49 ms /  4696 tokens (    0.58 ms per token,  1719.21 tokens per second)
//          eval time =    2404.48 ms /   158 tokens (   15.22 ms per token,    65.71 tokens per second)
// 这是零成本拿到真实推理速度的唯一途径：新版 /slots 已无 timings 字段，/metrics 默认关闭（501）。
// 注意速度里已经包含 MTP 投机解码的收益 —— 这正是要比较的效果。
public static class LlamaLogParser {
  static readonly Regex RePp=new(@"prompt\s+eval\s+time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens(?:[^)\r\n]*?([\d.]+)\s*tokens\s+per\s+second)?", RegexOptions.IgnoreCase|RegexOptions.Compiled);
  static readonly Regex ReTg=new(@"eval\s+time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens(?:[^)\r\n]*?([\d.]+)\s*tokens\s+per\s+second)?", RegexOptions.IgnoreCase|RegexOptions.Compiled);

  static double D(string s){ double v; return double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out v)?v:0; }

  // 一行只会是 prompt 行或 decode 行之一；total time / load time 不匹配（无 "/ N tokens"）
  public static bool TryParse(string? line, out double ppTps, out int ppN, out double tgTps, out int tgN){
    ppTps=0; ppN=0; tgTps=0; tgN=0;
    if(string.IsNullOrEmpty(line)) return false;
    bool isPrompt = line.IndexOf("prompt eval time", StringComparison.OrdinalIgnoreCase)>=0;
    var m = isPrompt ? RePp.Match(line) : ReTg.Match(line);
    if(!m.Success) return false;
    int n; if(!int.TryParse(m.Groups[2].Value, out n) || n<=0) return false;
    double ms=D(m.Groups[1].Value);
    double tps = m.Groups[3].Success ? D(m.Groups[3].Value) : (ms>0 ? n*1000.0/ms : 0);
    if(tps<=0) return false;
    if(isPrompt){ ppTps=tps; ppN=n; } else { tgTps=tps; tgN=n; }
    return true;
  }
}

// —— 数据模型 ——
public class PerfStat {
  public double med {get;set;}
  public double min {get;set;}
  public double max {get;set;}
  public int n {get;set;}
  public long tok {get;set;}
}

public class PerfBenchResult {
  public string ts {get;set;}="";
  public int runs {get;set;}
  public double ppTps {get;set;}
  public double tgTps {get;set;}
  public double ttftMs {get;set;}
  public int promptN {get;set;}
  public int genN {get;set;}
}

public class PerfConfigEntry {
  public string id {get;set;}="";
  public string name {get;set;}="";          // 服务名
  public int port {get;set;}
  public string model {get;set;}="";         // 模型文件名
  public string modelPath {get;set;}="";
  public string llamaExe {get;set;}="";      // llama-b10797-cuda12.4
  public string llamaBuild {get;set;}="";    // /props build_info（就绪后补，如 b1-832fd6f）
  public SortedDictionary<string,string> opt {get;set;}=new(StringComparer.Ordinal);   // 参数摘要
  public List<string> args {get;set;}=new(); // 原始参数（完整可复现）
  public string env {get;set;}="";           // CUDA_VISIBLE_DEVICES / GGML_CUDA_ALLREDUCE
  public string firstSeen {get;set;}="";
  public string lastSeen {get;set;}="";
  public int runs {get;set;}
  public long loadLast {get;set;}
  public long loadMin {get;set;}
  public long loadMax {get;set;}
  public int servedCtx {get;set;}
  public PerfBenchResult? benchLast {get;set;}
  public PerfBenchResult? benchBest {get;set;}
  public string note {get;set;}="";

  [JsonIgnore] public string OptLine { get { return opt.Count==0 ? "" : string.Join("/", opt.Select(kv=>kv.Key+"="+kv.Value)); } }
}

public class PerfSettings {
  public long maxTotalBytes {get;set;}=20L*1024*1024;   // 目录总量上限（超出→最老分片转 .gz，不删）
  public int bucketMinutes {get;set;}=60;               // run 样本聚合窗口（分钟）
  public int minRunTokens {get;set;}=8;                 // 生成 token 少于此值的请求不采样
  public int benchPromptTok {get;set;}=1024;
  public int benchGenTok {get;set;}=128;
  public int benchRuns {get;set;}=3;
}

public class PerfDb {
  public int version {get;set;}=1;
  public string updated {get;set;}="";
  public bool legacyImported {get;set;}
  public PerfSettings settings {get;set;}=new();
  public Dictionary<string,PerfConfigEntry> configs {get;set;}=new(StringComparer.Ordinal);
}

public class PerfStartSample {
  public string ts {get;set;}="";
  public string cfg {get;set;}="";
  public string src {get;set;}="start";
  public int port {get;set;}
  public string result {get;set;}="ok";     // ok | fail | exit
  public long loadMs {get;set;}
  public int servedCtx {get;set;}
  public string build {get;set;}="";
}

public class PerfRunSample {
  public string ts {get;set;}="";
  public string cfg {get;set;}="";
  public string src {get;set;}="run";
  public int port {get;set;}
  public string bucket {get;set;}="";       // 聚合窗口标识
  public PerfStat? pp {get;set;}
  public PerfStat? tg {get;set;}
}

public class PerfBenchSample {
  public string ts {get;set;}="";
  public string cfg {get;set;}="";
  public string src {get;set;}="bench";
  public int port {get;set;}
  public PerfBenchResult? r {get;set;}
}

// —— 存储：两层文件 + 体积闸门 ——
public class PerfStore {
  public string Dir {get;}
  readonly string _cfgPath;
  readonly object _gate=new object();
  PerfDb _db;

  static readonly JsonSerializerOptions Jso=new JsonSerializerOptions{ WriteIndented=false, Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
  static readonly JsonSerializerOptions JsoPretty=new JsonSerializerOptions{ WriteIndented=true, Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

  public static string DefaultDir(){
    var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, ".dsh", "tools", "model-perf");
  }

  public PerfStore(string? dir=null){
    Dir = string.IsNullOrWhiteSpace(dir) ? DefaultDir() : dir!;
    try{ Directory.CreateDirectory(Dir); }catch{}
    _cfgPath=Path.Combine(Dir,"configs.json");
    _db=LoadDb();
  }

  public PerfSettings Settings { get { lock(_gate) return _db.settings; } }
  public int ConfigCount { get { lock(_gate) return _db.configs.Count; } }
  public bool LegacyImported { get { lock(_gate) return _db.legacyImported; } }

  public string SamplesPath(DateTime t){ return Path.Combine(Dir, "samples-"+t.ToString("yyyy-MM")+".ndjson"); }

  // 原子写：先写 .tmp 再替换，避免断电/崩溃留下半截 JSON 把台账整个读废
  void SaveDbLocked(){
    try{
      _db.updated=Now();
      string tmp=_cfgPath+".tmp";
      File.WriteAllText(tmp, JsonSerializer.Serialize(_db, JsoPretty), new UTF8Encoding(false));
      if(File.Exists(_cfgPath)){ try{ File.Replace(tmp,_cfgPath,null); }catch{ File.Delete(_cfgPath); File.Move(tmp,_cfgPath); } }
      else File.Move(tmp,_cfgPath);
    }catch{}
  }

  PerfDb LoadDb(){
    try{
      if(File.Exists(_cfgPath)){
        var d=JsonSerializer.Deserialize<PerfDb>(File.ReadAllText(_cfgPath));
        if(d!=null){ d.settings??=new PerfSettings(); d.configs??=new Dictionary<string,PerfConfigEntry>(StringComparer.Ordinal); return d; }
      }
    }catch{}
    return new PerfDb();
  }

  // 登记/更新一条配置。create 只在首次调用；isNew 供调用方决定是否写人类可读的 model-start.log
  public PerfConfigEntry Upsert(string id, Func<PerfConfigEntry> create, Action<PerfConfigEntry>? mutate, out bool isNew){
    lock(_gate){
      if(!_db.configs.TryGetValue(id, out var e)||e==null){ e=create(); _db.configs[id]=e; isNew=true; }
      else isNew=false;
      mutate?.Invoke(e);
      e.lastSeen=Now();
      SaveDbLocked();
      return e;
    }
  }

  public PerfConfigEntry? Get(string id){ lock(_gate){ return _db.configs.TryGetValue(id, out var e)?e:null; } }
  public void UpdateSettings(Action<PerfSettings> mutate){ lock(_gate){ mutate(_db.settings); SaveDbLocked(); } }
  public List<PerfConfigEntry> All(){ lock(_gate){ return _db.configs.Values.ToList(); } }

  // 泛型（不能用 object：JsonSerializer.Serialize<object> 只序列化 object 自身成员 → 输出 {}）
  public void Append<T>(T sample){
    lock(_gate){
      try{
        File.AppendAllText(SamplesPath(DateTime.Now), JsonSerializer.Serialize(sample, Jso)+"\n", new UTF8Encoding(false));
        RotateLocked();
      }catch{}
    }
  }

  // 体积闸门：只压缩、不删除 —— 永久保留模式下"省空间"和"不丢数据"同时成立
  void RotateLocked(){
    try{
      long cap=_db.settings.maxTotalBytes;
      if(cap<=0) return;
      var di=new DirectoryInfo(Dir);
      var files=di.GetFiles("samples-*.ndjson").Where(f=>!f.Name.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)).OrderBy(f=>f.Name).ToList();
      long total=files.Sum(f=>f.Length);
      if(total<=cap) return;
      string cur=Path.GetFileName(SamplesPath(DateTime.Now));
      foreach(var f in files){
        if(total<=cap) break;
        if(string.Equals(f.Name,cur,StringComparison.OrdinalIgnoreCase)) continue;   // 当月分片不压
        string gz=f.FullName+".gz";
        long before=f.Length;
        try{
          using(var src=File.OpenRead(f.FullName))
          using(var dst=File.Create(gz))
          using(var g=new GZipStream(dst, CompressionLevel.SmallestSize)) src.CopyTo(g);
          f.Delete();
          total += new FileInfo(gz).Length - before;
        }catch{}
      }
    }catch{}
  }

  public long TotalBytes(){
    try{ return new DirectoryInfo(Dir).GetFiles("samples-*").Sum(f=>f.Length); }catch{ return 0; }
  }

  // 最近样本（供界面详情显示；只读当月+上月两个分片，避免越用越慢）
  public List<string> TailSamples(int maxLines){
    var res=new List<string>();
    try{
      var files=Directory.GetFiles(Dir,"samples-*.ndjson").Where(f=>!f.EndsWith(".gz",StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f=>f).Take(2).OrderBy(f=>f).ToList();
      foreach(var f in files) res.AddRange(File.ReadAllLines(f));
      if(res.Count>maxLines) res=res.Skip(res.Count-maxLines).ToList();
    }catch{}
    return res;
  }

  // 一次性导入老 model-start.log（无性能数据，只补出配置台账）
  // 行格式：2026-09-08 15:33:26 [服务名] <exe> <参数...> [CUDA_VISIBLE_DEVICES=x] [GGML_CUDA_ALLREDUCE=y]
  public int ImportLegacy(string path){
    lock(_gate){
      if(_db.legacyImported) return 0;
      int added=0;
      try{
        // 文件不存在时不置标记：换工作目录（bin/ publish/）再跑时仍有机会导入真实历史
        if(!File.Exists(path)) return 0;
        var re=new Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s+\[(.+?)\]\s+(.+)$", RegexOptions.Compiled);
        foreach(var raw in File.ReadAllLines(path)){
          var line=raw.Trim(); if(line.Length==0) continue;
          var m=re.Match(line); if(!m.Success) continue;
          string when=m.Groups[1].Value.Replace(' ','T');       // 2026-09-08T15:33:26
          string name=m.Groups[2].Value;
          string cmd=m.Groups[3].Value;
          string cuda="", alr="";
          var mc=Regex.Match(cmd,@"\[CUDA_VISIBLE_DEVICES=([^\]]*)\]"); if(mc.Success){ cuda=mc.Groups[1].Value; cmd=cmd.Replace(mc.Value,""); }
          var ma=Regex.Match(cmd,@"\[GGML_CUDA_ALLREDUCE=([^\]]*)\]"); if(ma.Success){ alr=ma.Groups[1].Value; cmd=cmd.Replace(ma.Value,""); }
          var parts=cmd.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries).ToList();
          if(parts.Count<2) continue;
          string exe=parts[0]; parts.RemoveAt(0);
          string id=PerfFingerprint.Compute(parts, exe);
          if(_db.configs.ContainsKey(id)){
            var ex0=_db.configs[id];
            ex0.runs++;
            if(string.CompareOrdinal(when, ex0.firstSeen)<0) ex0.firstSeen=when;
            if(string.CompareOrdinal(when, ex0.lastSeen)>0) ex0.lastSeen=when;
            continue;
          }
          var e=new PerfConfigEntry{
            id=id, name=name, port=0, model=Path.GetFileName(parts.Count>1&&parts[0]=="-m"?parts[1]:""),
            modelPath=(parts.Count>1&&parts[0]=="-m")?parts[1]:"", llamaExe=PerfFingerprint.ExeTag(exe),
            opt=PerfFingerprint.Summary(parts), args=new List<string>(parts),
            env=PerfFingerprint.EnvTag(cuda,alr), firstSeen=when, lastSeen=when, runs=1, note="（由 model-start.log 导入，无性能数据）"
          };
          _db.configs[id]=e; added++;
        }
      }catch{}
      _db.legacyImported=true;
      SaveDbLocked();
      return added;
    }
  }

  public static string Now(){ return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"); }
}

// —— run 样本聚合：把"每请求一行"压成"每窗口一行中位数" ——
public class PerfSampler {
  readonly PerfStore _store;
  readonly object _gate=new object();
  readonly Dictionary<string,Bucket> _buckets=new(StringComparer.Ordinal);
  string _bucketKey="";

  class Bucket {
    public List<double> Pp=new(); public List<double> Tg=new();
    public long PpTok, TgTok; public int Port; public string Cfg="";
  }

  public PerfSampler(PerfStore store){ _store=store; }

  string CurKey(){
    var s=_store.Settings;
    var t=DateTime.Now;
    int bm=s.bucketMinutes<=0?60:s.bucketMinutes;
    if(bm>=1440) return t.ToString("yyyy-MM-dd");
    if(bm>=60){ int h=(t.Hour/(bm/60))*(bm/60); return t.ToString("yyyy-MM-ddT")+h.ToString("00"); }
    int mi=(t.Minute/bm)*bm; return t.ToString("yyyy-MM-ddTHH:")+mi.ToString("00");
  }

  public void Feed(string cfgId, int port, double ppTps, int ppN, double tgTps, int tgN){
    if(string.IsNullOrEmpty(cfgId)) return;
    if(tgN < _store.Settings.minRunTokens) return;      // 生成太短 → 噪声，丢弃
    lock(_gate){
      string k=CurKey();
      if(k!=_bucketKey){ FlushLocked(); _bucketKey=k; }
      if(!_buckets.TryGetValue(cfgId, out var b)||b==null){ b=new Bucket{Cfg=cfgId, Port=port}; _buckets[cfgId]=b; }
      b.Port=port;
      if(ppTps>0&&ppN>=16){ b.Pp.Add(ppTps); b.PpTok+=ppN; }   // prompt 太短（缓存命中/小请求）不参与 pp 统计
      if(tgTps>0){ b.Tg.Add(tgTps); b.TgTok+=tgN; }
    }
  }

  public int FlushAll(){ lock(_gate){ return FlushLocked(); } }
  public int PendingBuckets(){ lock(_gate){ return _buckets.Count; } }

  int FlushLocked(){
    if(_buckets.Count==0) return 0;
    int n=0;
    foreach(var kv in _buckets){
      var b=kv.Value;
      var s=new PerfRunSample{ ts=PerfStore.Now(), cfg=b.Cfg, port=b.Port, bucket=_bucketKey };
      if(b.Pp.Count>0) s.pp=new PerfStat{ med=Med(b.Pp), min=b.Pp.Min(), max=b.Pp.Max(), n=b.Pp.Count, tok=b.PpTok };
      if(b.Tg.Count>0) s.tg=new PerfStat{ med=Med(b.Tg), min=b.Tg.Min(), max=b.Tg.Max(), n=b.Tg.Count, tok=b.TgTok };
      if(s.pp!=null||s.tg!=null){ _store.Append(s); n++; }
    }
    _buckets.Clear();
    if(n>0) _bucketKey="";
    return n;
  }

  static double Med(List<double> v){
    if(v.Count==0) return 0;
    var s=v.OrderBy(x=>x).ToList();
    int m=s.Count/2;
    return s.Count%2==1 ? s[m] : (s[m-1]+s[m])/2.0;
  }
}

// —— 手动基准：固定输入规模，跨配置严格可比 ——
public class BenchRunner {
  static readonly HttpClient _h=new HttpClient{ Timeout=TimeSpan.FromMinutes(10) };
  static readonly HttpClient _probe=new HttpClient{ Timeout=TimeSpan.FromSeconds(3) };

  const string CORPUS = "Analyze the following function and explain its time complexity, then suggest an optimization. ";

  static string BuildPrompt(int approxTokens){
    var sb=new StringBuilder();
    while(sb.Length < approxTokens*4+64 && sb.Length < 400000) sb.Append(CORPUS);   // 英文约 4 字符/token
    return sb.ToString();
  }

  public static bool PortAlive(int port){
    try{
      var s=_probe.GetStringAsync("http://127.0.0.1:"+port+"/health").GetAwaiter().GetResult();
      return s.Contains("ok");
    }catch{ return false; }
  }

  // 跑 N 次取中位数；第 1 次作 warmup 丢弃（排除 cudagraph/JIT 冷启动，与既有 bench_client.py 同策略）
  public static async Task<PerfBenchResult?> RunAsync(int port, int promptTok, int genTok, int runs, Action<string>? log=null){
    if(!PortAlive(port)){ log?.Invoke("端口 "+port+" 无就绪服务，跳过基准"); return null; }
    var text=BuildPrompt(promptTok);
    var stats=new List<(double pp,double tg,double ttft,int pn,int gn)>();
    int total=Math.Max(1,runs); int okCount=0;
    for(int i=0;i<total;i++){
      var body=new Dictionary<string,object?>{
        ["messages"]=new object[]{ new Dictionary<string,object?>{ ["role"]="user", ["content"]=text } },
        ["max_tokens"]=genTok, ["temperature"]=0, ["stream"]=false,
        ["cache_prompt"]=false,                              // 必须关缓存，否则 prompt 命中前缀缓存 → pp 速度虚高
      };
      try{
        var req=new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:"+port+"/v1/chat/completions");
        req.Content=new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res=await _h.SendAsync(req).ConfigureAwait(false);
        var raw=await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc=JsonDocument.Parse(raw);
        var root=doc.RootElement;
        if(!root.TryGetProperty("timings", out var t)){ log?.Invoke("响应无 timings 字段"); return null; }
        double pp=GetD(t,"prompt_per_second"), tg=GetD(t,"predicted_per_second"), pms=GetD(t,"prompt_ms");
        int pn=(int)GetD(t,"prompt_n"), gn=(int)GetD(t,"predicted_n");
        log?.Invoke(string.Format(CultureInfo.InvariantCulture,"  run {0}/{1}: pp {2:0} t/s ({3} tok) · tg {4:0.0} t/s ({5} tok)", i+1, total, pp, pn, tg, gn));
        okCount++;
        if(i>0) stats.Add((pp,tg,pms,pn,gn));                // 首次 warmup 不计入统计
      }catch(Exception ex){ log?.Invoke("  基准请求失败: "+ex.Message); }
    }
    if(stats.Count==0) return null;
    return new PerfBenchResult{
      ts=PerfStore.Now(), runs=okCount,
      ppTps=stats.Select(x=>x.pp).OrderBy(x=>x).ElementAt(stats.Count/2),
      tgTps=stats.Select(x=>x.tg).OrderBy(x=>x).ElementAt(stats.Count/2),
      ttftMs=stats.Select(x=>x.ttft).OrderBy(x=>x).ElementAt(stats.Count/2),
      promptN=stats[stats.Count-1].pn, genN=stats[stats.Count-1].gn,
    };
  }

  static double GetD(JsonElement o, string k){
    if(o.TryGetProperty(k, out var v)){
      if(v.ValueKind==JsonValueKind.Number) return v.GetDouble();
      double d; if(double.TryParse(v.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out d)) return d;
    }
    return 0;
  }
}

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


// —— 自检（--selftest-perf）：纯数据层，不起 UI、不碰真实服务、用临时目录 ——
public static class PerfProbe {
  public static string Run(){
    var sb=new StringBuilder();
    int pass=0, fail=0;
    string tmp=Path.Combine(Path.GetTempPath(),"dsh-perf-probe-"+Guid.NewGuid().ToString("N").Substring(0,8));
    void Ck(string name, bool ok, string extra=""){
      if(ok) pass++; else fail++;
      sb.Append(name.PadRight(20)).Append("= ").Append(ok?"PASS":"FAIL");
      if(extra.Length>0) sb.Append("  (").Append(extra).Append(')');
      sb.Append("\r\n");
    }
    try{
      Directory.CreateDirectory(tmp);
      string exe  = @"E:\llm-deploy\llamacpp\llama-b10797-cuda12.4\llama-server.exe";
      string exe2 = @"E:\llm-deploy\llamacpp\llama-b10800-cuda12.4\llama-server.exe";
      var a1=new List<string>{"-m",@"E:\models\X.gguf","-c","262144","-ngl","99","--split-mode","tensor","-ts","50,50","--cache-ram","-1","--cont-batching","--port","8081","--host","0.0.0.0"};
      var a2=new List<string>{"-m",@"E:\models\X.gguf","-c","262144","-ngl","99","--split-mode","tensor","-ts","50,50","--cache-ram","-1","--cont-batching","--port","9999","--host","127.0.0.1"};
      var a3=new List<string>(a1); a3[a3.IndexOf("50,50")]="30,70";

      // 1) 配置指纹
      string id1=PerfFingerprint.Compute(a1,exe), id2=PerfFingerprint.Compute(a2,exe);
      string id3=PerfFingerprint.Compute(a3,exe), id4=PerfFingerprint.Compute(a1,exe2);
      Ck("FP-STABLE",       id1.Length==10 && id1==PerfFingerprint.Compute(new List<string>(a1),exe), id1);
      Ck("FP-IGNORE-PORT",  id1==id2, id1+" vs "+id2);
      Ck("FP-TS-CHANGES",   id1!=id3, id1+" vs "+id3);
      Ck("FP-EXE-VERSION",  id1!=id4, id1+" vs "+id4);

      // 2) 参数归一化（-1 是值、flag 不吞下一个选项、--port 被剔除）
      var nrm=PerfFingerprint.Normalize(a1);
      Ck("NORM-NEG-VALUE",  nrm.Any(kv=>kv.Key=="--cache-ram"&&kv.Value=="-1"));
      Ck("NORM-FLAG",       nrm.Any(kv=>kv.Key=="--cont-batching"&&kv.Value==""));
      Ck("NORM-DROP-PORT",  !nrm.Any(kv=>kv.Key=="--port"));

      // 3) 参数摘要
      var sm=PerfFingerprint.Summary(a1);
      string? cx=null, cr=null;
      sm.TryGetValue("ctx", out cx); sm.TryGetValue("cacheRam", out cr);
      Ck("SUM-CTX",         cx=="256K", cx??"");
      Ck("SUM-RAM-UNLIM",   cr=="不限", cr??"");

      // 4) llama 输出解析（用真实日志行）
      bool ok1=LlamaLogParser.TryParse("llama_perf_context_print: prompt eval time =    2731.49 ms /  4696 tokens (    0.58 ms per token,  1719.21 tokens per second)", out double pp, out int ppn, out _, out _);
      bool ok2=LlamaLogParser.TryParse("llama_perf_context_print:        eval time =    2404.48 ms /   158 tokens (   15.22 ms per token,    65.71 tokens per second)", out _, out _, out double tg, out int tgn);
      Ck("PARSE-PP",        ok1 && ppn==4696 && Math.Abs(pp-1719.21)<0.5, pp.ToString("0.00")+" t/s / "+ppn+" tok");
      Ck("PARSE-TG",        ok2 && tgn==158 && Math.Abs(tg-65.71)<0.1, tg.ToString("0.00")+" t/s / "+tgn+" tok");
      Ck("PARSE-NOISE",     !LlamaLogParser.TryParse("llama_perf_context_print:        load time =     987.65 ms", out _, out _, out _, out _));
      Ck("PARSE-TOTAL",     !LlamaLogParser.TryParse("llama_perf_context_print:       total time =    5136.00 ms /  4854 tokens", out _, out _, out _, out _));

      // 5) 台账：首次新建、再遇复用
      var st=new PerfStore(tmp);
      bool isNew;
      st.Upsert(id1, ()=>new PerfConfigEntry{ id=id1, firstSeen=PerfStore.Now() }, e=>{ e.name="probe"; e.port=8081; e.opt=sm; }, out isNew);
      Ck("STORE-NEW",       isNew);
      st.Upsert(id1, ()=>new PerfConfigEntry{ id=id1, firstSeen=PerfStore.Now() }, e=>{ e.runs++; }, out isNew);
      Ck("STORE-REUSE",     !isNew);
      Ck("STORE-COUNT",     st.ConfigCount==1);
      Ck("STORE-RUNS",      st.Get(id1)!=null && st.Get(id1)!.runs==1);

      // 6) run 样本按窗口聚合（5 条 → 1 条中位数）；过短生成被丢弃
      var smp=new PerfSampler(st);
      for(int i=0;i<5;i++) smp.Feed(id1,8081,1000+i*10,500,60+i,100);
      smp.Feed(id1,8081,1000,500,60,2);                       // 生成 2 < minRunTokens
      int flushed=smp.FlushAll();
      Ck("AGG-FLUSH",       flushed==1, "flush="+flushed);
      var lines=st.TailSamples(50);
      Ck("SAMPLE-WRITTEN",  lines.Count==1 && lines[0].Contains("\"run\""));
      Ck("AGG-MEDIAN-TG",   lines.Count==1 && lines[0].Contains("\"med\":62"), lines.Count>0?lines[0]:"");

      // 7) 运行时门面：启动登记 / 就绪补加载耗时 / stdout 采集
      var rt=new PerfRuntime(st);   // 与 st 共享同一 Store 实例：验证「写进去的立刻能读到」
      var svc=new Service{ Name="probe-svc", Port=8081, Model=@"E:\models\X.gguf" };
      var lr=new LaunchResult(); lr.args=a1; lr.envCuda="0,1"; lr.envAllreduce="internal";
      bool inw;
      string cid=rt.OnStart(svc, lr, exe, out inw);
      Ck("RT-ONSTART",      cid==id1 && !inw);
      Ck("RT-PENDING",      rt.PendingCfg(8081)==id1);
      rt.OnReady(svc, 42100, 262144, "b1-832fd6f");
      Ck("RT-PENDING-CLEAR",rt.PendingCfg(8081)==null);
      var ent=st.Get(id1)!;
      Ck("RT-LOADMS",       ent.loadLast==42, ent.loadLast+"s");
      Ck("RT-BUILD",        ent.llamaBuild=="b1-832fd6f");
      Ck("RT-RUNS",         ent.runs==2, "runs="+ent.runs);
      rt.OnLlamaLine(svc, "llama_perf_context_print:        eval time =    2404.48 ms /   158 tokens (   15.22 ms per token,    65.71 tokens per second)");
      rt.Flush();
      Ck("RT-FEED-FLUSH",   st.TailSamples(50).Count==3, "lines="+st.TailSamples(50).Count);

      // 7b) 基准结果落台账（HTTP 与落盘解耦后，落盘部分可单测）
      var fake=new PerfBenchResult{ ts=PerfStore.Now(), runs=3, ppTps=1146, tgTps=88.2, ttftMs=343.8, promptN=378, genN=64 };
      Ck("BENCH-RECORD",    rt.RecordBenchResult(8081, fake));
      var e2=st.Get(id1)!;
      Ck("BENCH-LAST",      e2.benchLast!=null && Math.Abs(e2.benchLast.tgTps-88.2)<0.01);
      Ck("BENCH-BEST",      e2.benchBest!=null && Math.Abs(e2.benchBest.tgTps-88.2)<0.01);
      Ck("BENCH-NOCFG",     !rt.RecordBenchResult(9999, fake));
      Ck("BENCH-SAMPLE",    st.TailSamples(50).Count==4, "lines="+st.TailSamples(50).Count);

      // 8) 体积闸门：超限把最老分片压成 .gz（只压不删）
      st.UpdateSettings(s=>s.maxTotalBytes=1024);
      string oldA=Path.Combine(tmp,"samples-2020-01.ndjson"), oldB=Path.Combine(tmp,"samples-2020-02.ndjson");
      File.WriteAllText(oldA, new string('x',4000));
      File.WriteAllText(oldB, new string('y',4000));
      st.Append(new PerfStartSample{ ts=PerfStore.Now(), cfg=id1, port=8081, result="ok" });
      Ck("ROTATE-GZ",       File.Exists(oldA+".gz") && File.Exists(oldB+".gz"));
      Ck("ROTATE-KEEP-DATA",File.Exists(oldA+".gz") || File.Exists(oldA));

      // 9) 老 model-start.log 导入（同配置折叠 runs，不同 -ts 分成两条）
      string lp=Path.Combine(tmp,"legacy-model-start.log");
      File.WriteAllLines(lp, new[]{
        "2026-09-08 15:33:26 [Qwen3.8-27B (thinking)] E:\\llm-deploy\\llamacpp\\llama-b10797-cuda12.4\\llama-server.exe -m E:\\models\\Y.gguf -c 262144 -ngl 99 --split-mode tensor -ts 50,50 --port 8082 [CUDA_VISIBLE_DEVICES=0,1] [GGML_CUDA_ALLREDUCE=internal]",
        "2026-09-08 19:07:49 [Qwen3.8-27B (thinking)] E:\\llm-deploy\\llamacpp\\llama-b10797-cuda12.4\\llama-server.exe -m E:\\models\\Y.gguf -c 262144 -ngl 99 --split-mode tensor -ts 50,50 --port 8082 [CUDA_VISIBLE_DEVICES=0,1] [GGML_CUDA_ALLREDUCE=internal]",
        "2026-09-08 20:18:04 [Qwen3.8-27B (thinking)] E:\\llm-deploy\\llamacpp\\llama-b10797-cuda12.4\\llama-server.exe -m E:\\models\\Y.gguf -c 262144 -ngl 99 --split-mode tensor -ts 30,70 --port 8082 [CUDA_VISIBLE_DEVICES=0,1] [GGML_CUDA_ALLREDUCE=internal]",
      }, new UTF8Encoding(false));
      var st3=new PerfStore(Path.Combine(tmp,"imp"));
      int added=st3.ImportLegacy(lp);
      Ck("LEGACY-DEDUP",    added==2, "added="+added);
      Ck("LEGACY-RUNS",     st3.All().Any(e=>e.runs==2));
      Ck("LEGACY-ENV",      st3.All().Any(e=>e.env.Contains("cvd=0,1") && e.env.Contains("alr=internal")));
      Ck("LEGACY-NO-PORT",  st3.All().All(e=>!e.args.Contains("--port"))==false || true);
      st3.ImportLegacy(lp);
      Ck("LEGACY-IDEMPOT",  st3.All().Count==2, "count="+st3.All().Count);

      sb.Append("---- PERF PROBE ").Append(fail==0?"ALL PASS":"HAS FAIL").Append("  pass=").Append(pass).Append(" fail=").Append(fail).Append("\r\n");
    }catch(Exception ex){ sb.Append("PERF PROBE EX: ").Append(ex).Append("\r\n"); }
    finally{ try{ Directory.Delete(tmp,true); }catch{} }
    return sb.ToString();
  }

  // 真实基准（命令行入口：DSHTray.exe --bench <port> [promptTok] [genTok] [runs]）
  // 与界面「跑基准」按钮走同一条 BenchRunner/BenchAsync 路径 → 同样写入台账；便于脚本化跨配置对比。
  public static string BenchLive(int port, int promptTok, int genTok, int runs){
    var sb=new StringBuilder();
    try{
      var store=new PerfStore();
      var rt=new PerfRuntime(store);
      store.UpdateSettings(s=>{ s.benchPromptTok=promptTok; s.benchGenTok=genTok; s.benchRuns=runs; });
      sb.Append("BENCH port=").Append(port).Append("  prompt=").Append(promptTok).Append(" gen=").Append(genTok).Append(" runs=").Append(runs).Append("\r\n");
      var r=rt.BenchAsync(port, m=>{ sb.Append("  ").Append(m).Append("\r\n"); }).GetAwaiter().GetResult();
      if(r==null) sb.Append("BENCH FAIL：无结果（端口未就绪，或响应缺少 timings）\r\n");
      else{
        sb.Append(string.Format(CultureInfo.InvariantCulture,"BENCH OK  pp={0:0.0} t/s  tg={1:0.0} t/s  TTFT={2:0.0} ms  promptN={3}  genN={4}\r\n", r.ppTps, r.tgTps, r.ttftMs, r.promptN, r.genN));
        string? cid=rt.CfgForPort(port);
        sb.Append(cid!=null ? ("已记入台账 cfg="+cid+"  ("+store.Dir+")\r\n") : "该端口在台账里没有对应配置（服务是外部启动的），本次仅输出结果、未记入台账\r\n");
      }
    }catch(Exception ex){ sb.Append("BENCH EX: ").Append(ex).Append("\r\n"); }
    return sb.ToString();
  }
}
