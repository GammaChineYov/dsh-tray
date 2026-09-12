namespace QwenTray.Tests;

// S5（2026-09-12）：服务状态映射 —— 四态 × 三函数 **穷举**。
//
// 这三个函数原本是 TrayApp 的私有 static，只有 `--selftest-svcmenu` 能枚举到；而那个探针**不在**
// `dotnet test` 链上（要手动跑托盘自检才会发现回归）。搬进 Core 后它们可以直接被钉住。
//
// 为什么穷举 8 种组合而不是只测"真实会出现的 4 种"：
//   starting / running / busy 在真实运行中互斥（starting ⇒ !running，busy ⇒ !running && !starting），
//   但函数签名允许任意组合，而**菜单可用性是靠这三个布尔算出来的** —— 一旦有人在这里加了新的判定
//   分支，只测"合法输入"的用例不会红。S4 的教训是"断言要能拦下目标错因"，所以这里全 8 种组合都钉。
public class SvcStatusTests {

  // 黄=启动中、绿=运行中、橙=运行中(未托管)、红=未运行 —— 顺序即优先级，starting 最高
  [Theory]
  [InlineData(false, false, false, "未运行")]
  [InlineData(false, false, true,  "运行中(未托管)")]
  [InlineData(false, true,  false, "运行中")]
  [InlineData(false, true,  true,  "运行中")]
  [InlineData(true,  false, false, "启动中")]
  [InlineData(true,  false, true,  "启动中")]
  [InlineData(true,  true,  false, "启动中")]
  [InlineData(true,  true,  true,  "启动中")]
  public void State_IsExhaustive(bool starting, bool running, bool busy, string expect) {
    Assert.Equal(expect, SvcStatus.State(starting, running, busy));
  }

  // 一级项状态圆点色名（必须是 DotFor 认识的颜色名；这里只钉映射，不碰位图缓存）
  [Theory]
  [InlineData(false, false, false, "red")]
  [InlineData(false, false, true,  "orange")]
  [InlineData(false, true,  false, "green")]
  [InlineData(false, true,  true,  "green")]
  [InlineData(true,  false, false, "yellow")]
  [InlineData(true,  false, true,  "yellow")]
  [InlineData(true,  true,  false, "yellow")]
  [InlineData(true,  true,  true,  "yellow")]
  public void Dot_IsExhaustive(bool starting, bool running, bool busy, string expect) {
    Assert.Equal(expect, SvcStatus.Dot(starting, running, busy));
  }

  // 启停项可用性：start 只在"没跑也没被占"时可点；stop 在跑/启动中/被占时都可点（后两者要能中断）；
  // restart 恒可点。注意 busy 是"端口上有 llama 但不是本托盘拉起的" —— 必须允许「停止模型」回收它，
  // 所以 stop 在 busy 时为 true 是**有意的**，不是漏判。
  [Theory]
  [InlineData(false, false, false, true,  false, true)]
  [InlineData(false, false, true,  false, true,  true)]
  [InlineData(false, true,  false, false, true,  true)]
  [InlineData(false, true,  true,  false, true,  true)]
  [InlineData(true,  false, false, false, true,  true)]
  [InlineData(true,  false, true,  false, true,  true)]
  [InlineData(true,  true,  false, false, true,  true)]
  [InlineData(true,  true,  true,  false, true,  true)]
  public void Enable_IsExhaustive(bool starting, bool running, bool busy,
                                  bool start, bool stop, bool restart) {
    var en = SvcStatus.Enable(starting, running, busy);
    Assert.Equal(start, en.start);
    Assert.Equal(stop, en.stop);
    Assert.Equal(restart, en.restart);
  }

  // 交叉约束（把"三函数描述同一状态"这件事本身钉住）：三者的优先级顺序必须一致 ——
  // 状态文本、圆点色、可用性不能各自为政（历史上它们分散在三处 if 里，正是这种漂移的来源）
  [Fact] public void ThreeMappings_AgreeOnPriority() {
    foreach (bool s in new[] { false, true })
      foreach (bool r in new[] { false, true })
        foreach (bool b in new[] { false, true }) {
          bool startingish = SvcStatus.State(s, r, b) == "启动中";
          Assert.Equal(startingish, SvcStatus.Dot(s, r, b) == "yellow");
          Assert.Equal(startingish, s);           // starting 永远压过其余两个标志
          // 完全空闲 ⇔ start 可点
          bool idle = SvcStatus.State(s, r, b) == "未运行";
          Assert.Equal(idle, SvcStatus.Enable(s, r, b).start);
          Assert.Equal(!idle, SvcStatus.Enable(s, r, b).stop);
        }
  }
}
