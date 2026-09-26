namespace QwenTray.Tests;

// S5-3（2026-09-12）：菜单**文本合成** —— 从 TrayApp 搬进 Core 后钉住。
//
// 为什么值得钉：这些字符串是用户唯一能看到的"当前参数"出口（一级菜单标题 / 二级「运行时配置」6 行 /
// 悬浮 ToolTip），但它们原先只是 TrayApp 的私有 static，只有 `--selftest-svcmenu` 能间接枚举到，
// 而那个探针**不在** dotnet test 链上。
//
// 挑的是"容易悄悄错、错了又不报错"的地方：
//   · 缓存内存档位 1024 整数倍才折 GB（写错就显示 2048M 而不是 2GB）
//   · 文件名截断的宽度必须**恰好等于** max（差 1 个字符就会让子菜单宽度随文件名抖动）
//   · MTP / 参数组 / KV 的越界回落
//   · 「运行时配置」恒 6 行 —— 菜单侧按这个数建行池，多出的行永远不显示
public class SvcLinesTests {

  static SvcView View() => new SvcView {
    Model = @"C:\models\Qwen3.8-27B-Q4_K_M.gguf",
    Port = 8081, Batch = 2048, Ubatch = 512, Provider = "",
    Gpu = new GpuSelection { UseAll = true }, GpuCount = 2,
    SplitMode = 1, TsGpu1 = 50, KvMode = 1, CtxVal = 196608, CacheRam = 2048,
    MtpLevel = 0, ParamMode = 1, BindAll = true,
  };
  static SvcView CpuView() { var v = View(); v.Gpu = new GpuSelection { UseCpu = true }; return v; }

  // ---------- 档位标签 ----------

  // KV：0=默认 1=q8_0 其余（含越界）=f16 —— 越界回落到"最保真"而不是静默截断
  [Theory]
  [InlineData(0, "默认")]
  [InlineData(1, "8bit q8_0")]
  [InlineData(2, "16bit f16")]
  [InlineData(99, "16bit f16")]
  public void KvLabel_RoundTrip(int m, string expect) { Assert.Equal(expect, SvcLines.KvLabel(m)); }

  // 缓存内存：0=禁用 / 负数=无限制 / ≥1024 且是 1024 的整数倍才折 GB / 其余按 MiB 原样
  [Theory]
  [InlineData(0, "禁用")]
  [InlineData(-1, "无限制")]
  [InlineData(-2, "无限制")]
  [InlineData(512, "512M")]
  [InlineData(1000, "1000M")]      // 不是 1024 的整数倍 → 不能折成"0GB"
  [InlineData(1024, "1GB")]
  [InlineData(2048, "2GB")]
  [InlineData(4096, "4GB")]
  public void CacheRamLabel_IsExhaustive(int mb, string expect) { Assert.Equal(expect, SvcLines.CacheRamLabel(mb)); }

  [Theory]
  [InlineData(0, "无")]
  [InlineData(1, "MTP")]
  [InlineData(2, "MTP2")]
  [InlineData(4, "MTP4")]
  public void MtpLabel_RoundTrip(int m, string expect) { Assert.Equal(expect, SvcLines.MtpLabel(m)); }

  [Theory]
  [InlineData(0, "通用思考")]
  [InlineData(1, "编码思考")]
  [InlineData(2, "Instruct")]
  [InlineData(9, "Instruct")]
  public void ParamLabel_RoundTrip(int m, string expect) { Assert.Equal(expect, SvcLines.ParamLabel(m)); }

  // ---------- 文件名 / 截断 / 时长 ----------

  [Fact] public void FileName_StripsDirectory_AndToleratesNull() {
    Assert.Equal("m.gguf", SvcLines.FileName(@"C:\models\m.gguf"));
    Assert.Equal("", SvcLines.FileName(""));
    Assert.Equal("", SvcLines.FileName(null));
  }

