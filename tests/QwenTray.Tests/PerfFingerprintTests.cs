namespace QwenTray.Tests;

// S4（2026-09-12）：配置指纹 cfgId。
//
// cfgId 是性能台账（~/.dsh/tools/model-perf/configs.json）的主键。它一变，历史样本就与台账对不上 ——
// "哪套配置更快"这个核心问题立刻失去可比性，而且是**静默**失去（台账里多出一条看着正常的新记录）。
// 因此本文件最重要的一条是 Real8081Config_StillHashesTo_966d1620a3：
// args 抄自台账里 id=966d1620a3 那条的原文（当前跑在 8081 上的那套 Qwen3.6-35B 配置）。
// 它与托盘内的 FP-REAL-8081 探针同源，但现在是标准单测 —— `dotnet test` 就能跑，不必起托盘。
public class PerfFingerprintTests {
  const string Exe  = @"E:\llm-deploy\llamacpp\llama-b10797-cuda12.4\llama-server.exe";
  const string Exe2 = @"E:\llm-deploy\llamacpp\llama-b10800-cuda12.4\llama-server.exe";

  static List<string> BaseArgs() => new() {
    "-m", @"E:\models\X.gguf", "-c", "262144", "-ngl", "99", "--split-mode", "tensor",
    "-ts", "50,50", "--cache-ram", "-1", "--cont-batching", "--port", "8081", "--host", "0.0.0.0",
  };

  static List<string> Real8081Args() => new() {
    "-m", @"E:\llm-deploy\models\Qwen3.6-35B-A3B-Claude-4.7-Opus-Reasoning-Distilled-APEX-MTP-I-Compact.gguf",
    "-c", "262144", "-ngl", "99", "--split-mode", "tensor", "-ts", "50,50", "--main-gpu", "0",
    "--flash-attn", "on", "--cache-type-k", "q8_0", "--cache-type-v", "q8_0",
    "-b", "1024", "-ub", "1024", "--cont-batching", "--cache-ram", "2048",
    "--port", "8081", "--host", "0.0.0.0", "--reasoning", "on", "--reasoning-format", "deepseek",
    "--jinja", "--temp", "0.6", "--top-p", "0.95", "--top-k", "20", "--min-p", "0.0",
    "--presence-penalty", "0.0", "--repeat-penalty", "1.0",
  };

  [Fact] public void Real8081Config_StillHashesTo_966d1620a3() {
    Assert.Equal("966d1620a3", PerfFingerprint.Compute(Real8081Args(), Exe));
  }

  [Fact] public void IsStable_AndExactlyTenChars() {
    var id = PerfFingerprint.Compute(BaseArgs(), Exe);
    Assert.Equal(10, id.Length);
    Assert.Equal(id, PerfFingerprint.Compute(BaseArgs(), Exe));
  }

  // --port / --host 被剔除：同一套配置跑在不同端口上仍是同一个 cfg
  [Fact] public void IgnoresPortAndHost() {
    var a = BaseArgs();
    var b = new List<string>(a);
    b[b.IndexOf("8081")]    = "9999";
    b[b.IndexOf("0.0.0.0")] = "127.0.0.1";
    Assert.Equal(PerfFingerprint.Compute(a, Exe), PerfFingerprint.Compute(b, Exe));
  }

  // 顺序无关**仅**指"整对 option+value 换位"——Normalize 靠 opt/value 成对扫描，
  // 单独把值挪走会改变配对（那是另一回事，见下面 Normalize 的用例）。
  [Fact] public void MovingAWholePair_DoesNotChangeId() {
    var a = BaseArgs();
    var b = new List<string>(a);
    int i = b.IndexOf("-c");
    b.RemoveRange(i, 2);                       // 整对搬走
    b.Add("-c"); b.Add("262144");              // 放到末尾
    Assert.Equal(PerfFingerprint.Compute(a, Exe), PerfFingerprint.Compute(b, Exe));
  }

  [Fact] public void ChangingTs_ChangesId() {
    var a = BaseArgs();
    var b = new List<string>(a);
    b[b.IndexOf("50,50")] = "30,70";
    Assert.NotEqual(PerfFingerprint.Compute(a, Exe), PerfFingerprint.Compute(b, Exe));
  }

  // 换 llama.cpp 版本目录 = 换编译器 ⇒ 必须算作不同配置（否则升级带来的性能变化会被误读成"配置回退"）
  [Fact] public void DifferentLlamaBuildDirectory_ChangesId() {
    Assert.NotEqual(PerfFingerprint.Compute(BaseArgs(), Exe), PerfFingerprint.Compute(BaseArgs(), Exe2));
  }

  // --- Normalize ---

  [Fact] public void Normalize_KeepsNegativeOneAsAValue() {
    Assert.Contains(PerfFingerprint.Normalize(BaseArgs()),
      kv => kv.Key == "--cache-ram" && kv.Value == "-1");
  }

