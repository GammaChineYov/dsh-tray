namespace QwenTray.Tests;

// 2026-09-20：per-service 覆盖的合成规则。
//
// 这是「服务项写了什么」到「真正执行什么」的**唯一**裁决点，而它有三个很容易写反的地方：
//   ① 自定义参数到底是"取代模板"还是"追加到模板后面"；
//   ② 自定义参数时，由 GPU 选择派生的 CUDA_VISIBLE_DEVICES / GGML_CUDA_ALLREDUCE 还算不算数；
//   ③ 服务项的 Env 与派生环境变量同名时谁赢。
// 三条都只有**一条**正确答案，且错了之后的表现（起不来的服务 / 悄悄跑在别的卡上）都不是报错，
// 所以逐条钉住。
public class LaunchPlanTests {
  static ServiceSpec Spec() => new() {
    Name = "kvmem", Port = 8084, Model = @"E:\models\X.gguf", Batch = 1024, Ubatch = 1024,
  };
  // 一份"托盘内置模板"的替身：多卡 ⇒ envCuda="0,1" 且 envAllreduce="internal"
  static LaunchResult MultiCardBuild() {
    var r = new LaunchResult();
    r.args.AddRange(new[] { "-m", @"E:\models\X.gguf", "-ngl", "99", "--split-mode", "tensor" });
    r.envCuda = "0,1";
    r.envAllreduce = "internal";
    return r;
  }
  // ---------- ① 不覆盖 ⇒ 与改动前逐字节一致 ----------