  [Fact] public void Mid_ShortOrNull_IsReturnedVerbatim() {
    Assert.Equal("", SvcLines.Mid(null, 10));
    Assert.Equal("", SvcLines.Mid("", 10));
    Assert.Equal("abc", SvcLines.Mid("abc", 10));
    Assert.Equal("abc", SvcLines.Mid("abc", 3));   // 恰好等于宽度 → 不动它
  }

  [Fact] public void Mid_TruncatesKeepingHeadAndTail_ExactWidth() {
    string s = new string('a', 60) + "Q4_K_M.gguf";   // 71 字符
    string got = SvcLines.Mid(s, 42);
    Assert.Equal(42, got.Length);                    // 宽度必须**恰好**等于 max
    Assert.Contains("…", got);
    Assert.StartsWith(new string('a', 27), got);     // 头 = 保留字符的 2/3
    Assert.EndsWith("Q4_K_M.gguf", got);             // 尾 = 含量化档位，优先保留
  }

  // 宽度契约（防止菜单随模型名抖动）：任何 max 都必须产出**恰好 max** 个字符
  [Fact] public void Mid_AlwaysProducesExactlyMaxChars() {
    string s = "Qwen3.8-27B-Q4_K_M-abcdefghijklmnopqrstuvwxyz.gguf";
    for (int max = 1; max <= 40; max++) {
      string got = SvcLines.Mid(s, max);
      if (max >= s.Length) { Assert.Equal(s, got); continue; }
      Assert.Equal(max, got.Length);
      Assert.Contains("…", got);
    }
  }

  [Theory]
  [InlineData(0, "0s")]
  [InlineData(-5, "0s")]         // 负时长（时钟回拨/未探测）不得输出负数
  [InlineData(59, "59s")]
  [InlineData(60, "1m0s")]
  [InlineData(90, "1m30s")]
  [InlineData(3600, "1h0m")]
  [InlineData(3661, "1h1m")]
  [InlineData(86400, "1d")]      // 整 1 天且 0 小时 → 不带 "0h"
  [InlineData(90000, "1d1h")]
  public void Dur_Boundaries(int seconds, string expect) {
    Assert.Equal(expect, SvcLines.Dur(TimeSpan.FromSeconds(seconds)));
  }

  // ---------- 「运行时配置」6 行 ----------

  // 契约：恒 6 行。菜单按这个数建行池（BuildSvcMenu 里循环 6 次），少了会留下"    —"占位，
  // 多了则永远显示不出来 —— 两边必须一致。
  [Fact] public void CfgLines_AlwaysSixLines_WhateverTheState() {
    foreach (var v in new[] { View(), CpuView() }) {
      Assert.Equal(6, SvcLines.CfgLines(v).Length);
      v.Running = true; v.RunCtx = 262144; v.RunVision = 1;
      Assert.Equal(6, SvcLines.CfgLines(v).Length);
    }
  }

  [Fact] public void CfgLines_CpuMode_NoGpuAndFlashAttnOff() {
    var l = SvcLines.CfgLines(CpuView());
    Assert.Contains("CPU（-ngl 0，无 GPU 加速）", l[1]);
    Assert.Contains("flash-attn = 关", l[3]);
  }

  [Fact] public void CfgLines_SingleGpu_UsesSplitModeNone() {
    var v = View(); v.Gpu = new GpuSelection { UseAll = true }; v.GpuCount = 1;
    var l = SvcLines.CfgLines(v);
    Assert.Contains("GPU0 · -ngl 99 · --split-mode none", l[1]);
    Assert.Contains("flash-attn = on", l[3]);
  }

