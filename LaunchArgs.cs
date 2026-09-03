using System;
using System.Collections.Generic;

namespace QwenTray {

// 构建结果：启动参数 + CUDA_VISIBLE_DEVICES（空串 = 不设置）
public class LaunchResult {
  public List<string> args = new List<string>();
  public string envCuda = "";
}

// GPU 选择：CPU / 全部（GPU）/ 指定 GPUn（可多选）
public class GpuSelection {
  public bool UseCpu;          // CPU 模式（-ngl 0）
  public bool UseAll;          // 全部 GPU（张量并行）
  public List<int> Indices = new List<int>(); // 指定 GPU 列表（多选）

  public string CfgString() {
    if (UseCpu) return "cpu";
    if (UseAll) return "all";
    if (Indices.Count > 0) { Indices.Sort(); return string.Join(",", Indices); }
    return "all";
  }
  public static GpuSelection FromCfg(string s) {
    var g = new GpuSelection();
    s = (s ?? "").Trim().ToLowerInvariant();
    if (s == "cpu") g.UseCpu = true;
    else if (s == "all" || s.Length == 0) g.UseAll = true;
    else {
      foreach (var t in s.Split(',')) { int i; if (int.TryParse(t.Trim(), out i) && i >= 0) g.Indices.Add(i); }
      if (g.Indices.Count == 0) g.UseAll = true;
    }
    return g;
  }
  // 描述当前选择（用于日志/菜单提示）
  public string Describe(List<Gpu> gpus) {
    var eff = LaunchArgs.EffectiveGpus(this, gpus.Count);
    if (eff.Count == 0) return "CPU";
    var names = new List<string>();
    foreach (var i in eff) { var g = gpus.Find(x => x.Index == i); names.Add(g != null ? "GPU" + i + "(" + g.PciBus + ")" : "GPU" + i); }
    return string.Join(" + ", names) + (eff.Count > 1 ? "（张量并行）" : "");
  }
  // 一级菜单直接显示的精简标签：全部 / CPU / GPU0 / GPU0+GPU1
  public string ShortLabel() {
    if (UseCpu) return "CPU";
    if (UseAll || Indices.Count == 0) return "全部";
    Indices.Sort();
    return string.Join("+", Indices.ConvertAll(i => "GPU" + i).ToArray());
  }
}

public static class LaunchArgs {
  // ctx 菜单选项：8k/16k/32k/64k/128k/192k/256k
  public static readonly int[] CtxOptions = { 8192, 16384, 32768, 65536, 131072, 196608, 262144 };

  // 实际生效的 GPU 列表（过滤越界索引）
  public static List<int> EffectiveGpus(GpuSelection gpu, int gpuCount) {
    if (gpu.UseCpu) return new List<int>();
    if (gpu.UseAll) { var l = new List<int>(); for (int i = 0; i < gpuCount; i++) l.Add(i); return l; }
    var r = new List<int>();
    foreach (var i in gpu.Indices) if (i < gpuCount) r.Add(i);
    return r;
  }

  // 构建 llama-server 参数 + CUDA_VISIBLE_DEVICES（envCuda 为空串表示不设置）
  // splitMode: 0=按层切分 layer（多卡默认，不依赖 CUDA split buffers）；1=张量并行 row（需 split buffers 支持）
  // GPU 规则：CPU→-ngl 0（去 flash-attn/量化 KV）；单卡→-ngl 99 --split-mode none；多卡→-ngl 99 --split-mode <layer|row> + --main-gpu 0
  public static LaunchResult Build(Service svc, GpuSelection gpu, int ctx, int paramMode, int splitMode, int gpuCount) {
    var r = new LaunchResult();
    var a = r.args;
    a.Add("-m"); a.Add(svc.Model);
    if (svc.UseMmproj) { a.Add("--mmproj"); a.Add(svc.Mmproj); }
    a.Add("-c"); a.Add(ctx.ToString());

    var eff = EffectiveGpus(gpu, gpuCount);
    bool cpu = gpu.UseCpu || eff.Count == 0;
    if (cpu) {
      a.Add("-ngl"); a.Add("0");
    } else if (eff.Count == 1) {
      r.envCuda = eff[0].ToString();
      a.Add("-ngl"); a.Add("99");
      a.Add("--split-mode"); a.Add("none");
    } else {
      r.envCuda = string.Join(",", eff);
      a.Add("-ngl"); a.Add("99");
      a.Add("--split-mode"); a.Add(splitMode==1 ? "row" : "layer"); // row=张量并行(需split buffers)；layer=按层切分
      a.Add("--main-gpu"); a.Add("0");
    }
    if (!cpu) {
      a.Add("--flash-attn"); a.Add("on");
      a.Add("--cache-type-k"); a.Add("q8_0");
      a.Add("--cache-type-v"); a.Add("q8_0");
    }
    a.Add("-b"); a.Add(svc.Batch.ToString());
    a.Add("-ub"); a.Add(svc.Ubatch.ToString());
    a.Add("--cont-batching");
    a.Add("--cache-ram"); a.Add("0");
    a.Add("--port"); a.Add(svc.Port.ToString());
    a.Add("--host"); a.Add("0.0.0.0");
    a.Add("--reasoning"); a.Add("on");
    a.Add("--reasoning-format"); a.Add("deepseek");
    a.Add("--jinja");
    a.AddRange(Sampler(paramMode));
    return r;
  }

  // 推理参数组：0=通用思考 1=编码思考 2=Instruct
  static List<string> Sampler(int m) {
    if (m == 0) return new List<string> { "--temp", "1.0", "--top-p", "0.95", "--top-k", "20", "--min-p", "0.0", "--presence-penalty", "1.5", "--repeat-penalty", "1.0" };
    if (m == 2) return new List<string> { "--temp", "0.7", "--top-p", "0.80", "--top-k", "20", "--min-p", "0.0", "--presence-penalty", "1.5", "--repeat-penalty", "1.0" };
    return new List<string> { "--temp", "0.6", "--top-p", "0.95", "--top-k", "20", "--min-p", "0.0", "--presence-penalty", "0.0", "--repeat-penalty", "1.0" };
  }
}
}
