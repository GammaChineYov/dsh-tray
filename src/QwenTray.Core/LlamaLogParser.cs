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

// S1（2026-09-11）拆自 ModelPerf.cs（llama stdout 解析）：零逻辑改动，仅位移。
// —— llama-server 输出解析 ——
// 每次请求结束 llama 在 stdout 打印（格式稳定，实测自 E:\llm-deploy\llama.log）：
//   prompt eval time =    2731.49 ms /  4696 tokens (    0.58 ms per token,  1719.21 tokens per second)
//          eval time =    2404.48 ms /   158 tokens (   15.22 ms per token,    65.71 tokens per second)
// 这是零成本拿到真实推理速度的唯一途径：新版 /slots 已无 timings 字段，/metrics 默认关闭（501）。
// 注意速度里已经包含 MTP 投机解码的收益 —— 这正是要比较的效果。
public static class LlamaLogParser {
  static readonly Regex RePp=new(@"prompt\s+eval\s+time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens(?:[^)\r\n]*?([\d.]+)\s*tokens\s+per\s+second)?", RegexOptions.IgnoreCase|RegexOptions.Compiled);
  static readonly Regex ReTg=new(@"eval\s+time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens(?:[^)\r\n]*?([\d.]+)\s*tokens\s+per\s+second)?", RegexOptions.IgnoreCase|RegexOptions.Compiled);
  // prompt cache（--cache-ram）状态行 —— 见 server_prompt_cache::update()（tools/server/server-task.cpp）。
  // 实测（b10797，E:\llm-deploy\llamacpp\llama-b10797-cuda12.4）：
  //   0.03.297.635 I srv        update:  - cache state: 0 prompts, 0.000 MiB (limits: 512.000 MiB, 8192 tokens, 536870912 est)
  // ⚠️ 该行是 SRV_TRC（trace 级）：llama-server 默认 verbosity=3 会滤掉，**必须 -lv 4** 才出现。
  //    /metrics、/slots、/props 三处都**没有** cache-ram 用量（已穷举源码 to_metrics() 与 /slots 的 to_json() 确认）。
  static readonly Regex ReCacheState=new(@"cache state:\s*(\d+)\s+prompts,\s*([\d.]+)\s*MiB\s*\(limits:\s*([\d.]+)\s*MiB", RegexOptions.IgnoreCase|RegexOptions.Compiled);

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

  // prompt cache（--cache-ram）用量/上限。返回 false = 本行不含该信息（多数行如此）。
  // 解码成**整 MiB**：int 字段可 volatile，且 tooltip 只显示整数 MiB，没必要留小数。
  public static bool TryParseCacheState(string? line, out int prompts, out int usedMib, out int limitMib){
    prompts=0; usedMib=0; limitMib=0;
    if(string.IsNullOrEmpty(line)) return false;
    if(line.IndexOf("cache state:", StringComparison.OrdinalIgnoreCase)<0) return false;
    var m=ReCacheState.Match(line);
    if(!m.Success) return false;
    int p; if(!int.TryParse(m.Groups[1].Value, out p)) return false;
    prompts=p;
    usedMib = (int)Math.Round(D(m.Groups[2].Value));
    limitMib= (int)Math.Round(D(m.Groups[3].Value));
    return true;
  }
}
