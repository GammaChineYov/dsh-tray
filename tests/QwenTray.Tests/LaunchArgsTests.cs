namespace QwenTray.Tests;

// S4（2026-09-12）：llama-server 启动参数构造。
//
// 这是"配置 → 真正跑起来的东西"的唯一边界：参数错一处 = 模型起不来、或跑在了你以为之外的配置上，
// 而这两件事都只在托盘日志里看得见（起不来只是菜单项点了没反应）。
// 多卡相关的几个开关（--split-mode tensor / -ts / GGML_CUDA_ALLREDUCE=internal）尤其容易静默退化。
public class LaunchArgsTests {
  static ServiceSpec Spec(int port = 8081) => new() {
    Name = "t", Port = port, Model = @"E:\models\X.gguf", Batch = 1024, Ubatch = 1024,
  };
  static string Val(List<string> a, string k) {
    int i = a.IndexOf(k);
    return i >= 0 && i + 1 < a.Count ? a[i + 1] : "<missing>";
  }
  static bool Has(List<string> a, string k) => a.Contains(k);

  // --- GPU 三种模式 ---

  [Fact] public void CpuMode_UsesNgl0_AndSetsNoCudaDevice() {
    var r = LaunchArgs.Build(Spec(), new GpuSelection { UseCpu = true }, 8192, 0, 0, 0, 2048, 50, 2);
    Assert.Equal("0", Val(r.args, "-ngl"));
    Assert.Equal("",  r.envCuda);                    // 空串 = 不设置 CUDA_VISIBLE_DEVICES
    Assert.False(Has(r.args, "--flash-attn"));       // CPU 模式不加 flash-attn / 量化 KV
    Assert.False(Has(r.args, "--split-mode"));
  }

  [Fact] public void SingleGpu_SetsCudaDevice_AndSplitNone() {
    var r = LaunchArgs.Build(Spec(), GpuSelection.FromCfg("1"), 8192, 0, 0, 0, 2048, 50, 2);
    Assert.Equal("1",    r.envCuda);
    Assert.Equal("99",   Val(r.args, "-ngl"));
    Assert.Equal("none", Val(r.args, "--split-mode"));
    Assert.Equal("",     r.envAllreduce);
  }

  [Fact] public void MultiGpu_TensorMode_SetsAllReduceInternal_AndTs() {
    var r = LaunchArgs.Build(Spec(), new GpuSelection { UseAll = true }, 262144, 0, 1, 1, 2048, 50, 2);
    Assert.Equal("0,1",    r.envCuda);
    Assert.Equal("internal", r.envAllreduce);        // Windows 无 NCCL ⇒ GGML_CUDA_ALLREDUCE=internal
    Assert.Equal("tensor", Val(r.args, "--split-mode"));
    Assert.Equal("50,50",  Val(r.args, "-ts"));
    Assert.Equal("0",      Val(r.args, "--main-gpu"));
    Assert.Equal("q8_0",   Val(r.args, "--cache-type-k"));
    Assert.Equal("on",     Val(r.args, "--flash-attn"));
  }

  [Fact] public void MultiGpu_LayerMode_HasNoTs() {
    var r = LaunchArgs.Build(Spec(), new GpuSelection { UseAll = true }, 8192, 0, 0, 0, 2048, 50, 2);
    Assert.Equal("layer", Val(r.args, "--split-mode"));
    Assert.False(Has(r.args, "-ts"));
    Assert.Equal("internal", r.envAllreduce);        // 多卡一律要内置 AllReduce
  }

  // -ts 的写法是 "(100-tsGpu1),tsGpu1" —— 即 tsGpu1 是 **GPU1 的占比**，不是 GPU0 的
  [Fact] public void TsRatio_IsGpu1Percent_AndGoesSecond() {
    var r = LaunchArgs.Build(Spec(), new GpuSelection { UseAll = true }, 8192, 0, 1, 0, 2048, 30, 2);
    Assert.Equal("70,30", Val(r.args, "-ts"));
  }

  // --- 可选参数 ---

