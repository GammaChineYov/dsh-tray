namespace QwenTray.Tests;

// S5-4（2026-09-12）：菜单「重启模型」重读配置的**判定内核**（Find + Apply）—— 从 TrayApp 搬进 Core 后钉住。
//
// 重点不在"能比对字段"，而在**三条守卫**：用户手改 JSON 之后能不能生效、会不会反过来把内存里的值抹掉。
//   · Batch/Ubatch 只在配置文件里 > 0 时才覆盖（没写 = 不表态，不是"改成 0"）
//   · Provider 只在非空时才覆盖（留空 = 不表态，不是"清空 provider"）
//   · Mmproj 是**双条件**：开关或路径任一变化都算变化
// 这三条原先没有任何断言，而"改了的配置没生效 / 没改的东西被抹掉"正是这类投诉的根因。
//
// 另一条容易漏的：Apply 只写**字段**，不写 Name —— 改名由 Find 的名字匹配承担，顺序反了就会
// 出现"配置里改了名，重读后内存里名字没变、下次又按旧名匹配"的循环。
public class SvcConfigDiffTests {

  static AppConfig Cfg(params ServiceConfig[] svcs) {
    var c = new AppConfig();
    c.Services.AddRange(svcs);
    return c;
  }

  static ServiceSpec Spec(string model = @"C:\m\A.gguf", int port = 8081, int batch = 1024, int ubatch = 1024,
                          bool useMm = false, string mm = "", bool specDecode = false, string provider = "llama-local") {
    return new ServiceSpec {
      Name = "S", Model = model, Port = port, Batch = batch, Ubatch = ubatch,
      UseMmproj = useMm, Mmproj = mm, SpecDecode = specDecode, Provider = provider,
    };
  }

  static ServiceConfig CfgOf(string name = "S", int port = 8081, string model = @"C:\m\A.gguf", int batch = 1024,
                             int ubatch = 1024, bool useMm = false, string mm = "", bool specDecode = false,
                             string provider = "llama-local") {
    return new ServiceConfig {
      Name = name, Port = port, Model = model, Batch = batch, Ubatch = ubatch,
      UseMmproj = useMm, Mmproj = mm, SpecDecode = specDecode, Provider = provider,
    };
  }

  // ---------------- Find ----------------

  // 名字命中优先于端口命中：端口被改过时，名字才是用户真正在改的那一项
  [Fact] public void Find_MatchesByName_IgnoringCase() {
    var c = Cfg(CfgOf(name: "Other", port: 9999), CfgOf(name: "QWEN3.6-35B", port: 8081));
    Assert.Equal("QWEN3.6-35B", SvcConfigDiff.Find(c, "qwen3.6-35b", 9999)!.Name);
  }

  [Fact] public void Find_FallsBackToPort_WhenNameChanged() {
    var c = Cfg(CfgOf(name: "Renamed", port: 8082));
    Assert.Equal("Renamed", SvcConfigDiff.Find(c, "OldName", 8082)!.Name);
  }

  [Fact] public void Find_NothingMatches_IsNull() {
    Assert.Null(SvcConfigDiff.Find(Cfg(CfgOf(name: "A", port: 8081)), "B", 9999));
    Assert.Null(SvcConfigDiff.Find(null, "A", 8081));
    Assert.Null(SvcConfigDiff.Find(new AppConfig(), "A", 8081));   // Services 空表
  }

  // ---------------- Apply ----------------

  [Fact] public void Apply_NoChange_ReturnsEmpty() {
    Assert.Empty(SvcConfigDiff.Apply(CfgOf(), Spec()));
  }

  [Fact] public void Apply_ModelPortSpecDecode_AreWrittenThrough() {
    var cur = Spec(model: @"C:\m\A.gguf", port: 8081, specDecode: false);
    var diffs = SvcConfigDiff.Apply(CfgOf(model: @"C:\m\B.gguf", port: 8082, specDecode: true), cur);
    Assert.Equal(3, diffs.Count);
    Assert.Equal(@"C:\m\B.gguf", cur.Model);   // 写穿：调用方传的是 svc.Spec（引用类型）
    Assert.Equal(8082, cur.Port);
    Assert.True(cur.SpecDecode);
  }

  // 守卫 1：配置文件里没写 Batch/Ubatch（=0）＝ 不表态。绝不能把内存里的值抹成 0
  //（抹掉后 llama-server 会按 0 起，启动参数直接失效/崩）
  [Fact] public void Apply_ZeroBatchUbatch_IsNotApplied() {
    var cur = Spec(batch: 2048, ubatch: 512);
    var diffs = SvcConfigDiff.Apply(CfgOf(batch: 0, ubatch: 0), cur);
    Assert.Empty(diffs);
    Assert.Equal(2048, cur.Batch);
    Assert.Equal(512, cur.Ubatch);
  }

