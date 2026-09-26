using System;
using Xunit;

namespace QwenTray.Tests;

// 2026-09-12：「会话入口文案必须能预测行为」的回归 —— 用户报「没勾选内置渲染，点会话却走了内置渲染」。
//
// 门控（OpenPop / OpenSessionById）本身是对的、且 cfg 与勾选态自洽，所以修的不是行为而是**标签**：
// 开关 ON 时入口文案必须自证「这一项会走内置渲染」。这里锁住的正是那条耦合，防止以后有人只改文案
// 或只改门控，把「标签说谎」重新引回来。
public class SessionEntryLabelsTests {

  [Fact]
  public void Off_KeepsBareLabels() {
    Assert.Equal("启动 DSH 会话", SessionEntryLabels.OpenDshText(false));
    Assert.Equal("最近会话", SessionEntryLabels.RecentHeaderText(false));
    Assert.DoesNotContain(SessionEntryLabels.BuiltinSuffix, SessionEntryLabels.OpenDshText(false));
    Assert.DoesNotContain(SessionEntryLabels.BuiltinSuffix, SessionEntryLabels.RecentHeaderText(false));
  }

  [Fact]
  public void On_TwoGatedEntriesCarryTheSuffix() {
    Assert.Equal("启动 DSH 会话（内置渲染）", SessionEntryLabels.OpenDshText(true));
    Assert.Equal("最近会话（内置渲染）", SessionEntryLabels.RecentHeaderText(true));
    Assert.EndsWith(SessionEntryLabels.BuiltinSuffix, SessionEntryLabels.OpenDshText(true));
    Assert.EndsWith(SessionEntryLabels.BuiltinSuffix, SessionEntryLabels.RecentHeaderText(true));
  }

  [Fact]
  public void SuffixedLabelStillStartsWithItsBaseName() {
    // 只加后缀、不改基名：任何按「基名」找项/排版的代码与用户记忆都继续成立。
    Assert.StartsWith(SessionEntryLabels.OpenDsh, SessionEntryLabels.OpenDshText(true));
    Assert.StartsWith(SessionEntryLabels.RecentHeader, SessionEntryLabels.RecentHeaderText(true));
  }

  [Fact]
  public void OpenDshTooltip_DescribesTheEffectiveMode() {
    Assert.Contains("官方 DSH Web UI", SessionEntryLabels.OpenDshTooltip(false));
    Assert.DoesNotContain("实际用托盘内置渲染", SessionEntryLabels.OpenDshTooltip(false));

    var on = SessionEntryLabels.OpenDshTooltip(true);
    Assert.Contains("实际用托盘内置渲染", on);
    // ON 时必须给出「怎么切回官方 UI」的路，否则用户只能猜。
    Assert.Contains("先取消勾选", on);
  }

  [Fact]
  public void ToggleTooltip_TeachesWhichEntriesItMoves() {
    var tip = SessionEntryLabels.ToggleTooltip();
    Assert.Contains(SessionEntryLabels.OpenDsh, tip);
    Assert.Contains(SessionEntryLabels.RecentHeader, tip);
    Assert.Contains(SessionEntryLabels.BuiltinSuffix, tip);
    // 「启动内置对话」不受开关影响 —— 免得用户以为勾选会改它。
    Assert.Contains("「启动内置对话」始终走内置渲染", tip);
  }

  [Fact]
  public void ToggleLog_ReportsWhichWayTheLabelsMoved() {
    Assert.Contains("加上", SessionEntryLabels.ToggleLogSuffix(true));
    Assert.DoesNotContain("去掉", SessionEntryLabels.ToggleLogSuffix(true));
    Assert.Contains("去掉", SessionEntryLabels.ToggleLogSuffix(false));
    Assert.Contains(SessionEntryLabels.BuiltinSuffix, SessionEntryLabels.ToggleLogSuffix(true));
  }

  [Fact]
  public void OpenLogPrefix_NamesThePathActuallyTaken() {
    // 日志基名刻意中性（两个入口开关 ON 时行为相同，日志只记「实际走的路」而不是「点了哪一项」）。
    Assert.StartsWith(SessionEntryLabels.OpenLogBase, SessionEntryLabels.OpenLogPrefix(false));
    Assert.Contains("官方 Web UI", SessionEntryLabels.OpenLogPrefix(false));
    Assert.Contains("内置渲染", SessionEntryLabels.OpenLogPrefix(true));
    Assert.NotEqual(SessionEntryLabels.OpenLogPrefix(false), SessionEntryLabels.OpenLogPrefix(true));
  }
}
