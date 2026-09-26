using System;
using System.IO;

namespace QwenTray.Tests;

// 2026-09-13：L3 硬盘 KV 缓存的「启动预热」判定与结果解析。
//
// 为什么这些值得钉住：预热是**唯一会自己动显存 KV 的功能**（回收只推权重页，可再取）。
// 它判错的代价分两类，两类都在这里做成显式负向断言：
//   ① 该不跑时跑了（有请求在处理 / 没有空槽）⇒ 抢占正在推理的槽位或显存带宽；
//   ② 该拦住时放行（条目与当前模型/KV 类型不匹配）⇒ restore 走到 GGML_ASSERT，
//      最坏 abort 掉整个 llama-server（源码证据：src/llama-context.cpp:3274 只校验 magic/version，
//      不校验模型/KV 类型；故绑定校验只能由我们自己兜）。
public class WarmupTests {

  // ── 判定内核 ───────────────────────────────────────────────────────
  [Fact] public void Allow_HappyPath() {
    var (allow, why) = Warmup.Allow(enabled: true, anyProcessing: false, entries: 3, freeSlots: 2);
    Assert.True(allow);
    Assert.Equal("", why);
  }

  [Fact] public void Allow_Disabled_NeverAllows() {
    Assert.False(Warmup.Allow(false, false, 3, 2).allow);
  }

  // 🔴 与 MemTrimTests 的 Idle_ProcessingInFlight_NeverAllows 同源：推理中一律不预热
  [Fact] public void Allow_ProcessingInFlight_NeverAllows() {
    Assert.False(Warmup.Allow(true, anyProcessing: true, entries: 9, freeSlots: 9).allow);
  }

  [Fact] public void Allow_NoEntries_NeverAllows() {
    Assert.False(Warmup.Allow(true, false, entries: 0, freeSlots: 2).allow);
  }

  // 没有空槽 ⇒ 绝不预热：否则就是覆盖别人（另一个会话）正在用的 KV
  [Fact] public void Allow_NoFreeSlot_NeverAllows() {
    Assert.False(Warmup.Allow(true, false, entries: 3, freeSlots: 0).allow);
  }

  // 负值 = "调用方不知道" ⇒ 该条判据不参与（托盘不知道条目数/空槽数，那是 kvctl 的活）
  [Fact] public void Allow_UnknownCounts_DoNotBlock() {
    Assert.True(Warmup.Allow(true, false, entries: -1, freeSlots: -1).allow);
    Assert.True(Warmup.Allow(true, false, entries: 5,  freeSlots: -1).allow);
    Assert.True(Warmup.Allow(true, false, entries: -1, freeSlots: 5).allow);
  }

  // 但"不知道"不等于放行一切：托盘**知道**的两件事（开关、有没有请求）永远是硬闸门
  [Fact] public void Allow_UnknownCounts_StillRespectKnownGates() {
    Assert.False(Warmup.Allow(false, false, -1, -1).allow);
    Assert.False(Warmup.Allow(true, true, -1, -1).allow);
  }

  // 判据顺序：开关优先（关着的时候不该因为"有请求"而报一个误导性原因）
  [Fact] public void Allow_ReasonOrder_EnabledFirst() {
    Assert.Equal("开关关闭", Warmup.Allow(false, true, 0, 0).why);
    Assert.Contains("有请求在处理", Warmup.Allow(true, true, 0, 0).why);
    Assert.Contains("没有条目", Warmup.Allow(true, false, 0, 0).why);
    Assert.Contains("没有空闲槽", Warmup.Allow(true, false, 1, 0).why);
  }

  // ── KV 类型名（写进 _runtime.json 的那两项）──────────────────────────
  // kvMode==0 是"不显式传参 ⇒ llama 默认 f16"。登记时必须写 f16：写成 "default"/空串
  // 会让 kvctl 的 binding_check 永远 mismatch ⇒ 自动预热静默失效。
  [Theory]
  [InlineData(0, false, "f16", "f16")]
  [InlineData(1, false, "q8_0", "q8_0")]
  [InlineData(2, false, "f16", "f16")]
  [InlineData(1, true,  "f16", "f16")]   // CPU 模式不传 cache 类型
  public void KvTypes_MapsToRealTypeNames(int kvMode, bool cpu, string k, string v) {
    var t = Warmup.KvTypes(kvMode, cpu);
    Assert.Equal(k, t.k);
    Assert.Equal(v, t.v);
  }

