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

// S1（2026-09-11）拆自 ModelPerf.cs（数据模型）：零逻辑改动，仅位移。
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
