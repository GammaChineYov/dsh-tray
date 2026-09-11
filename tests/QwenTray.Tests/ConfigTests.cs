namespace QwenTray.Tests;

// S4（2026-09-12）：配置解析与空值回填。
//
// 这段逻辑原本锁在 Config.Load() 的 IO 里（读文件 + 解析 + 回填 + 失败时写盘），根本测不到。
// S4 把它抽成 MergeFrom/Merge 两个纯函数 ⇒ 现在可以穷举边界。
//
// ⚠️ 这些测试**绝不调用 Config.Load()** —— 它有写盘副作用：读不到/解析失败时会把默认配置
//    写回 Config.Path_（= 进程 BaseDirectory 下）。在测试进程里调它会往 bin/ 里丢垃圾文件。
//    "只读解析"这个需求本身也是本项目的一个坑（见 dsh-tray-dev skill：重读配置必须只读解析）。
//    MergeFrom 正是那个干净的只读入口 —— 但它不处理 IO，IO 仍在 Load() 里。
public class ConfigTests {

  [Fact] public void Default_HasTheThreeDocumentedServices() {
    var c = Config.Default();
    Assert.Equal(3, c.Services.Count);
    Assert.Equal(8081, c.Services[0].Port);
    Assert.Equal(8082, c.Services[1].Port);
    Assert.Equal(8083, c.Services[2].Port);
    Assert.All(c.Services, s => Assert.True(s.Enabled));
  }

  [Theory]
  [InlineData((string?)null)]
  [InlineData("")]
  [InlineData("   ")]                          // 非空但解析必炸
  [InlineData("not json at all")]
  [InlineData("{")]                            // 截断的 JSON
  [InlineData("{}")]                           // 能解析但没有 Services
  [InlineData("{\"Services\":[]}")]            // Services 为空数组
  [InlineData("[]")]                           // 根本不是对象
  public void MergeFrom_UnusableText_ReturnsNull(string? json) {
    Assert.Null(Config.MergeFrom(json));
  }

  [Fact] public void MergeFrom_FillsBlankStringsFromDefaults() {
    var c = Config.MergeFrom("{\"Services\":[{\"Name\":\"x\",\"Port\":8081}]}");
    Assert.NotNull(c);
    var d = Config.Default();
    Assert.Equal(d.LlamaServerExe,        c!.LlamaServerExe);
    Assert.Equal(d.DshUrl,                c.DshUrl);
    Assert.Equal(d.SettingsYamlPath,      c.SettingsYamlPath);
    Assert.Equal(d.OfficialDeepSeekUrl,   c.OfficialDeepSeekUrl);
  }

  [Fact] public void MergeFrom_KeepsExplicitValues() {
    var c = Config.MergeFrom(
      "{\"LlamaServerExe\":\"D:\\\\llama\\\\llama-server.exe\"," +
      "\"DshUrl\":\"http://127.0.0.1:9999/\"," +
      "\"Services\":[{\"Name\":\"x\",\"Port\":8081}]}");
    Assert.Equal(@"D:\llama\llama-server.exe", c!.LlamaServerExe);
    Assert.Equal("http://127.0.0.1:9999/",      c.DshUrl);
  }

  // 记录**有意**行为：Dsh* 系列字段不回填默认值。
  // 原因：留空 = 托盘菜单只提示配置；补上默认值会让"DshHomeDir 在本机非默认时必须填真实值"
  // 这个有意留空失效。（Default() 里这几个字段本身就是空串，所以等价于"不回填"。）
  [Fact] public void MergeFrom_DoesNotBackfillDshFields() {
    var c = Config.MergeFrom("{\"Services\":[{\"Name\":\"x\",\"Port\":8081}]}");
    Assert.Equal("", c!.DshNodeExe);
    Assert.Equal("", c.DshCliBinJs);
    Assert.Equal("", c.DshWorkDir);
    Assert.Equal("", c.DshWebToken);
  }

  [Fact] public void MergeFrom_RoundTripsServiceFields() {
    var c = Config.MergeFrom(
      "{\"Services\":[{\"Name\":\"qwen\",\"Port\":8082,\"Model\":\"m.gguf\"," +
      "\"SpecDecode\":true,\"Enabled\":false,\"Provider\":\"p\"}]}");
    Assert.Single(c!.Services);
    var s = c.Services[0];
    Assert.Equal("qwen",   s.Name);
    Assert.Equal(8082,     s.Port);
    Assert.Equal("m.gguf", s.Model);
    Assert.True(s.SpecDecode);
    Assert.False(s.Enabled);
    Assert.Equal("p", s.Provider);
  }

  // Merge 是纯函数：不该改动入参以外的状态，也不该被 Default() 的默认值反向污染
  [Fact] public void Merge_OnlyTouchesTheFourStringFields() {
    var c = new AppConfig { LlamaServerExe = "", DshUrl = "http://x/" };
    c.Services.Add(new ServiceConfig { Name = "kept", Port = 7777 });
    var m = Config.Merge(c);
    Assert.Same(c, m);                                  // 原地改，不复制
    Assert.Equal("kept", m.Services[0].Name);           // Services 未被替换成默认的 3 条
    Assert.Single(m.Services);
    Assert.Equal("http://x/", m.DshUrl);                // 有值不动
    Assert.NotEqual("", m.LlamaServerExe);              // 空值被补
  }
}
