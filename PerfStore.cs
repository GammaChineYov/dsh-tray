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

// S1（2026-09-11）拆自 ModelPerf.cs（两层文件存储 + 体积闸门）：零逻辑改动，仅位移。
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
    // S0-⑦ 真·尾部读：只解析分片末尾的若干行，代价与 maxLines 成正比、与文件大小无关。
    // 原实现是 File.ReadAllLines 整片全读再 Skip().Take() —— 注释写着"避免越用越慢"，实现却在越用越慢。
    var res=new List<string>();
    try{
      if(maxLines<=0) return res;
      var files=Directory.GetFiles(Dir,"samples-*.ndjson").Where(f=>!f.EndsWith(".gz",StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f=>f).Take(2).OrderBy(f=>f).ToList();
      for(int i=files.Count-1;i>=0&&res.Count<maxLines;i--){   // 从最新分片往回取，凑够即止
        var tail=ReadTailLines(files[i],maxLines);
        if(tail.Count>0) res.InsertRange(0,tail);
      }
      if(res.Count>maxLines) res=res.Skip(res.Count-maxLines).ToList();
    }catch{}
    return res;
  }

  // 从文件尾部倒读最多 maxLines 行：按块反向扫描 '\n'。
  // UTF-8 的续字节恒 ≥ 0x80，不可能等于 0x0A，所以按字节切行对中文安全。
  static List<string> ReadTailLines(string path,int maxLines){
    var rev=new List<string>();
    try{
      using(var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
        long pos=fs.Length;
        if(pos<=0) return rev;
        const int Chunk=64*1024;
        var buf=new byte[Chunk];
        var cur=new List<byte>(256);
        while(pos>0&&rev.Count<maxLines){
          int n=(int)Math.Min(Chunk,pos); pos-=n;
          fs.Seek(pos,SeekOrigin.Begin);
          int read=0;
          while(read<n){ int r=fs.Read(buf,read,n-read); if(r<=0) break; read+=r; }
          for(int i=read-1;i>=0&&rev.Count<maxLines;i--){
            byte b=buf[i];
            if(b==(byte)'\n'){
              // cur 空且 rev 也空 = 命中"文件以换行结尾"时产生的首个空串，必须跳过（否则会多出一个空行）；
              // 若 rev 已非空，说明文件中确实存在一个空行，必须保留
              if(cur.Count>0||rev.Count>0){
                cur.Reverse();
                rev.Add(System.Text.Encoding.UTF8.GetString(cur.ToArray()).TrimEnd('\r'));
                cur.Clear();
              }
            } else cur.Add(b);
          }
        }
        if(rev.Count<maxLines&&cur.Count>0){ cur.Reverse(); rev.Add(System.Text.Encoding.UTF8.GetString(cur.ToArray()).TrimEnd('\r')); }
      }
    }catch{}
    rev.Reverse();
    return rev;
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