  [Fact] public void NoOverride_UsesGlobalExe_AndTemplateArgs() {
    var p = LaunchPlan.Compose(Spec(), MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.False(p.ArgsCustom);
    Assert.Equal(@"C:\llama\llama-server.exe", p.Exe);
    Assert.Equal(6, p.Args.Count);                       // 模板原样
    Assert.Equal("0,1", p.Env["CUDA_VISIBLE_DEVICES"]);  // 派生环境照旧
    Assert.Equal("internal", p.Env["GGML_CUDA_ALLREDUCE"]);
    Assert.Empty(p.Warnings);
  }

  [Fact] public void ExeOnly_OverridesExe_ButKeepsTemplate() {
    var s = Spec(); s.Exe = @"G:\kvmem\build-sm75\bin\llama-kvmem-server.exe";
    var p = LaunchPlan.Compose(s, MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.Equal(@"G:\kvmem\build-sm75\bin\llama-kvmem-server.exe", p.Exe);
    Assert.False(p.ArgsCustom);
    Assert.Equal(6, p.Args.Count);
  }

  // ---------- ② 自定义参数 = 取代模板（不是追加） ----------

  [Fact] public void CustomArgs_ReplacesTemplateEntirely() {
    var s = Spec(); s.Args = "-c 262144 --kvmem-budget 36864";
    var p = LaunchPlan.Compose(s, MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.True(p.ArgsCustom);
    Assert.Equal(new[] { "-c", "262144", "--kvmem-budget", "36864" }, p.Args);
    Assert.DoesNotContain("-ngl", p.Args);               // 模板整份作废，不是拼接
    Assert.DoesNotContain("--split-mode", p.Args);
  }

  // 派生环境是"模板的配套产物"，模板作废 ⇒ 派生物也必须作废。
  // 反例（这条断言防的）：单卡 kvmem 顶着 CUDA_VISIBLE_DEVICES=0,1 与 ALLREDUCE=internal 上机。
  [Fact] public void CustomArgs_DropsDerivedCudaEnv() {
    var s = Spec(); s.Args = "-c 262144";
    var p = LaunchPlan.Compose(s, MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.False(p.Env.ContainsKey("CUDA_VISIBLE_DEVICES"));
    Assert.False(p.Env.ContainsKey("GGML_CUDA_ALLREDUCE"));
  }

  // ---------- ③ Env 总是最后叠加，同名覆盖 ----------

  [Fact] public void CustomArgs_ServiceEnv_IsHonoured_AndOverridesDerived() {
    var s = Spec(); s.Args = "-c 262144";
    s.Env = "CUDA_VISIBLE_DEVICES=0\nKVMEM_VISION_DEVICE=gpu";
    var p = LaunchPlan.Compose(s, MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.Equal("0", p.Env["CUDA_VISIBLE_DEVICES"]);    // 压掉了派生值（本例中派生值本就已作废）
    Assert.Equal("gpu", p.Env["KVMEM_VISION_DEVICE"]);
  }

  [Fact] public void ServiceEnv_OverridesDerived_EvenWithoutCustomArgs() {
    var s = Spec(); s.Env = "CUDA_VISIBLE_DEVICES=1";    // 不写 Args ⇒ 走模板，但要换卡
    var p = LaunchPlan.Compose(s, MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.False(p.ArgsCustom);
    Assert.Equal("1", p.Env["CUDA_VISIBLE_DEVICES"]);
    Assert.Equal("internal", p.Env["GGML_CUDA_ALLREDUCE"]);   // 只覆盖同名那一个，其余派生值保留
  }

  [Fact] public void ServiceEnv_KeyIsCaseInsensitive() {
    var s = Spec(); s.Env = "cuda_visible_devices=1";    // Windows 环境变量名不区分大小写
    var p = LaunchPlan.Compose(s, MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.Equal(2, p.Env.Count);                        // 没有并存两个同名键（派生值被就地改写）
    Assert.Equal("1", p.Env["CUDA_VISIBLE_DEVICES"]);
  }

  // ---------- 参数串切分 ----------

  [Fact] public void SplitArgs_WhitespaceFormed_AndQuoteProtectsSpaces() {
    Assert.Equal(new[] { "-c", "262144" }, LaunchPlan.SplitArgs("  -c   262144  "));
    Assert.Equal(new[] { "--mmproj", @"E:\my models\m.gguf" },
                 LaunchPlan.SplitArgs("--mmproj \"E:\\my models\\m.gguf\""));
  }

  [Fact] public void SplitArgs_Empty_ReturnsEmpty() {
    Assert.Empty(LaunchPlan.SplitArgs(null));
    Assert.Empty(LaunchPlan.SplitArgs("   "));
  }

  // 引号不配对**不抛**（抛了会炸在菜单刷新的 try/catch 里被静默吞掉），但必须留痕
  [Fact] public void SplitArgs_UnbalancedQuote_WarnsInsteadOfThrowing() {
    var warn = new List<string>();
    var a = LaunchPlan.SplitArgs("--a \"b c", warn);
    Assert.Equal(new[] { "--a", "b c" }, a);
    Assert.Single(warn);
  }

  // 空串写了但切不出参数 ⇒ 不退化成模板（否则会凑出"自建 exe + 内置模板"这种最危险的组合）
  [Fact] public void CustomArgs_EmptyAfterSplit_DoesNotFallBackToTemplate() {
    var s = Spec(); s.Args = "   ";
    var p = LaunchPlan.Compose(s, MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.False(p.ArgsCustom);                          // 纯空白 = 没写 ⇒ 走模板
    var s2 = Spec(); s2.Args = "\"\"";
    var p2 = LaunchPlan.Compose(s2, MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.True(p2.ArgsCustom);
    Assert.Single(p2.Args);                              // 显式空参数：照切不误，且不借用模板
    Assert.DoesNotContain("-ngl", p2.Args);
  }

  // ---------- {model} / {mmproj} 占位符 ----------

  // 为什么值得：自定义参数串接管了 -m/--mmproj，若让人把路径抄第二遍，改了 Model 忘了改 Args
  // 就会得到"菜单显示 A、实际加载 B"——不报错的错。
  [Fact] public void CustomArgs_SubstitutesModelAndMmprojPlaceholders() {
    var s = Spec(); s.UseMmproj = true; s.Mmproj = @"E:\m\mmproj-Q8_0.gguf";
    s.Args = "-m {model} --mmproj {mmproj} -c 262144";
    var p = LaunchPlan.Compose(s, MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.Equal(new[] { "-m", @"E:\models\X.gguf", "--mmproj", @"E:\m\mmproj-Q8_0.gguf", "-c", "262144" }, p.Args);
    Assert.Empty(p.Warnings);
  }

  // 引用了却没给 ⇒ 留痕（替换出来是空串，命令行的形状会变得很怪，必须能从日志看出为什么）
  [Fact] public void Placeholder_MissingSource_Warns() {
    var s = Spec(); s.UseMmproj = false; s.Args = "--mmproj {mmproj}";
    var warn = new List<string>();
    LaunchPlan.SubstitutePaths(s.Args, s, warn);
    Assert.Single(warn);

    var s2 = Spec(); s2.Model = ""; s2.Args = "-m {model}";
    var warn2 = new List<string>();
    LaunchPlan.SubstitutePaths(s2.Args, s2, warn2);
    Assert.Single(warn2);
  }

  // 直接写死路径照旧能用（不做替换而已）
  [Fact] public void Placeholder_NotUsed_RawPathsPassThrough() {
    var s = Spec(); s.Args = @"-m E:\direct\X.gguf";
    Assert.Equal(new[] { "-m", @"E:\direct\X.gguf" },
                 LaunchPlan.SplitArgs(LaunchPlan.SubstitutePaths(s.Args, s)));
  }

  // ---------- %NAME% 展开 ----------

  // 反例（这条防的）：ProcessStartInfo.Environment 不展开 %PATH%，写成 PATH=...;%PATH% 会把
  // 字面量 "%PATH%" 塞进子进程 —— CUDA 的 cudart64_13.dll 就再也找不到了（且不报错，只是加载失败）。
  [Fact] public void ExpandVars_ResolvesAgainstLookup() {
    var warn = new List<string>();
    var v = LaunchPlan.ExpandVars(@"%BASE%\bin;%PATH%", n => n == "BASE" ? @"G:\cuda" : n == "PATH" ? @"C:\Windows" : null, warn);
    Assert.Equal(@"G:\cuda\bin;C:\Windows", v);
    Assert.Empty(warn);
  }

  [Fact] public void ExpandVars_UnknownKeptVerbatim_AndWarns() {
    var warn = new List<string>();
    Assert.Equal("keep %NOPE% here", LaunchPlan.ExpandVars("keep %NOPE% here", _ => null, warn));
    Assert.Single(warn);
    Assert.Contains("NOPE", warn[0]);
  }

  [Fact] public void ExpandVars_EscapesAndLonePercent() {
    Assert.Equal("100%", LaunchPlan.ExpandVars("100%%", _ => "x"));   // %% ⇒ 一个字面 %
    Assert.Equal("a%",   LaunchPlan.ExpandVars("a%", _ => "x"));      // 落单的 % 原样
  }

  [Fact] public void ParseEnv_ExpandsValues_UsingInjectedLookup() {
    var d = new Dictionary<string, string>();
    LaunchPlan.ParseEnv("PATH=%CUDABIN%\\bin;%PATH%", d, null, n => n == "CUDABIN" ? @"G:\cuda132" : n == "PATH" ? @"C:\Windows" : null);
    Assert.Equal(@"G:\cuda132\bin;C:\Windows", d["PATH"]);
  }

  // ---------- 环境串解析 ----------

  // 同一块内「后写的能看到先写的」：查找顺序 = 本块已定义项 → 注入 lookup。
  // 为什么值得钉：托盘里 kvmem 那条服务就靠它写 `CUDA_HOME=...` 再 `PATH=%CUDA_HOME%\bin;...`。
  // 若哪天把顺序反过来（先查进程环境），%CUDA_HOME% 会解析不到 ⇒ 原样保留字面量 ⇒ CUDA 运行时 DLL 找不到，
  // 而子进程只是**加载失败**、不报错，是最难定位的一类。
  [Fact] public void ParseEnv_SeesEarlierEntriesInSameBlock() {
    var d = new Dictionary<string, string>();
    LaunchPlan.ParseEnv("CUDA_HOME=G:\\cuda132\nPATH=%CUDA_HOME%\\bin;%WIN%",
                        d, null, n => n == "WIN" ? @"C:\Windows" : null);
    Assert.Equal(@"G:\cuda132\bin;C:\Windows", d["PATH"]);
  }

  // 托盘里 kvmem IQ3 那条服务的 Env **原文形状**（路径换成占位）：一次同时压两级查找 ——
  // `PATH` 引用**同一块内**先定义的 `CUDA_HOME`（顺序有意义），末尾 `%PATH%` 再落到**进程环境**。
  // 这是新配置实际依赖的唯一语义，钉住它 = 钉住"改配置时不会把 CUDA 运行时目录弄丢"。
  [Fact] public void ParseEnv_KvmemServiceEnvShape_ExpandsBothLookupLevels() {
    var d = new Dictionary<string, string>();
    LaunchPlan.ParseEnv(
      "CUDA_VISIBLE_DEVICES=0\nCUDA_DEVICE_ORDER=PCI_BUS_ID\nKVMEM_VISION_DEVICE=gpu\n"
      + "CUDA_HOME=G:\\cuda132\nPATH=%CUDA_HOME%\\bin;%CUDA_HOME%\\bin\\x64;%PATH%",
      d, null, n => n == "PATH" ? @"C:\Windows" : null);
    Assert.Equal("0", d["CUDA_VISIBLE_DEVICES"]);
    Assert.Equal("gpu", d["KVMEM_VISION_DEVICE"]);
    Assert.Equal(@"G:\cuda132\bin;G:\cuda132\bin\x64;C:\Windows", d["PATH"]);
  }

  // 一行一项、按换行分隔，空行跳过
  [Fact] public void ParseEnv_NewlineSeparated_AndSkipsEmpty() {
    var d = new Dictionary<string, string>();
    LaunchPlan.ParseEnv("A=1\nB=2\n\n C=3 \n", d);
    Assert.Equal(3, d.Count);
    Assert.Equal("1", d["A"]);
    Assert.Equal("3", d["C"]);
  }

  // 反例（这条防的）：若拿分号当分隔符，`PATH=...;%PATH%` 会被劈成两项，
  // 前一项成了 "PATH=...;"、后一项连 '=' 都没有 ⇒ 用户"写了 PATH 却没生效"。
  // 分号是 Windows PATH 值**本身**的合法内容，必须原样保留在值里。
  [Fact] public void ParseEnv_SemicolonIsLiteralInsideValue() {
    var d = new Dictionary<string, string>();
    LaunchPlan.ParseEnv(@"PATH=C:\a;C:\b;%PATH%", d, null, n => n == "PATH" ? @"C:\Windows" : null);
    Assert.Single(d);
    Assert.Equal(@"C:\a;C:\b;C:\Windows", d["PATH"]);
  }

  // 缺 '=' 一类输入静默丢弃**不行** —— 用户写了却没生效，必须能从日志查出来
  [Fact] public void ParseEnv_MissingEquals_WarnsAndSkips() {
    var d = new Dictionary<string, string>(); var warn = new List<string>();
    LaunchPlan.ParseEnv("A=1\nNOVALUE\nB=2", d, warn);
    Assert.Equal(2, d.Count);
    Assert.Single(warn);
    Assert.Contains("NOVALUE", warn[0]);
  }

  [Fact] public void ParseEnv_ValueMayContainEquals() {
    var d = new Dictionary<string, string>();
    LaunchPlan.ParseEnv("OPTS=a=b", d);
    Assert.Equal("a=b", d["OPTS"]);
  }

  // ---------- 展示 ----------

  [Fact] public void CommandLine_And_EnvLine_AreStable() {
    var s = Spec(); s.Exe = @"C:\x\llama.exe"; s.Args = "-c 1024"; s.Env = "B=2\nA=1";
    var p = LaunchPlan.Compose(s, MultiCardBuild(), @"C:\llama\llama-server.exe");
    Assert.Equal(@"C:\x\llama.exe -c 1024", p.CommandLine());
    Assert.Equal("A=1 | B=2", p.EnvLine());              // key 排序 ⇒ 同一份配置每次渲染一致
  }
}
