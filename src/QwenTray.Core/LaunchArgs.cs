using System;
using System.Collections.Generic;

namespace QwenTray {

// 构建结果：启动参数 + CUDA_VISIBLE_DEVICES（空串 = 不设置）
public class LaunchResult {
  public List<string> args = new List<string>();
  public string envCuda = "";
  public string envAllreduce = "";   // 多卡张量并行(tensor)需要内置 CUDA AllReduce（GGML_CUDA_ALLREDUCE=internal）
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
  // 深拷贝（2026-09-26）：「编辑中的 GPU 选择」需要一份可变的临时副本 —— 选中了模型时，
  // 面板编辑只写该模型的覆盖配置，**不得**顺手改掉全局 gpuSel（否则编辑会泄漏成其他
  // 未覆盖模型的基线）。Indices 是 List（引用），MemberwiseClone 之后必须再拷一份。
  public GpuSelection Clone() {
    var g = (GpuSelection)MemberwiseClone();
    g.Indices = new List<int>(Indices);
    return g;
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
  // cache-ram 菜单选项（MiB）：512M/1G/2G/4G/8G/16G/无限制(-1)/禁用(0)
  // 注：8192 正是 llama.cpp 自身默认值；它是「host prompt/前缀缓存」的上限，
  //     与显存 KV cache 无关，因此加大它只吃系统内存，不吃显存。
  public static readonly int[] CacheRamOptions = { 512, 1024, 2048, 4096, 8192, 16384, -1, 0 };

  // 三级降级缓存的**硬盘层**落点（2026-09-13 加）：
  //   L1 显存 = llama KV cache（-c/--cache-type-*）      —— 自动
  //   L2 内存 = --cache-ram 前缀缓存（host RAM）          —— 自动
  //   L3 硬盘 = --slot-save-path + REST /slots 的 save|restore —— 🔑 **策略必须外部驱动**（上游明确不做自动，#17107 关为 not planned）
  //            但**命中判定不用外部做**：restore 回槽后 llama-server 自行做 LCP 前缀匹配
  //            （2026-09-13 实测：erase 后重发 1651 tok = 532.6 ms → restore 后重发 prompt_n=4 / 48.1 ms）
  // 所以这一项是本托盘/L3 管理器要接的原语开关：给了路径才有 /slots action 端点（不给 ⇒ 501）。
  // 实测（build b1-832fd6f / llama-b10797-cuda12.4）：带 mmproj 的视觉模型同样可 save/restore
  // （上游 #21133 称视觉会挡住 slot-save —— 在本 build 上**不成立**，文本与含图上下文均实测通过）。
  public const string DefaultSlotSavePath = @"E:\kv_cache\slots";

  // 实际生效的 GPU 列表（过滤越界索引）
  public static List<int> EffectiveGpus(GpuSelection gpu, int gpuCount) {
    if (gpu.UseCpu) return new List<int>();
    if (gpu.UseAll) { var l = new List<int>(); for (int i = 0; i < gpuCount; i++) l.Add(i); return l; }
    var r = new List<int>();
    foreach (var i in gpu.Indices) if (i < gpuCount) r.Add(i);
    return r;
  }

  // 构建 llama-server 参数 + CUDA_VISIBLE_DEVICES（envCuda 为空串表示不设置）
  // splitMode: 0=按层切分 layer（多卡默认，不依赖 split buffers，但双卡偶发崩）；1=张量并行 tensor（内置 AllReduce，稳定推荐）
  // bindAll: true=--host 0.0.0.0（局域网可访问，默认）；false=--host 127.0.0.1（仅本机）
  // GPU 规则：CPU→-ngl 0（去 flash-attn/量化 KV）；单卡→-ngl 99 --split-mode none；多卡→-ngl 99 --split-mode <layer|row> + --main-gpu 0
  public static LaunchResult Build(ServiceSpec svc, GpuSelection gpu, int ctx, int paramMode, int splitMode, int kvMode, int cacheRam, int tsGpu1, int gpuCount, bool bindAll=true, int mtpLevel=0, string slotSavePath="") {
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
      // 多卡张量并行(tensor)需要内置 CUDA AllReduce（Windows 无 NCCL 时用 GGML_CUDA_ALLREDUCE=internal）
      r.envAllreduce = "internal";
      a.Add("-ngl"); a.Add("99");
      // 张量并行 tensor(内置 AllReduce)：比 layer 稳（layer 双卡偶发崩）、比 row 能加载（row 需 split buffers，本机 MoE/mmproj 模型不支持）
      a.Add("--split-mode"); a.Add(splitMode==1 ? "tensor" : "layer");
      if (splitMode==1) { a.Add("-ts"); a.Add((100-tsGpu1)+","+tsGpu1); } // 张量并行比例：GPU1 占比 tsGpu1%（默认50=均分，越大 GPU1 分越多份额）
      a.Add("--main-gpu"); a.Add("0");
    }
    if (!cpu) {
      a.Add("--flash-attn"); a.Add("on");
      if (kvMode == 1) { a.Add("--cache-type-k"); a.Add("q8_0"); a.Add("--cache-type-v"); a.Add("q8_0"); }      // 8bit q8_0（省显存，默认）
      else if (kvMode == 2) { a.Add("--cache-type-k"); a.Add("f16"); a.Add("--cache-type-v"); a.Add("f16"); } // 16bit f16（显存 +约1倍）
      // kvMode==0（默认）: 不显式指定 cache 类型，用 llama 自身默认（f16）
    }
    a.Add("-b"); a.Add(svc.Batch.ToString());
    a.Add("-ub"); a.Add(svc.Ubatch.ToString());
    a.Add("--cont-batching");
    a.Add("--cache-ram"); a.Add(cacheRam.ToString());
    // L3 硬盘层原语：给了路径，llama-server 才挂 POST /slots/{id}?action=save|restore（不给 ⇒ 501 not_supported）
    if (!string.IsNullOrWhiteSpace(slotSavePath)) { a.Add("--slot-save-path"); a.Add(slotSavePath); }
    a.Add("--port"); a.Add(svc.Port.ToString());
    a.Add("--host"); a.Add(bindAll ? "0.0.0.0" : "127.0.0.1");
    a.Add("--reasoning"); a.Add("on");
    a.Add("--reasoning-format"); a.Add("deepseek");
    a.Add("--jinja");
    if (mtpLevel >= 1) { a.Add("--spec-type"); a.Add("draft-mtp"); a.Add("--spec-draft-n-max"); a.Add(mtpLevel.ToString()); }   // MTP 投机解码档位（全局菜单；mtpLevel=0=不加；--spec-draft-n-max=1..8）
    a.AddRange(Sampler(paramMode));
    return r;
  }

