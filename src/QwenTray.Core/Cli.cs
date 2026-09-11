using System;

namespace QwenTray;

// S4（2026-09-12）：命令行解析从 Program2.Main 抽出 —— 纯数据、零副作用 ⇒ 可被 QwenTray.Tests 穷举。
//
// 为什么这段值得单独抽出来做测试（不是"为了测试而测试"）：
//   Main 里这段解析直接决定**进程要不要走到 Application.Run**，而历史上漏写一个探针分支的代价是
//   起出一个没有单实例锁、带通知区图标、且永不退出的「幽灵托盘」，只能手工 taskkill。
//   变成 CliOptions 之后，「每一个诊断标志都必须让 IsDiagnostic 为真」就是一条可穷举的断言 ——
//   新增探针时忘了登记，测试会当场红，而不是等到某天在通知区里多出一个幽灵。
//
// 另一个直接受益者：Desktop\apply-dsh-tray.cmd 的「新构建健康哨」就是跑
//   DSHTray.exe --selftest-exit 与 --selftest-svcmenu
// 这两条分派的正确性（含"绝不走到 Application.Run"）现在被单测看住了。
public sealed class CliOptions {
  // —— 诊断 / 自检开关 ——
  public bool DumpMenu;          // --dump-menu（⚠️ 历史上只认 args[0]，见 Parse）
  public bool SelftestLogWin;    // --selftest-logwin   （日志窗）
  public bool SelftestMenuShow;  // --selftest-menushow （会真弹菜单）
  public bool SelftestPlugins;   // --selftest-plugins
  public bool SelftestRpc;       // --selftest-rpc
  public bool SelftestLock;      // --selftest-lock
  public bool SelftestExit;      // --selftest-exit     ← apply 脚本用它当健康哨
  public bool SelftestSvcMenu;   // --selftest-svcmenu  ← apply 脚本用它当健康哨
  public bool SelftestPerf;      // --selftest-perf
  public bool SelftestLogSink;   // --selftest-logsink
  public bool SelftestUiThread;  // --selftest-uithread

  // —— 命令行真实基准：DSHTray.exe --bench <port> [promptTok] [genTok] [runs] ——
  public int BenchPort;
  public int BenchPrompt = 512;
  public int BenchGen    = 64;
  public int BenchRuns   = 3;

  // —— 退出语义（2026-09-11 反转：默认「只退托盘、保留模型」）——
  public bool StopAll;   // --stopall ：连模型一起停
  public bool NoStop;    // --nostop  ：legacy alias，等价默认（保留模型）
  public bool AskExit;   // --exit | --nostop | --stopall

  /// <summary>全部「诊断/自检模式」——**不含** --bench（它和探针共用同一批前置 return，但语义上不是自检）。</summary>
  public bool IsDiagnostic =>
    DumpMenu || SelftestLogWin || SelftestMenuShow || SelftestPlugins || SelftestRpc || SelftestLock ||
    SelftestExit || SelftestSvcMenu || SelftestPerf || SelftestLogSink || SelftestUiThread;

  /// <summary>--bench 是否给了可用端口（端口解析失败 ⇒ 0 ⇒ 不是基准模式）。</summary>
  public bool IsBench => BenchPort > 0;

  /// <summary>不占单实例锁、不建 TaskbarWatcher、**绝不**走到 Application.Run。</summary>
  public bool IsLockFree => IsDiagnostic || IsBench;

  public static CliOptions Parse(string[]? args) {
    var c = new CliOptions();
    if (args == null) return c;

    // ⚠️ 历史行为，刻意如实保留（S4 是行为中性重构，不"顺手修好"）：
    //    --dump-menu **只认 args[0]**，而所有 --selftest-* 认任意位置。
    //    实际调用形态都是 `DSHTray.exe --dump-menu` ⇒ 两种写法等价。
    //    要把它对齐成"任意位置"属于行为变更，应单独评估，不能混在抽离里。
    c.DumpMenu = args.Length > 0 && args[0] == "--dump-menu";

    bool Has(string name) => Array.IndexOf(args, name) >= 0;
    c.SelftestLogWin   = Has("--selftest-logwin");
    c.SelftestMenuShow = Has("--selftest-menushow");
    c.SelftestPlugins  = Has("--selftest-plugins");
    c.SelftestRpc      = Has("--selftest-rpc");
    c.SelftestLock     = Has("--selftest-lock");
    c.SelftestExit     = Has("--selftest-exit");
    c.SelftestSvcMenu  = Has("--selftest-svcmenu");
    c.SelftestPerf     = Has("--selftest-perf");
    c.SelftestLogSink  = Has("--selftest-logsink");
    c.SelftestUiThread = Has("--selftest-uithread");

    // 只认第一个 --bench；端口缺失/非数字 ⇒ TryParse 置 0 ⇒ 非基准模式（与抽取前一致）
    int bi = Array.IndexOf(args, "--bench");
    if (bi >= 0 && bi + 1 < args.Length) {
      int.TryParse(args[bi + 1], out c.BenchPort);
      if (bi + 2 < args.Length) int.TryParse(args[bi + 2], out c.BenchPrompt);
      if (bi + 3 < args.Length) int.TryParse(args[bi + 3], out c.BenchGen);
      if (bi + 4 < args.Length) int.TryParse(args[bi + 4], out c.BenchRuns);
    }

    c.StopAll = Has("--stopall");
    c.NoStop  = Has("--nostop");
    c.AskExit = Has("--exit") || c.NoStop || c.StopAll;
    return c;
  }
}
