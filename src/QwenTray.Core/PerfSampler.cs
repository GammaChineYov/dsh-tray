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

namespace QwenTray;

// S1（2026-09-11）拆自 ModelPerf.cs（run 样本按小时聚合）：零逻辑改动，仅位移。
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
