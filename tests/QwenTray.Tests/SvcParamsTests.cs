namespace QwenTray.Tests;

// 参数解析（2026-09-26）：「本模型的参数」× 「托盘全局默认」⇒ **生效值**。
//
// 为什么值得单测（这类缺陷的典型症状）：
//   菜单里显示 192K、实际按 128K 启动 —— 启动(Start) 与菜单渲染(RefreshCurGroup) 各算一遍，
//   两边只要有一个取整/回落的写法不同，用户看到的就不是实际跑的（执行鸿沟）。
//   解析被抽成纯函数后，四处消费点（启动 / 状态行 / 运行时配置行 / 启动日志）天然同源；
//   这里把合成规则钉死，任何一侧回退都会被 dotnet test 抓住（不必靠真跑一次模型）。
//
// 钉的是三条语义：
//   ① **本模型覆盖优先**，缺失项逐项回落全局（不是整组覆盖 —— 只改一项不能牵连别的模型）
//   ② 覆盖项**逐个计数**进 Overrides（菜单要显示"本模型覆盖了几项"）
//   ③ GPU 选择是**整块**覆盖（字符串非空才算覆盖，空串 = 不表态）
public class SvcParamsTests {

  static ServiceSpec Spec() => new ServiceSpec { Name = "Qwen3.8-27B (thinking)", Port = 8082 };

  // ---------- ① 覆盖优先 / 逐项回落 ----------

