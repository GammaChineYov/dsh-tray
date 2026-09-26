namespace QwenTray.Tests;

// 2026-09-13：工作集主动回收的三条路径判定。
//
// 为什么值得钉住：这三条路径里**只有「空闲自动」是会自己动手的**，而它一旦判错（比如在有请求时
// 照样回收），代价是把正在使用的权重页推去 standby ⇒ 下一次推理整段重新缺页 ⇒ 用户看到的不是
// "省了内存"，而是"模型突然变卡"。所以这里把「有请求在跑 ⇒ 一律不自动回收」做成显式负向断言。
public class MemTrimTests {

  // --- 空闲路径 ---

  [Fact] public void Idle_Disabled_NeverAllows() {
    Assert.False(MemTrim.AllowIdle(idleEnabled: false, anyProcessing: false, sinceLastTrimMs: -1, intervalSec: 1800));
  }

  // 🔴 本文件最重要的断言：有请求在处理时，自动回收必须被拦下（无论开关/间隔怎么配）
  [Fact] public void Idle_ProcessingInFlight_NeverAllows() {
    Assert.False(MemTrim.AllowIdle(true, anyProcessing: true, sinceLastTrimMs: -1,  intervalSec: 60));
    Assert.False(MemTrim.AllowIdle(true, anyProcessing: true, sinceLastTrimMs: 99999999, intervalSec: 60));
  }

  [Fact] public void Idle_NeverTrimmedBefore_Allows() {
    Assert.True(MemTrim.AllowIdle(true, false, sinceLastTrimMs: -1, intervalSec: 1800));
  }

  [Fact] public void Idle_IntervalBoundary() {
    Assert.False(MemTrim.AllowIdle(true, false, 1800_000 - 1, 1800));   // 差 1ms ⇒ 不到点
    Assert.True (MemTrim.AllowIdle(true, false, 1800_000,     1800));   // 正好到点 ⇒ 放行
  }

  // --- 就绪路径 ---

  [Fact] public void OnReady_FollowsSwitch() {
    Assert.True (MemTrim.AllowOnReady(onReadyEnabled: true,  anyProcessing: false));
    Assert.False(MemTrim.AllowOnReady(onReadyEnabled: false, anyProcessing: false));
  }

  [Fact] public void OnReady_ProcessingInFlight_Blocked() {
    Assert.False(MemTrim.AllowOnReady(true, anyProcessing: true));
  }

  // --- 统一入口 ---

  [Fact] public void Manual_AlwaysAllowed() {
    // 手动即便在"有请求在跑"时也放行：点的人是知情的（并且这正是"没人能保证它松手"时的兜底手段）
    Assert.True(MemTrim.Allow(TrimKind.Manual, false, false, true, 0, 60));
    Assert.True(MemTrim.Allow(TrimKind.Manual, true,  true,  false, -1, 60));
  }

  [Fact] public void Allow_DispatchesPerKind() {
    // Idle 关 / OnReady 开：同样的入参下两条路径结论必须不同（防"统一入口把 kind 吃了"）
    Assert.True (MemTrim.Allow(TrimKind.OnReady, idleEnabled: false, onReadyEnabled: true,  anyProcessing: false, sinceLastTrimMs: 0, intervalSec: 1800));
    Assert.False(MemTrim.Allow(TrimKind.Idle,    idleEnabled: false, onReadyEnabled: true,  anyProcessing: false, sinceLastTrimMs: 0, intervalSec: 1800));
  }

  // --- 间隔夹取 ---

  [Theory]
  [InlineData(0,       60)]
  [InlineData(-5,      60)]
  [InlineData(59,      60)]
  [InlineData(60,      60)]
  [InlineData(1800,    1800)]
  [InlineData(86400,   86400)]
  [InlineData(999999,  86400)]
  public void ClampInterval_BoundsTheValue(int input, int expected) {
    Assert.Equal(expected, MemTrim.ClampInterval(input));
  }

