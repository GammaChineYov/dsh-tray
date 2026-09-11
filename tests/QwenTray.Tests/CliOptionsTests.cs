namespace QwenTray.Tests;

// S4（2026-09-12）：CliOptions 的穷举断言。
//
// 这组测试存在的理由不是"覆盖率"，而是：Main 里这段解析决定**进程要不要走到 Application.Run**。
// 历史上漏写一个探针分支的代价 = 起出一个没有单实例锁、带通知区图标、且永不退出的「幽灵托盘」，
// 只能手工 taskkill。所以最关键的用例是 EveryDiagnosticFlag_IsLockFree ——
// 以后新增探针却忘了登记进 IsDiagnostic，它会当场红，而不是等某天在通知区里多出一个幽灵。
public class CliOptionsTests {

  // 全部诊断/自检标志。⚠️ 新增探针必须同时改这里 **和** CliOptions.IsDiagnostic。
  public static TheoryData<string> DiagnosticFlags() => new() {
    "--selftest-logwin", "--selftest-menushow", "--selftest-plugins", "--selftest-rpc",
    "--selftest-lock", "--selftest-exit", "--selftest-svcmenu", "--selftest-perf",
    "--selftest-logsink", "--selftest-uithread",
  };

  [Theory] [MemberData(nameof(DiagnosticFlags))]
  public void EveryDiagnosticFlag_IsLockFree(string flag) {
    var c = CliOptions.Parse(new[] { flag });
    Assert.True(c.IsDiagnostic, $"{flag} 未登记进 IsDiagnostic ⇒ 会占用单实例锁并可能落到 Application.Run");
    Assert.True(c.IsLockFree, $"{flag} 应免锁");
  }

  // Desktop\apply-dsh-tray.cmd 的「新构建健康哨」就是跑这两条，它们必须落在诊断模式里
  [Theory]
  [InlineData("--selftest-exit")]
  [InlineData("--selftest-svcmenu")]
  public void ApplyScriptHealthChecks_AreDiagnostic(string flag) {
    Assert.True(CliOptions.Parse(new[] { flag }).IsDiagnostic);
  }

  [Fact] public void DumpMenu_FirstArg_IsDiagnostic() {
    var c = CliOptions.Parse(new[] { "--dump-menu" });
    Assert.True(c.DumpMenu);
    Assert.True(c.IsDiagnostic);
  }

  // 记录历史怪癖：--dump-menu **只认 args[0]**，而其余标志认任意位置。
  // 这是既有契约（调用形态恒为 `DSHTray.exe --dump-menu`），S4 刻意不"顺手修好"。
  // 哪天要对齐成"任意位置"，这条就是那个变更的显式清单项。
  [Fact] public void DumpMenu_OnlyFirstArg_ButOtherFlagsAnywhere() {
    var c = CliOptions.Parse(new[] { "--selftest-perf", "--dump-menu" });
    Assert.False(c.DumpMenu);
    Assert.True(c.SelftestPerf);
    Assert.True(CliOptions.Parse(new[] { "-x", "--selftest-perf" }).SelftestPerf);
  }

  [Fact] public void NoArgs_EverythingOff_SoItBecomesTheTray() {
    foreach (var args in new[] { (string[]?)null, Array.Empty<string>() }) {
      var c = CliOptions.Parse(args);
      Assert.False(c.IsDiagnostic);
      Assert.False(c.IsBench);
      Assert.False(c.IsLockFree);   // ⇒ 占单实例锁 = 真托盘
      Assert.False(c.AskExit);
    }
  }

  [Fact] public void Exit_DefaultsToKeepingTheModel() {
    var c = CliOptions.Parse(new[] { "--exit" });
    Assert.True(c.AskExit); Assert.False(c.StopAll); Assert.False(c.NoStop);
  }

  [Fact] public void Exit_WithStopAll_StopsTheModel() {
    var c = CliOptions.Parse(new[] { "--exit", "--stopall" });
    Assert.True(c.AskExit); Assert.True(c.StopAll);
  }

  // --nostop 是 legacy 别名，语义等同于默认（保留模型）
  [Fact] public void NoStop_IsAliasForKeepModel() {
    var c = CliOptions.Parse(new[] { "--nostop" });
    Assert.True(c.AskExit); Assert.True(c.NoStop); Assert.False(c.StopAll);
  }

  [Fact] public void Bench_PortOnly_UsesDocumentedDefaults() {
    var c = CliOptions.Parse(new[] { "--bench", "8081" });
    Assert.Equal(8081, c.BenchPort);
    Assert.Equal(512,  c.BenchPrompt);
    Assert.Equal(64,   c.BenchGen);
    Assert.Equal(3,    c.BenchRuns);
    Assert.True(c.IsBench);
    Assert.True(c.IsLockFree);      // 基准同样不能占锁
    Assert.False(c.IsDiagnostic);   // 但它不是自检
  }

  [Fact] public void Bench_FullArgs_AllParsed() {
    var c = CliOptions.Parse(new[] { "--bench", "8081", "1024", "128", "5" });
    Assert.Equal(8081, c.BenchPort);
    Assert.Equal(1024, c.BenchPrompt);
    Assert.Equal(128,  c.BenchGen);
    Assert.Equal(5,    c.BenchRuns);
  }

  // 端口非数字 ⇒ TryParse 置 0 ⇒ **不是**基准模式 ⇒ 会占锁并走到 Application.Run。
  // 这是既有行为，钉住它而不是"修"它（要改属行为变更，需单独评估）。
  [Fact] public void Bench_NonNumericPort_FallsBackToTrayMode() {
    var c = CliOptions.Parse(new[] { "--bench", "abc" });
    Assert.Equal(0, c.BenchPort);
    Assert.False(c.IsBench);
    Assert.False(c.IsLockFree);
  }

  [Fact] public void Bench_WithoutPort_IsNotBench() {
    Assert.False(CliOptions.Parse(new[] { "--bench" }).IsBench);
  }
}
