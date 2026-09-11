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

// S1（2026-09-11）拆自 ModelPerf.cs（手动基准）：零逻辑改动，仅位移。
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
