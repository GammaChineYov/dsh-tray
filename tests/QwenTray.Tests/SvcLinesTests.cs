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
}
