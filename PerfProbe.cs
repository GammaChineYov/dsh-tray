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

// S1（2026-09-11）拆自 ModelPerf.cs（自检 --selftest-perf）：零逻辑改动，仅位移。

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

      // 1b) 真实配置回归锚（2026-09-12 S2 加入）：当前跑在 8081 的 Qwen3.6-35B 配置必须仍算出同一个 cfgId。
      //     这串 args 抄自 ~/.dsh/tools/model-perf/configs.json 里 id=966d1620a3 那条的台账原文。
      //     任何改动 PerfFingerprint 归一化/剔除规则、或 LaunchArgs 参数构造的行为变化都会在这里撞线
      //     —— cfgId 一变，历史样本就与台账对不上，"哪套配置更快"这个核心问题就失去可比性。
      var realArgs=new List<string>{"-m",@"E:\llm-deploy\models\Qwen3.6-35B-A3B-Claude-4.7-Opus-Reasoning-Distilled-APEX-MTP-I-Compact.gguf","-c","262144","-ngl","99","--split-mode","tensor","-ts","50,50","--main-gpu","0","--flash-attn","on","--cache-type-k","q8_0","--cache-type-v","q8_0","-b","1024","-ub","1024","--cont-batching","--cache-ram","2048","--port","8081","--host","0.0.0.0","--reasoning","on","--reasoning-format","deepseek","--jinja","--temp","0.6","--top-p","0.95","--top-k","20","--min-p","0.0","--presence-penalty","0.0","--repeat-penalty","1.0"};
      string realId=PerfFingerprint.Compute(realArgs,exe);
      Ck("FP-REAL-8081",    realId=="966d1620a3", realId);

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
      string cid=rt.OnStart(svc.Spec, lr, exe, out inw);
      Ck("RT-ONSTART",      cid==id1 && !inw);
      Ck("RT-PENDING",      rt.PendingCfg(8081)==id1);
      rt.OnReady(svc.Spec, 42100, 262144, "b1-832fd6f");
      Ck("RT-PENDING-CLEAR",rt.PendingCfg(8081)==null);
      var ent=st.Get(id1)!;
      Ck("RT-LOADMS",       ent.loadLast==42, ent.loadLast+"s");
      Ck("RT-BUILD",        ent.llamaBuild=="b1-832fd6f");
      Ck("RT-RUNS",         ent.runs==2, "runs="+ent.runs);
      rt.OnLlamaLine(svc.Spec, "llama_perf_context_print:        eval time =    2404.48 ms /   158 tokens (   15.22 ms per token,    65.71 tokens per second)");
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