  // DRY 采样四参（2026-09-23 新增，三档共用同一组值/顺序 ⇒ 单变量对照）。
  //
  // 为什么加：本地小模型（35B-A3B MTP-I-Compact）跑批时反复陷入「退化重复循环」——
  //   单步 reasoning 输出 13.8 万~16.7 万字符，尾部 40-gram 唯一率低到 0.012
  //   （同一短语被无分隔地重复数十万字符），最后撞 --max-tokens 收尾。
  //   跨 30 轮会话普查：7 次 max-tokens 里 5 次都是这个模式（a9/a12/a15/a17/a20/a26/a30d2）。
  //   旧的 --repeat-penalty 1.0 在 llama.cpp 语义里 = 惩罚完全关闭，且没有任何 DRY 兜底。
  //   本 build（llama-b10797-cuda12.4）实测支持以下四参：--dry-multiplier 默认 0=关。
  //
  // 回滚：删掉本常量与 Sampler() 里的 AddRange 一行，即完全回到旧行为（无 DRY）。
  //   本轮刻意不动 --temp/--top-p/--top-k/--min-p/--presence-penalty/--repeat-penalty 的取值。
  static readonly string[] DryArgs = { "--dry-multiplier", "0.8", "--dry-base", "1.75", "--dry-allowed-length", "2", "--dry-penalty-last-n", "256" };

  // 推理参数组：0=通用思考 1=编码思考 2=Instruct
  static List<string> Sampler(int m) {
    var a = m == 0
      ? new List<string> { "--temp", "1.0", "--top-p", "0.95", "--top-k", "20", "--min-p", "0.0", "--presence-penalty", "1.5", "--repeat-penalty", "1.0" }
      : m == 2
      ? new List<string> { "--temp", "0.7", "--top-p", "0.80", "--top-k", "20", "--min-p", "0.0", "--presence-penalty", "1.5", "--repeat-penalty", "1.0" }
      : new List<string> { "--temp", "0.6", "--top-p", "0.95", "--top-k", "20", "--min-p", "0.0", "--presence-penalty", "0.0", "--repeat-penalty", "1.0" };
    a.AddRange(DryArgs);   // 三档追加同一组 DRY（值/顺序一致）
    return a;
  }
}
}