  // ── JSON 转义与运行态登记 ──────────────────────────────────────────
  [Fact] public void JsonEscape_QuotesAndBackslashes() {
    Assert.Equal("a\\\\b", Warmup.JsonEscape(@"a\b"));      // 反斜杠必须加倍（Windows 路径必踩）
    Assert.Equal("a\\\"b", Warmup.JsonEscape("a\"b"));
    Assert.Equal("", Warmup.JsonEscape(null!));
    Assert.Equal("", Warmup.JsonEscape(""));
  }

  [Fact] public void RuntimeJson_HasAllBindingFields() {
    string j = Warmup.RuntimeJson(@"E:\m\x.gguf", "q8_0", "q8_0", "on", 262144, 42, 8082);
    foreach (var key in new[] { "kvTypeK", "kvTypeV", "flashAttn", "nCtx", "pid", "port", "model" })
      Assert.Contains("\"" + key + "\"", j);
    Assert.Contains(@"""nCtx"":262144", j);                 // 数字不加引号
    Assert.Contains(@"E:\\m\\x.gguf", j);                   // 路径被转义
    Assert.Contains("\"model\":\"", j);
  }

  // 运行态必须与 --slot-save-path 同目录；未启用（空路径）时没有落点
  [Fact] public void RuntimePathFor_FollowsSlotDir() {
    Assert.Equal(Warmup.DefaultRuntimePath, Warmup.RuntimePathFor(LaunchArgs.DefaultSlotSavePath));
    Assert.Equal("", Warmup.RuntimePathFor(""));
    Assert.Equal("", Warmup.RuntimePathFor("   "));
  }

  [Fact] public void WriteRuntime_ActuallyWritesUtf8NoBom() {
    string dir = Path.Combine(Path.GetTempPath(), "qwentray-warmup-" + Guid.NewGuid().ToString("N"));
    string p = Path.Combine(dir, "_runtime.json");
    try {
      string json = Warmup.RuntimeJson("m", "q8_0", "q8_0", "on", 4096, 1, 9);
      string err; Assert.True(Warmup.WriteRuntime(p, json, out err), err);
      Assert.True(File.Exists(p));
      var bytes = File.ReadAllBytes(p);
      Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);  // 无 BOM
      Assert.Equal(json, File.ReadAllText(p));                                                     // 逐字节往返
    } finally { try { Directory.Delete(dir, true); } catch { } }
  }

  // ── 结果解析（kvctl warmup --json 的回执）──────────────────────────
  // 🔴 夹具必须是**实拍原文**，绝不能自己拼紧凑 JSON（2026-09-13 端到端实测踩到的就是这个）：
  //    kvctl 走 python json.dumps 的默认分隔符 ⇒ 冒号后面**有一个空格**（"restored": 1）。
  //    第一版夹具写成紧凑 JSON，于是手写扫描器"刚好"能过；真机上整字段取不到，
  //    托盘对着"已恢复 1 条"报「未恢复（计划 0 条）/ RESULT NOOP」。
  //    下面两条抄自 e2e_warmup.py 的真实回执：成功恢复一条 / 绑定不一致被拒。
  const string REAL_OK =
    "{\"port\": 8099, \"model\": \"faf6301597d8\", \"dryRun\": false, \"runtime\": true, \"attempted\": 1, " +
    "\"restored\": 1, \"picks\": [{\"key\": \"a98c1d45dedfd531\", \"file\": \"a98c1d45dedfd531.bin\", \"slot\": 0, " +
    "\"tokens\": 1024, \"bytes\": 80233368, \"createdAt\": \"2026-09-13T18:00:19.443+08:00\", \"lastHitAt\": null, " +
    "\"verified\": true}], \"skips\": [], \"results\": [{\"key\": \"a98c1d45dedfd531\", \"slot\": 0, \"http\": 200, " +
    "\"n_restored\": 1024, \"restore_ms\": 29.427}], \"ok\": true}";
  const string REAL_SKIP =
    "{\"port\": 8099, \"model\": \"faf6301597d8\", \"dryRun\": false, \"runtime\": true, \"attempted\": 0, " +
    "\"restored\": 0, \"picks\": [], \"skips\": [{\"key\": \"a98c1d45dedfd531\", " +
    "\"why\": \"绑定不一致 → kvTypeK 条目=q8_0 ≠ 运行=q4_0; kvTypeV 条目=q8_0 ≠ 运行=q4_0\"}], \"results\": [], \"ok\": false}";

  [Fact] public void ParseResult_RealKvctlOk() {
    var r = Warmup.ParseResult(REAL_OK);
    Assert.True(r.ok);
    Assert.Equal(1, r.restored);        // ← 旧手写扫描器在这里恒返回 0（"restored": 后面带空格）
    Assert.Equal(1, r.attempted);
    Assert.Contains("1/1", r.line);
  }

  // 🔴 回归锁：冒号后带空格的 JSON 必须照样解析（json.dumps 默认分隔符；这就是真机翻车点）
  [Fact] public void ParseResult_ToleratesSpaceAfterColon() {
    var r = Warmup.ParseResult("{\"attempted\": 2, \"restored\": 2, \"ok\": true}");
    Assert.True(r.ok);
    Assert.Equal(2, r.restored);
    Assert.Equal(2, r.attempted);
  }

  [Fact] public void ParseResult_RealKvctlSkip_CarriesReason() {
    var r = Warmup.ParseResult(REAL_SKIP);
    Assert.False(r.ok);
    Assert.Equal(0, r.restored);
    Assert.Contains("绑定不一致", r.line);   // 失败必须带原因，否则用户无法区分"没条目"与"被闸门拦"
  }

  // 前导日志行不能干扰解析：从后往前找第一条**能解析**的 JSON 行
  [Fact] public void ParseResult_SkipsLeadingLogLines() {
    var r = Warmup.ParseResult("模型指纹 : abc\n运行态   : (无登记)\n{\"attempted\": 1, \"restored\": 1, \"ok\": true}");
    Assert.True(r.ok);
    Assert.Equal(1, r.restored);
  }

  // 🔴 顶层 restored 不能被 results[].n_restored 抢先匹配（两者只差下划线）—— 用实拍数据考这条
  [Fact] public void ParseResult_DoesNotConfuse_Restored_With_n_restored() {
    var r = Warmup.ParseResult(REAL_OK);
    Assert.Equal(1, r.restored);
    Assert.Equal(1, r.attempted);
  }

  [Fact] public void ParseResult_NoJson_IsNotOk() {
    var r = Warmup.ParseResult("没有条目可预热。");
    Assert.False(r.ok);
    Assert.Contains("没有条目可预热", r.line);
    Assert.False(Warmup.ParseResult("").ok);
    Assert.False(Warmup.ParseResult(null!).ok);
  }

  // ── python 定位（自动预热最可能的失效方式就是"找不到 python"，且它是静默失效）
  [Fact] public void PickPython_PicksFirstExisting() {
    var cands = new[] { "D:\\nope\\python.exe", "E:\\yes\\python.exe", "python.exe" };
    Assert.Equal("E:\\yes\\python.exe", Warmup.PickPython(cands, p => p.StartsWith("E:")));
    Assert.Equal("", Warmup.PickPython(cands, _ => false));
    Assert.Equal("python.exe", Warmup.PickPython(new[] { "", "  ", "python.exe" }, p => p.Trim().Length > 0));
  }

  [Fact] public void PythonCandidates_EnvOverrideGoesFirst() {
    string? old = Environment.GetEnvironmentVariable("DSH_PYTHON");
    try {
      Environment.SetEnvironmentVariable("DSH_PYTHON", @"X:\custom\python.exe");
      Assert.Equal(@"X:\custom\python.exe", Warmup.PythonCandidates()[0]);
    } finally { Environment.SetEnvironmentVariable("DSH_PYTHON", old); }
  }

  // ── 文案 ───────────────────────────────────────────────────────────
  [Fact] public void ResultLine_DistinguishesOkFromNoop() {
    Assert.StartsWith("[OK]", Warmup.ResultLine(true, 2, 2, ""));
    Assert.StartsWith("[--]", Warmup.ResultLine(false, 0, 3, ""));
    Assert.Contains("0/3", Warmup.ResultLine(false, 0, 3, ""));
  }

  [Fact] public void PolicyText_SaysWhatWillHappen() {
    Assert.Contains("开", Warmup.PolicyText(true));
    Assert.Contains("手动", Warmup.PolicyText(false));
  }
}