  // 多卡：按层 layer 不带 -ts；张量并行 tensor 必须带 "-ts <GPU0%>,<GPU1%>"（与 TsGpu1 之和恒 100）
  //
  // ⚠️ 这里钉的是**现状** "GPU0+1"（不是 "GPU0+GPU1"）：eff 是 List<int>，Join 出来就是 "0+1"。
  //    它与一级菜单 GPU 项的 GpuSelection.ShortLabel()（"GPU0+GPU1"）**不一致**，是既有缺陷。
  //    S5 是纯搬运 ⇒ 如实保留；要"修"它必须把这条断言一起改（并走托盘人工回归），
  //    这样就有一次**显式决定**，而不是某天被顺手改掉。
  [Fact] public void CfgLines_MultiGpu_LayerVsTensor() {
    var v = View(); v.SplitMode = 0;
    Assert.Contains("GPU0+1 · -ngl 99 · --split-mode layer", SvcLines.CfgLines(v)[1]);
    v.SplitMode = 1; v.TsGpu1 = 30;
    Assert.Contains("GPU0+1 · -ngl 99 · --split-mode tensor -ts 70,30", SvcLines.CfgLines(v)[1]);
  }

  [Fact] public void CfgLines_ModelLine_ShowsMmprojStateAndTruncatesLongName() {
    var v = View();
    var noMm = SvcLines.CfgLines(v)[0];
    Assert.Contains("Qwen3.8-27B-Q4_K_M.gguf", noMm);
    Assert.Contains("无 mmproj", noMm);

    v.UseMmproj = true; v.Mmproj = @"C:\models\mmproj-F16.gguf";
    Assert.Contains("mmproj = mmproj-F16.gguf", SvcLines.CfgLines(v)[0]);

    v.Model = @"C:\models\" + new string('x', 80) + ".gguf";
    string line = SvcLines.CfgLines(v)[0];
    Assert.Contains("…", line);                                  // 名字被截断
    Assert.EndsWith(" · mmproj = mmproj-F16.gguf", line);        // 截断后仍与 mmproj 段拼得上（不吞掉后半行）
  }

  [Fact] public void CfgLines_ParamLine_And_ListenLine() {
    var v = View();
    Assert.Contains("KV = 8bit q8_0", SvcLines.CfgLines(v)[2]);
    Assert.Contains("批 = 2048/512", SvcLines.CfgLines(v)[2]);
    Assert.Contains("缓存内存 = 2GB", SvcLines.CfgLines(v)[2]);
    Assert.Contains("上下文 = 192K", SvcLines.CfgLines(v)[2]);
    Assert.Contains("MTP = 无 · 参数组 = 编码思考", SvcLines.CfgLines(v)[3]);
    Assert.Contains("0.0.0.0:8081", SvcLines.CfgLines(v)[4]);
    v.BindAll = false;
    Assert.Contains("127.0.0.1:8081", SvcLines.CfgLines(v)[4]);
  }

  // 第 6 行：未运行 = 一句承诺；运行中用服务端实测；实测为 0（未探测）时回落显示菜单值并标注
  [Fact] public void CfgLines_ProbeLine_ReflectsRunState() {
    var v = View();
    Assert.Equal("    实测 = （未运行 → 启动后自动探测 /props）", SvcLines.CfgLines(v)[5]);

    v.Running = true; v.RunCtx = 0; v.RunVision = -1;
    Assert.Equal("    实测 = ctx 192K(未探测) · 视觉 未探测", SvcLines.CfgLines(v)[5]);

    v.RunCtx = 262144; v.RunVision = 1;
    Assert.Equal("    实测 = ctx 256K · 视觉 支持", SvcLines.CfgLines(v)[5]);   // 服务端 256K ≠ 菜单 192K：以实测为准

    v.RunVision = 0;
    Assert.Contains("视觉 不支持", SvcLines.CfgLines(v)[5]);
  }

  // ---------- 「运行时配置」6 行 · 自定义参数（2026-09-20）----------
  //
  // 自定义参数的服务**没有托盘参数可言**：ctx / KV / 切分 / MTP / 缓存内存 那五行描述的是内置模板，
  // 而这条服务根本没走模板 ⇒ 继续渲染等于把不存在的配置当事实报出来（用户会照着它去调参）。
  // 契约不变：恒 6 行。

  static SvcView CustomView() {
    var v = View();
    v.ArgsCustom = true;
    v.Exe = "llama-kvmem-server.exe";
    v.CustomArgs = "-c 262144 --kvmem-budget 36864 --kv-dtype q8_0";
    v.EnvLine = "CUDA_VISIBLE_DEVICES=0 | KVMEM_VISION_DEVICE=gpu";
    return v;
  }