  [Fact] public void Mmproj_IsAddedOnlyWhenEnabled() {
    var off = Spec();
    var on  = Spec(); on.UseMmproj = true; on.Mmproj = @"E:\models\mm.gguf";
    Assert.False(Has(LaunchArgs.Build(off, new GpuSelection{UseAll=true}, 8192,0,0,0,0,50,2).args, "--mmproj"));
    var r = LaunchArgs.Build(on, new GpuSelection{UseAll=true}, 8192,0,0,0,0,50,2);
    Assert.Equal(@"E:\models\mm.gguf", Val(r.args, "--mmproj"));
  }

  [Fact] public void Mtp_AddsSpecFlags_OnlyWhenLevelIsAtLeastOne() {
    var l0 = LaunchArgs.Build(Spec(), new GpuSelection{UseAll=true}, 8192,0,0,0,0,50,2, true, 0);
    Assert.False(Has(l0.args, "--spec-type"));
    var l2 = LaunchArgs.Build(Spec(), new GpuSelection{UseAll=true}, 8192,0,0,0,0,50,2, true, 2);
    Assert.Equal("draft-mtp", Val(l2.args, "--spec-type"));
    Assert.Equal("2",         Val(l2.args, "--spec-draft-n-max"));
  }

  [Fact] public void KvMode_SelectsCacheTypes() {
    var f16 = LaunchArgs.Build(Spec(), new GpuSelection{UseAll=true}, 8192,0,0,2,0,50,2);
    Assert.Equal("f16", Val(f16.args, "--cache-type-k"));
    Assert.Equal("f16", Val(f16.args, "--cache-type-v"));

    // kvMode=0 是默认：**不显式指定** cache 类型，交给 llama 自身默认
    var none = LaunchArgs.Build(Spec(), new GpuSelection{UseAll=true}, 8192,0,0,0,0,50,2);
    Assert.False(Has(none.args, "--cache-type-k"));
    Assert.False(Has(none.args, "--cache-type-v"));
  }

  [Fact] public void BindAll_ControlsHost() {
    Assert.Equal("0.0.0.0",   Val(LaunchArgs.Build(Spec(), new GpuSelection{UseAll=true},8192,0,0,0,0,50,2, true).args,  "--host"));
    Assert.Equal("127.0.0.1", Val(LaunchArgs.Build(Spec(), new GpuSelection{UseAll=true},8192,0,0,0,0,50,2, false).args, "--host"));
  }

  [Fact] public void PortBatchAndContext_AreCarriedThrough() {
    var r = LaunchArgs.Build(Spec(8099), new GpuSelection{UseAll=true}, 65536, 0, 0, 0, 4096, 50, 2);
    Assert.Equal("8099",  Val(r.args, "--port"));
    Assert.Equal("65536", Val(r.args, "-c"));
    Assert.Equal("1024",  Val(r.args, "-b"));
    Assert.Equal("1024",  Val(r.args, "-ub"));
    Assert.Equal("4096",  Val(r.args, "--cache-ram"));
    Assert.True(Has(r.args, "--cont-batching"));
  }

  [Fact] public void SamplerMode_SelectsTheDocumentedPreset() {
    Assert.Equal("1.0", Val(LaunchArgs.Build(Spec(), new GpuSelection{UseAll=true},8192, 0,0,0,0,50,2).args, "--temp"));
    Assert.Equal("0.7", Val(LaunchArgs.Build(Spec(), new GpuSelection{UseAll=true},8192, 2,0,0,0,50,2).args, "--temp"));
  }

  [Fact] public void ReasoningAndJinja_AreAlwaysOn() {
    var a = LaunchArgs.Build(Spec(), new GpuSelection{UseAll=true},8192,0,0,0,0,50,2).args;
    Assert.Equal("on",       Val(a, "--reasoning"));
    Assert.Equal("deepseek", Val(a, "--reasoning-format"));
    Assert.True(Has(a, "--jinja"));
  }

  // --- GpuSelection ---