  [Fact]
  public void 全未设置时逐项回落全局默认() {
    var sp = Spec();
    var p = SvcParams.Resolve(sp, 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    Assert.Equal(196608, p.Ctx);
    Assert.Equal(1, p.KvMode);
    Assert.Equal(2048, p.CacheRam);
    Assert.Equal(1, p.SplitMode);
    Assert.Equal(50, p.TsGpu1);
    Assert.Equal(0, p.MtpLevel);
    Assert.Equal(1, p.ParamMode);
    Assert.True(p.BindAll);
    Assert.Equal(0, p.Overrides);      // 一项都没覆盖 ⇒ 菜单应显示"未覆盖（用全局默认）"
  }

  [Fact]
  public void 本模型覆盖优先且只影响被写的那一项() {
    var sp = Spec();
    sp.Ctx = 65536;                     // 只改上下文
    var g = SvcParams.Resolve(sp, 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    Assert.Equal(65536, g.Ctx);
    Assert.Equal(1, g.KvMode);          // 其余仍回落全局（不能被本模型"顺手"改掉）
    Assert.Equal(2048, g.CacheRam);
    Assert.Equal(1, g.Overrides);
  }

  [Fact]
  public void 九项全覆盖时全部取本模型值() {
    var sp = Spec();
    sp.Ctx = 8192; sp.KvMode = 2; sp.CacheRam = 0; sp.SplitMode = 0; sp.TsGpu1 = 90;
    sp.MtpLevel = 5; sp.ParamMode = 2; sp.BindAll = false; sp.GpuSel = "0";
    var p = SvcParams.Resolve(sp, 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    Assert.Equal(9, p.Overrides);
    Assert.Equal(8192, p.Ctx); Assert.Equal(2, p.KvMode); Assert.Equal(0, p.CacheRam);
    Assert.Equal(0, p.SplitMode); Assert.Equal(90, p.TsGpu1); Assert.Equal(5, p.MtpLevel);
    Assert.Equal(2, p.ParamMode); Assert.False(p.BindAll); Assert.Equal("0", p.GpuCfg);
  }

  // ⚠️ 反例：cacheRam=0（禁用）与 ctx 这类"写了 0 与没写"必须可分 —— 若字段是 int 而非 int?，
  //   用户把缓存内存调到"禁用"会静默退回默认值（看起来像没生效）。
  [Fact]
  public void 零值合法不能被当成未设置() {
    var sp = Spec(); sp.CacheRam = 0;
    var p = SvcParams.Resolve(sp, 2048, 1, 2048, 1, 50, 0, 1, true, "all");
    Assert.Equal(0, p.CacheRam);
    Assert.Equal(1, p.Overrides);       // 若判成"未设置"这里会是 0
  }

  [Fact]
  public void Spec全空时等价于全部回落() {
    var p = SvcParams.Resolve(new ServiceSpec(), 131072, 0, 512, 0, 10, 3, 0, false, "cpu");
    Assert.Equal(131072, p.Ctx); Assert.Equal("cpu", p.GpuCfg); Assert.Equal(0, p.Overrides);
  }

  // ---------- ③ GPU 选择：非空串才算覆盖 ----------

  [Theory]
  [InlineData(null, "all", "all")]
  [InlineData("", "0,1", "0,1")]        // 空 = 不表态
  [InlineData("0,1", "cpu", "0,1")]     // 本模型写了就用本模型的
  [InlineData("cpu", "all", "cpu")]
  public void GpuOrDefault_本模型覆盖优先空串回落(string gpuSel, string global, string want) {
    var sp = Spec(); sp.GpuSel = gpuSel ?? "";
    var p = SvcParams.Resolve(sp, 196608, 1, 2048, 1, 50, 0, 1, true, global);
    Assert.Equal(want, p.GpuOrDefault(new GpuSelection { UseAll = true }).CfgString());
  }

  [Fact]
  public void 孤立GPU覆盖串仍能被解析() {   // FromCfg 对 "2"（本机双卡之外的单卡）不该炸
    var g = GpuSelection.FromCfg("1");
    Assert.Equal("1", g.CfgString());
    Assert.True(GpuSelection.FromCfg("").UseAll);
  }

  // ---------- ② 覆盖计数与渲染同源 ----------

  [Fact]
  public void Overrides与Describe讲的是同一份生效值() {
    var sp = Spec();
    sp.KvMode = 2; sp.MtpLevel = 4; sp.GpuSel = "1";
    var p = SvcParams.Resolve(sp, 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    Assert.Equal(3, p.Overrides);
    string d = p.Describe();
    Assert.Contains("16bit", d);        // KV=2
    Assert.Contains("MTP4", d);
  }

  // Describe 的 ctx 换算单独钉：192K 不能显示成 256K（÷1024 而非 ÷1000）
  [Fact]
  public void DescribeCtx按1024折算() {
    var p = SvcParams.Resolve(Spec(), 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    Assert.Contains("192K", p.Describe());
  }

  // ---------- DescribeOverrides（2026-09-26：菜单「运行时配置」行只打覆盖项）----------
  // 用户圈注：未覆盖项 = 参数面板正在显示的全局默认，全量 Describe() 是纯重复。
  // 钉两条：① 没覆盖 ⇒ 空串（调用方显示"全部跟随全局默认"） ② 覆盖谁打谁，且**不含**未覆盖项的值。

  [Fact]
  public void 全未覆盖时DescribeOverrides返回空串() {
    var p = SvcParams.Resolve(Spec(), 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    Assert.Equal("", p.DescribeOverrides(new GpuSelection { UseAll = true }));
  }

  [Fact]
  public void 只打覆盖项不打未覆盖项() {
    var sp = Spec();
    sp.KvMode = 2; sp.Ctx = 65536;          // 只覆盖这两项
    var p = SvcParams.Resolve(sp, 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    string d = p.DescribeOverrides(new GpuSelection { UseAll = true });
    Assert.Contains("64K", d);
    Assert.Contains("16bit", d);
    Assert.DoesNotContain("192K", d);       // 全局 ctx 196608 不得出现（面板已有）
    Assert.DoesNotContain("8bit", d);       // 全局 KV 不得出现
    Assert.DoesNotContain("MTP", d);        // 未覆盖 MTP
    Assert.DoesNotContain("缓存内存", d);   // 未覆盖 cacheRam
  }

  [Fact]
  public void 覆盖计数与Overrides行描述同源() {   // 打出来的项数 == Overrides
    var sp = Spec();
    sp.MtpLevel = 4; sp.BindAll = false; sp.GpuSel = "1"; sp.ParamMode = 2;
    var p = SvcParams.Resolve(sp, 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    Assert.Equal(4, p.Overrides);
    string d = p.DescribeOverrides(new GpuSelection { UseAll = true });
    Assert.Contains("MTP4", d);
    Assert.Contains("127.0.0.1", d);
    Assert.Contains("GPU1", d);
    Assert.Contains("Instruct", d);
  }

  [Fact]
  public void 按层切分时不打GPU1占比() {   // ts 只在张量并行下生效，按层时打了误导
    var sp = Spec();
    sp.SplitMode = 0; sp.TsGpu1 = 90;
    var p = SvcParams.Resolve(sp, 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    string d = p.DescribeOverrides(new GpuSelection { UseAll = true });
    Assert.Contains("按层", d);
    Assert.DoesNotContain("90%", d);
  }

  // ---------- DiffVs（2026-09-26：复制生成模型配置后，「非 null=覆盖」失效，改值级差异）----------
  // 基线：ctx=196608 KV=1 ram=2048 split=1 ts=50 mtp=0 pm=1 bind=true gpu="all"

  [Fact]
  public void 与基线完全一致时DiffVs为空() {
    var p = SvcParams.Resolve(Spec(), 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    var (n, txt) = p.DiffVs(196608, 1, 2048, 1, 50, 0, 1, true, new GpuSelection { UseAll = true });
    Assert.Equal(0, n);
    Assert.Equal("", txt);
  }

  [Fact]
  public void DiffVs只列与基线不同的项() {
    var sp = Spec(); sp.Ctx = 32768; sp.BindAll = false;
    var p = SvcParams.Resolve(sp, 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    var (n, txt) = p.DiffVs(196608, 1, 2048, 1, 50, 0, 1, true, new GpuSelection { UseAll = true });
    Assert.Equal(2, n);
    Assert.Contains("32K", txt);
    Assert.Contains("127.0.0.1", txt);
    Assert.DoesNotContain("192K", txt);    // 与基线相同的项不得出现（面板已有，重复=噪音）
    Assert.DoesNotContain("缓存内存", txt);
  }

  [Fact]
  public void DiffVs的GPU与切分按值比较() {
    var sp = Spec(); sp.GpuSel = "0"; sp.SplitMode = 1; sp.TsGpu1 = 70;
    var p = SvcParams.Resolve(sp, 196608, 1, 2048, 1, 50, 0, 1, true, "all");
    var (n, txt) = p.DiffVs(196608, 1, 2048, 1, 50, 0, 1, true, new GpuSelection { UseAll = true });
    Assert.Contains("GPU0", txt);
    Assert.Contains("张量并行 70%", txt);
    Assert.True(n >= 2);
  }

  // ---------- 持久化往返（写进 config.json ⇒ 重启托盘后仍在）----------

  [Fact]
  public void 写入再读回后Resolve结果不变() {
    var sp = Spec();
    sp.Ctx = 65536; sp.MtpLevel = 3; sp.KvMode = 1; sp.BindAll = false; sp.GpuSel = "0,1";
    var a = SvcParams.Resolve(sp, 196608, 0, 4096, 1, 50, 0, 0, true, "all");
    var back = ServiceSpec.From(ToConfig(sp));
    var b = SvcParams.Resolve(back, 196608, 0, 4096, 1, 50, 0, 0, true, "all");
    Assert.Equal(a.Ctx, b.Ctx); Assert.Equal(a.MtpLevel, b.MtpLevel);
    Assert.Equal(a.KvMode, b.KvMode); Assert.Equal(a.BindAll, b.BindAll);
    Assert.Equal(a.GpuCfg, b.GpuCfg); Assert.Equal(a.Overrides, b.Overrides);
  }

  // ServiceSpec.From 必须把新增的 9 项一起搬过去 —— 漏搬 = 重启托盘后覆盖静默丢失
  static ServiceConfig ToConfig(ServiceSpec sp) => new ServiceConfig {
    Name = sp.Name, Port = sp.Port,
    Ctx = sp.Ctx, KvMode = sp.KvMode, CacheRam = sp.CacheRam, SplitMode = sp.SplitMode,
    TsGpu1 = sp.TsGpu1, MtpLevel = sp.MtpLevel, ParamMode = sp.ParamMode,
    BindAll = sp.BindAll, GpuSel = sp.GpuSel,
  };
}