  [Fact] public void CfgLines_CustomArgs_StillSixLines() {
    foreach (var v in new[] { CustomView(), View() }) {
      Assert.Equal(6, SvcLines.CfgLines(v).Length);
      v.Running = true; v.RunCtx = 262144; v.RunVision = 1;
      Assert.Equal(6, SvcLines.CfgLines(v).Length);
    }
  }

  [Fact] public void CfgLines_CustomArgs_ReportsExeAndArgs_NotTrayParams() {
    var l = SvcLines.CfgLines(CustomView());
    Assert.Contains("Qwen3.8-27B-Q4_K_M.gguf", l[0]);          // 模型行仍然成立，照旧
    Assert.Contains("exe = llama-kvmem-server.exe", l[1]);
    Assert.Contains("自定义参数 6 项", l[1]);
    Assert.Contains("--kvmem-budget 36864", l[2]);
    Assert.Contains("已显示完全", l[3]);                        // 46 字符装得下 ⇒ 不出现"参数(续)"
    Assert.Contains("环境 = CUDA_VISIBLE_DEVICES=0", l[4]);
    // 托盘参数一律不得出现（它们对这条服务不成立）
    foreach (var line in l)
      foreach (var bad in new[] { "-ngl", "flash-attn", "--split-mode", "KV = ", "缓存内存 = ", "MTP = ", "上下文 = " })
        Assert.DoesNotContain(bad, line);
  }

  [Fact] public void CfgLines_CustomArgs_LongString_SplitsOverTwoLines() {
    var v = CustomView(); v.CustomArgs = new string('x', 200);
    var l = SvcLines.CfgLines(v);
    Assert.Contains("…", l[2]);                                 // 第一段被截断
    Assert.StartsWith("    参数(续) = ", l[3]);                  // 余下接着排，不是丢掉
  }

  // 自定义参数下 ctx 不做"托盘参数回退"：-c 藏在参数串里，托盘并不知道它是多少，
  // 拿菜单里的 ctxVal 冒充实测值正是这条分支要消灭的那个谎。
  [Fact] public void CfgLines_CustomArgs_NoCtxFallback_WhenProbeEmpty() {
    var v = CustomView(); v.Running = true; v.RunCtx = 0; v.RunVision = -1;
    Assert.Contains("ctx 未探测", SvcLines.CfgLines(v)[5]);
    Assert.DoesNotContain("192K", SvcLines.CfgLines(v)[5]);
  }

  // ---------- 状态悬浮（StatusTip） ----------

  [Fact] public void StatusTip_NotRunning_HasNoPidNoRam() {
    string t = SvcLines.StatusTip(View());
    Assert.Equal("状态: 未运行", t.Split('\n')[0].TrimEnd('\r'));
    Assert.Contains("模型: " + @"C:\models\Qwen3.8-27B-Q4_K_M.gguf", t);
    Assert.Contains("端口: 8081   provider: llama-local", t);   // 空 provider → 显示默认
    Assert.DoesNotContain("pid:", t);
    Assert.DoesNotContain("内存:", t);                          // 未运行不报内存
    Assert.DoesNotContain("服务端实测", t);
    Assert.Equal(t, t.TrimEnd());                               // 末尾已 TrimEnd（否则 ToolTip 会多出一行空白）
  }

  // 自定义参数的服务：菜单 6 行只放得下截断版（CustomCfgLines），完整串落在这里 ——
  // 这是"点不到菜单细节时的唯一出口"，所以**不截断**。默认服务则一行都不多。
  [Fact] public void StatusTip_CustomArgs_AppendsExeArgsEnv_Unclipped() {
    var v = CustomView(); v.CustomArgs = new string('y', 300);
    string t = SvcLines.StatusTip(v);
    Assert.Contains("exe: llama-kvmem-server.exe", t);
    Assert.Contains(new string('y', 300), t);                   // 未被截断
    Assert.Contains("环境: CUDA_VISIBLE_DEVICES=0", t);
    Assert.DoesNotContain("exe: ", SvcLines.StatusTip(View())); // 默认服务不带这三行
  }