  [Theory]
  [InlineData("cpu",  true,  false, "cpu")]
  [InlineData("all",  false, true,  "all")]
  [InlineData("",     false, true,  "all")]
  public void FromCfg_ParsesTheKnownForms(string cfg, bool useCpu, bool useAll, string roundTrip) {
    var g = GpuSelection.FromCfg(cfg);
    Assert.Equal(useCpu, g.UseCpu);
    Assert.Equal(useAll, g.UseAll);
    Assert.Equal(roundTrip, g.CfgString());
  }

  [Fact] public void FromCfg_IndexList_RoundTripsSorted() {
    var g = GpuSelection.FromCfg("1,0");
    Assert.Equal(new[] { 1, 0 }, g.Indices);   // 解析保序
    Assert.Equal("0,1", g.CfgString());        // 序列化排序
    Assert.Equal("GPU0+GPU1", g.ShortLabel());
  }

  [Fact] public void FromCfg_UnrecognizedText_FallsBackToAll() {
    Assert.True(GpuSelection.FromCfg("zzz").UseAll);
    Assert.True(GpuSelection.FromCfg(null!).UseAll);
  }

  [Fact] public void EffectiveGpus_FiltersOutOfRangeIndices() {
    Assert.Equal(new[] { 0, 1 }, LaunchArgs.EffectiveGpus(new GpuSelection { UseAll = true }, 2));
    Assert.Empty(LaunchArgs.EffectiveGpus(new GpuSelection { UseCpu = true }, 2));
    Assert.Equal(new[] { 0 }, LaunchArgs.EffectiveGpus(GpuSelection.FromCfg("0,5"), 2));
  }

  [Fact] public void ShortLabel_MarksAllAndCpu() {
    Assert.Equal("全部", GpuSelection.FromCfg("all").ShortLabel());
    Assert.Equal("CPU",  GpuSelection.FromCfg("cpu").ShortLabel());
    Assert.Equal("GPU1", GpuSelection.FromCfg("1").ShortLabel());
  }

  // 越界索引全部被过滤 ⇒ 退化成 CPU（-ngl 0），不会拿一个不存在的 GPU 去起服务
  [Fact] public void AllGpuIndicesOutOfRange_DegradesToCpu() {
    var r = LaunchArgs.Build(Spec(), GpuSelection.FromCfg("7,8"), 8192, 0, 0, 0, 0, 50, 2);
    Assert.Equal("0", Val(r.args, "-ngl"));
  }

  // --- 菜单选项表（改这里等于改托盘菜单，改动需有意识）---

  [Fact] public void ContextAndCacheRamOptionTables_AreTheDocumentedOnes() {
    Assert.Equal(new[] { 8192, 16384, 32768, 65536, 131072, 196608, 262144 }, LaunchArgs.CtxOptions);
    Assert.Equal(new[] { 512, 1024, 2048, 4096, -1, 0 }, LaunchArgs.CacheRamOptions);
  }

  // --- Describe：菜单/日志里给人看的那一行 ---

  [Fact] public void Describe_ListsPciBus_AndMarksTensorParallel() {
    var gpus = new List<Gpu> {
      new Gpu { Index = 0, PciBus = "01:00.0" },
      new Gpu { Index = 1, PciBus = "02:00.0" },
    };
    Assert.Equal("GPU0(01:00.0) + GPU1(02:00.0)（张量并行）", GpuSelection.FromCfg("all").Describe(gpus));
    Assert.Equal("GPU1(02:00.0)", GpuSelection.FromCfg("1").Describe(gpus));
    Assert.Equal("CPU",          GpuSelection.FromCfg("cpu").Describe(gpus));
  }

  // GPU 列表里查不到该索引时退化成 "GPUn"（不抛异常）
  [Fact] public void Describe_FallsBackToIndexWhenGpuMissingFromList() {
    var odd = new List<Gpu> { new Gpu { Index = 5 }, new Gpu { Index = 6 } };
    Assert.Equal("GPU0 + GPU1（张量并行）", GpuSelection.FromCfg("all").Describe(odd));
  }
}