  // 夹取真的作用在判定上：cfg 里手改成 1 秒也不会变成"每秒回收"
  [Fact] public void ClampInterval_IsAppliedInsideAllowIdle() {
    Assert.False(MemTrim.AllowIdle(true, false, sinceLastTrimMs: 5_000, intervalSec: 1));  // 1s 被夹成 60s
    Assert.True (MemTrim.AllowIdle(true, false, sinceLastTrimMs: 60_000, intervalSec: 1));
  }

  [Fact] public void IntervalLabel_CoversOptionsAndOverflow() {
    Assert.Equal("5分钟",   MemTrim.IntervalLabel(300));
    Assert.Equal("30分钟",  MemTrim.IntervalLabel(1800));
    Assert.Equal("1小时",   MemTrim.IntervalLabel(3600));
    Assert.Equal("2小时",   MemTrim.IntervalLabel(7200));
    Assert.Equal("仅手动",  MemTrim.IntervalLabel(86400));
    Assert.Equal("仅手动",  MemTrim.IntervalLabel(999999));   // 越界也回落到"仅手动"，不吐怪数字
  }

  // 菜单档位表：每个档位都必须是合法（可被夹取函数原样接受）⇒ 改表不会悄悄引入会被夹掉的值
  [Fact] public void IdleIntervalOptions_AreAllWithinBounds() {
    Assert.NotEmpty(MemTrim.IdleIntervalOptions);
    foreach (var v in MemTrim.IdleIntervalOptions) {
      Assert.InRange(v, MemTrim.MinIdleIntervalSec, MemTrim.MaxIdleIntervalSec);
      Assert.Equal(v, MemTrim.ClampInterval(v));
    }
  }

  // --- 文本 ---

  [Theory]
  [InlineData(0,               "0 B")]
  [InlineData(2048,            "2.0 KiB")]
  [InlineData(5 * 1048576L,    "5.0 MiB")]
  [InlineData(17_860_000_000L, "16.63 GiB")]
  public void Bytes_FormatsPerMagnitude(long v, string expected) {
    Assert.Equal(expected, MemTrim.Bytes(v));
  }

  [Fact] public void ResultLine_DistinguishesFailureFromNoOpFromSuccess() {
    // 三种结果必须能被区分 —— 否则用户无法判断"到底是没回收还是回收不动"
    Assert.Contains("回收失败", MemTrim.ResultLine(TrimKind.Manual, "m", ok: false, 1000, 0));
    Assert.Contains("已在最小", MemTrim.ResultLine(TrimKind.Idle,   "m", ok: true,  1000, 1000));
    var ok = MemTrim.ResultLine(TrimKind.OnReady, "Qwen3.8-27B", ok: true, 40_000_000_000L, 22_000_000_000L);
    Assert.Contains("启动后", ok);
    Assert.Contains("16.76 GiB", ok);          // 两者之差 18e9 B
    Assert.Contains("→", ok);                   // 前后都要给
  }

  [Fact] public void PolicyText_ReportsAllThreePaths() {
    var t = MemTrim.PolicyText(onReadyEnabled: true, idleEnabled: true, 1800);
    Assert.Contains("启动后回收=开", t);
    Assert.Contains("空闲自动回收=开（每 30分钟）", t);
    Assert.Contains("手动=随时可用", t);

    var off = MemTrim.PolicyText(false, false, 1800);
    Assert.Contains("启动后回收=关", off);
    Assert.Contains("空闲自动回收=关", off);
  }

  [Fact] public void KindLabel_IsHumanReadable() {
    Assert.Equal("手动",   MemTrim.KindLabel(TrimKind.Manual));
    Assert.Equal("启动后", MemTrim.KindLabel(TrimKind.OnReady));
    Assert.Equal("空闲",   MemTrim.KindLabel(TrimKind.Idle));
  }
}