  [Fact] public void StatusTip_Busy_ExplainsPortReclaim() {
    var v = View(); v.PortBusy = true;
    Assert.Contains("运行中(未托管，可「停止模型」按端口回收)", SvcLines.StatusTip(v));
  }

  // starting 压过 running：进程已拉起但端口未就绪时，用户看到的是"启动中"
  [Fact] public void StatusTip_Starting_WinsOverRunning() {
    var v = View(); v.Starting = true; v.Running = true; v.Pid = 10; v.StartedAt = DateTime.Now;
    Assert.StartsWith("状态: 启动中", SvcLines.StatusTip(v));
  }

  [Fact] public void StatusTip_RunningWithHandle_PrintsPidStartedRam() {
    var v = View(); v.Running = true; v.Pid = 1234; v.StartedAt = DateTime.Now.AddMinutes(-3); v.RamGb = "12.34G";
    string t = SvcLines.StatusTip(v);
    Assert.Contains("状态: 运行中", t);
    Assert.Contains("pid: 1234", t);
    Assert.Contains("已运行: 3m", t);
    Assert.Contains("内存: 12.34G", t);
  }

  // 进程句柄丢失（外部启动 / 刚退出）：pid 行整行不打印，但内存行照打 —— 原实现在同一 try 里，
  // 这里把"整行都不可信"这个语义钉住，避免有人把它拆成"有 pid 就打、没启动时间就打空"
  [Fact] public void StatusTip_RunningWithoutHandle_OmitsPidLineButKeepsRam() {
    var v = View(); v.Running = true; v.Pid = 0; v.StartedAt = null; v.RamGb = "9.00G";
    string t = SvcLines.StatusTip(v);
    Assert.DoesNotContain("pid:", t);
    Assert.DoesNotContain("已运行", t);
    Assert.Contains("内存: 9.00G", t);
  }

  [Fact] public void StatusTip_MmprojLineOnlyWhenEnabled() {
    var v = View();
    Assert.DoesNotContain("mmproj:", SvcLines.StatusTip(v));
    v.UseMmproj = true; v.Mmproj = @"C:\models\mmproj-F16.gguf";
    Assert.Contains("mmproj: " + @"C:\models\mmproj-F16.gguf", SvcLines.StatusTip(v));   // 悬浮里给完整路径
  }

  [Fact] public void StatusTip_ProviderOverride_AndServerProbeLine() {
    var v = View(); v.Provider = "llama-local-thinking";
    Assert.Contains("provider: llama-local-thinking", SvcLines.StatusTip(v));

    // 未运行 → 不出现服务端实测；运行且探测到 → 出现
    v.Running = true; v.RunCtx = 0;
    Assert.DoesNotContain("服务端实测", SvcLines.StatusTip(v));
    v.RunCtx = 196608; v.RunVision = 1;
    Assert.Contains("服务端实测: ctx 192K · 视觉 支持", SvcLines.StatusTip(v));
  }

  // 一级项快捷启动的提示行（2026-09-12）：模型一级项的 ToolTip 就是本函数的输出（RefreshSvcMenu 每次都重写）
  // ⇒ 这条提示必须**任何状态**下都在（未运行/启动中/运行中/被外部占用），否则用户发现不了这个交互。
  [Fact] public void StatusTip_AlwaysCarriesQuickStartHint() {
    Assert.Contains("直接点本一级项 = 快捷启动", SvcLines.StatusTip(View()));
    var starting = View(); starting.Starting = true;
    Assert.Contains("直接点本一级项 = 快捷启动", SvcLines.StatusTip(starting));
    var running = View(); running.Running = true;
    Assert.Contains("直接点本一级项 = 快捷启动", SvcLines.StatusTip(running));
    var busy = View(); busy.PortBusy = true;
    Assert.Contains("直接点本一级项 = 快捷启动", SvcLines.StatusTip(busy));
  }