  [Fact] public void Apply_PositiveBatchUbatch_AreApplied() {
    var cur = Spec(batch: 1024, ubatch: 1024);
    var diffs = SvcConfigDiff.Apply(CfgOf(batch: 2048, ubatch: 512), cur);
    Assert.Equal(2, diffs.Count);
    Assert.Equal(2048, cur.Batch);
    Assert.Equal(512, cur.Ubatch);
  }

  // 守卫 2：Provider 留空 ＝ 不表态，不是"清空 provider"
  [Fact] public void Apply_EmptyProvider_IsNotApplied() {
    var cur = Spec(provider: "llama-local-thinking");
    Assert.Empty(SvcConfigDiff.Apply(CfgOf(provider: ""), cur));
    Assert.Equal("llama-local-thinking", cur.Provider);
  }

  [Fact] public void Apply_NonEmptyProvider_IsApplied() {
    var cur = Spec(provider: "llama-local");
    var diffs = SvcConfigDiff.Apply(CfgOf(provider: "llama-local-thinking"), cur);
    Assert.Single(diffs);
    Assert.Equal("llama-local-thinking", cur.Provider);
  }

  // 守卫 3：Mmproj 双条件 —— 只勾开关、只换路径、关掉，三种都算"变化"
  [Fact] public void Apply_MmprojFlagOrPath_EachCountsAsChange() {
    // 只勾开关，文件不动
    var cur = Spec(useMm: false, mm: @"C:\m\mm.gguf");
    Assert.Single(SvcConfigDiff.Apply(CfgOf(useMm: true, mm: @"C:\m\mm.gguf"), cur));
    Assert.True(cur.UseMmproj);

    // 只换路径，开关不动
    cur = Spec(useMm: true, mm: @"C:\m\mm.gguf");
    Assert.Single(SvcConfigDiff.Apply(CfgOf(useMm: true, mm: @"C:\m\mm2.gguf"), cur));
    Assert.Equal(@"C:\m\mm2.gguf", cur.Mmproj);

    // 关掉 → 描述里出现"关"（日志要能一眼看出视觉能力被摘了）
    cur = Spec(useMm: true, mm: @"C:\m\mm.gguf");
    var d = SvcConfigDiff.Apply(CfgOf(useMm: false, mm: @"C:\m\mm.gguf"), cur);
    Assert.Single(d);
    Assert.Contains("→ 关", d[0]);
    Assert.False(cur.UseMmproj);
  }

  // 差异顺序 = 日志里显示的顺序，必须稳定（用户是照这行去核对自己刚改的字段）
  // 注意 "mmproj  → mm.gguf" 中间是**两个**空格：cur.Mmproj 为空时 FileName("") 也返回空串。
  [Fact] public void Apply_DiffOrderIsStable_AndJoinedWithChineseSemicolon() {
    var cur = Spec(model: @"C:\m\A.gguf", port: 8081, batch: 1024, ubatch: 1024,
                   useMm: false, mm: "", specDecode: false, provider: "llama-local");
    var sc = CfgOf(port: 8082, model: @"C:\m\B.gguf", batch: 2048, ubatch: 512,
                   useMm: true, mm: @"C:\m\mm.gguf", specDecode: true, provider: "p2");

    var diffs = SvcConfigDiff.Apply(sc, cur);

    Assert.Equal(
      "模型 A.gguf → B.gguf；端口 8081 → 8082；mmproj  → mm.gguf；批 1024 → 2048；" +
      "ubatch 1024 → 512；SpecDecode False → True；provider llama-local → p2",
      string.Join("；", diffs));
    Assert.Equal(@"C:\m\B.gguf", cur.Model);
    Assert.Equal(2048, cur.Batch);
    Assert.Equal("p2", cur.Provider);
  }

  // Apply 不改名：改名由 Find 承担（否则 Find 用旧名匹配、Apply 又改回新名，两处打架）
  [Fact] public void Apply_DoesNotRenameTheService() {
    var cur = Spec();
    cur.Name = "Old";
    SvcConfigDiff.Apply(CfgOf(name: "New"), cur);
    Assert.Equal("Old", cur.Name);
  }

  // 长路径只显示文件名：完整 GGUF 路径会把日志行撑爆（目录名还会误导"改了文件"）
  [Fact] public void Apply_ReportsFileNames_NotFullPaths() {
    var cur = Spec(model: @"D:\a\very\long\path\to\A.gguf");
    var d = SvcConfigDiff.Apply(CfgOf(model: @"D:\another\place\B.gguf"), cur);
    Assert.Single(d);
    Assert.Equal("模型 A.gguf → B.gguf", d[0]);
  }
}