  [Fact] public void Normalize_TreatsATrailingFlagAsEmptyValue() {
    Assert.Contains(PerfFingerprint.Normalize(BaseArgs()),
      kv => kv.Key == "--cont-batching" && kv.Value == "");
  }

  [Fact] public void Normalize_DropsPortAndHost() {
    var n = PerfFingerprint.Normalize(BaseArgs());
    Assert.DoesNotContain(n, kv => kv.Key == "--port");
    Assert.DoesNotContain(n, kv => kv.Key == "--host");
  }

  [Fact] public void Normalize_SortsByKey() {
    var keys = PerfFingerprint.Normalize(BaseArgs()).ConvertAll(kv => kv.Key);
    var sorted = new List<string>(keys); sorted.Sort(StringComparer.Ordinal);
    Assert.Equal(sorted, keys);
  }

  // --- ExeTag ---

  [Fact] public void ExeTag_IsTheBuildDirectoryName() {
    Assert.Equal("llama-b10797-cuda12.4", PerfFingerprint.ExeTag(Exe));
  }

  [Fact] public void ExeTag_BareFileNameOrEmpty_IsQuestionMark() {
    Assert.Equal("llama-server.exe", PerfFingerprint.ExeTag("llama-server.exe"));
    Assert.Equal("?", PerfFingerprint.ExeTag(""));
    Assert.Equal("?", PerfFingerprint.ExeTag(null!));
  }

  // --- Summary（进台账 configs.json 的 opt 字段，供界面一眼对比）---

  [Fact] public void Summary_RendersCtxAndUnlimitedCacheRam() {
    var s = PerfFingerprint.Summary(BaseArgs());
    Assert.Equal("256K", s["ctx"]);
    Assert.Equal("不限", s["cacheRam"]);   // -1 = 无限制
  }

  [Fact] public void Summary_MapsEveryRecognizedOption() {
    var a = new List<string>{
      "-m", @"E:\models\M.gguf", "--mmproj", @"E:\models\mm.gguf", "-c", "131072",
      "-ngl", "99", "--split-mode", "tensor", "-ts", "60,40", "--flash-attn", "on",
      "--cache-type-k", "q8_0", "--cache-type-v", "f16", "-b", "2048", "-ub", "512",
      "--cache-ram", "2048", "--spec-type", "draft-mtp", "--spec-draft-n-max", "3",
    };
    var s = PerfFingerprint.Summary(a);
    Assert.Equal("M.gguf",    s["model"]);
    Assert.Equal("mm.gguf",   s["mmproj"]);
    Assert.Equal("128K",      s["ctx"]);
    Assert.Equal("99",        s["ngl"]);
    Assert.Equal("tensor",    s["split"]);
    Assert.Equal("60,40",     s["ts"]);
    Assert.Equal("on",        s["flash"]);
    Assert.Equal("q8_0",      s["kvK"]);
    Assert.Equal("f16",       s["kvV"]);
    Assert.Equal("2048",      s["batch"]);
    Assert.Equal("512",       s["ubatch"]);
    Assert.Equal("2G",        s["cacheRam"]);
    Assert.Equal("draft-mtp", s["spec"]);
    Assert.Equal("3",         s["mtpN"]);
  }

  // FmtCtx：只有"1024 的倍数且 >= 1024"才折成 K，否则原样显示（不四舍五入、不猜）
  [Fact] public void Summary_CtxNotAMultipleOf1024_IsShownRaw() {
    var a = BaseArgs();
    a[a.IndexOf("262144")] = "100000";
    Assert.Equal("100000", PerfFingerprint.Summary(a)["ctx"]);
  }

  // FmtRam 的四条分支都要走到：不限(-1) / 关(0) / G(1024 倍数) / M(其它)
  [Theory]
  [InlineData("-1",   "不限")]
  [InlineData("0",    "关")]
  [InlineData("2048", "2G")]
  [InlineData("4096", "4G")]
  [InlineData("512",  "512M")]
  public void Summary_RendersCacheRam(string raw, string shown) {
    var a = BaseArgs();
    a[a.IndexOf("--cache-ram") + 1] = raw;
    Assert.Equal(shown, PerfFingerprint.Summary(a)["cacheRam"]);
  }

  // --- EnvTag：环境变量也属于"这套部署"的一部分（换哪几张卡、要不要内置 AllReduce）---

  [Fact] public void EnvTag_EncodesBothSwitches() {
    Assert.Equal("cvd=0,1;alr=internal", PerfFingerprint.EnvTag("0,1", "internal"));
    Assert.Equal("cvd=1",                PerfFingerprint.EnvTag("1", ""));
    Assert.Equal("alr=internal",         PerfFingerprint.EnvTag("", "internal"));
    Assert.Equal("",                     PerfFingerprint.EnvTag("", ""));
  }
}