  // ---------- 通知区 tooltip：行合成 + 127 字符钳位（2026-09-13） ----------

  // 上限就是 Win32 szTip 的 127：超了 .NET 8 会抛 ArgumentException，而调用点在 try/catch 里 ⇒ 静默不刷新。
  [Fact] public void Tip_ClampNeverExceeds127() {
    var many = new List<string>();
    for (int i = 0; i < 20; i++) many.Add("Qwen3.8-27B(" + (8080 + i) + "):运行 RAM 18.34G KV 1234/262144 缓存 12M/2048M");
    var s = SvcLines.ClampTip(many);
    Assert.True(s.Length <= SvcLines.TipMaxChars, "实得 " + s.Length + " 字符: " + s);
  }

  // 装得下时必须**一字不改**（钳位不能顺手改了正常情况下的观感）
  [Fact] public void Tip_UnderLimit_IsUntouched() {
    var lines = new List<string> { "Qwen3.8-27B(8082):运行 RAM 18.34G KV 1234/262144 缓存 12M/2048M", "GPU0: 20.3/22.0G 88% 62°C", "CPU: 12% 45°C 内存: 63%" };
    Assert.Equal(string.Join("\n", lines), SvcLines.ClampTip(lines));
  }

  // 截断必须留省略号（让用户知道"后面还有，只是没显示"），而不是无声砍掉尾部
  [Fact] public void Tip_Overflow_TruncatesTailWithEllipsis() {
    var lines = new List<string> { new string('A', 100), new string('B', 100) };
    var s = SvcLines.ClampTip(lines);
    Assert.True(s.Length <= SvcLines.TipMaxChars, "实得 " + s.Length);
    Assert.StartsWith(new string('A', 100), s);          // 第一行完整保留
    Assert.EndsWith("…", s);                              // 溢出有明确标记
    int b = s.Count(c => c == 'B');
    Assert.True(b > 0 && b < 100, "末行应被**截断**而不是整行丢弃，实得 " + b + " 个 B");
  }

  // 极窄上限也不许崩、不许返回超长串（兜底分支）
  [Fact] public void Tip_TinyLimit_IsSafe() {
    var s = SvcLines.ClampTip(new List<string> { "abcdef" }, 3);
    Assert.True(s.Length <= 3, s);
  }

  [Fact] public void Tip_EmptyInput_IsEmpty() {
    Assert.Equal("", SvcLines.ClampTip(new List<string>()));
  }

  // 单模型行：KV 总量为 0（未探到 /props）⇒ **整段 KV 不出现**，不能显示 "KV 0/0"
  [Fact] public void SvcTipLine_HidesKvWhenTotalUnknown() {
    var s = SvcLines.SvcTipLine("Qwen3.8-27B", 8082, "运行", "18.34G", 0, 0, 0, 0);
    Assert.Equal("Qwen3.8-27B(8082):运行 RAM 18.34G 缓存 无", s);
  }

  [Fact] public void SvcTipLine_ShowsBothMetricsWhenProbed() {
    var s = SvcLines.SvcTipLine("Qwen3.8-27B", 8082, "运行", "18.34G", 1234, 262144, 12, 2048);
    Assert.Equal("Qwen3.8-27B(8082):运行 RAM 18.34G KV 1234/262144 缓存 12M/2048M", s);
  }

  // 缓存上限为 0（llama 未以 -lv 4 启动 ⇒ 拿不到那行 trace）⇒ 显示「无」。
  // 🔴 关键纪律：**绝不许**退回 cfg 里的 cacheRam 设定值冒充"实测用量"（那是编数据）。
  [Fact] public void SvcTipLine_CacheUnavailable_SaysNone_NotConfiguredValue() {
    var s = SvcLines.SvcTipLine("Qwen3.8-27B", 8082, "运行", "18.34G", 100, 262144, 0, 0);
    Assert.Contains("缓存 无", s);
    Assert.DoesNotContain("2048", s);
  }
}
