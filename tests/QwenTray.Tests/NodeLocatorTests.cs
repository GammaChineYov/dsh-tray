using System;
using System.IO;

namespace QwenTray.Tests;

// S5（2026-09-12）：NodeLocator —— 「托盘启动 DSH 失败」的根因修复。
//
// 被测场景全部是**文件系统事实**，所以用临时目录造一棵假的 versions 树，而不是 mock IO。
// 被测逻辑只读，测试自己负责清理自己创建的临时目录。
public class NodeLocatorTests : IDisposable {

  readonly string _root;

  public NodeLocatorTests() {
    _root = Path.Combine(Path.GetTempPath(), "nodeloc-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_root);
  }

  public void Dispose() {
    try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
  }

  // 造 <root>\versions\<ver>\node.exe；可选把该目录 mtime 固定成给定时间
  string MakeVersion(string ver, DateTime? mtimeUtc = null) {
    string d = Path.Combine(_root, "versions", ver);
    Directory.CreateDirectory(d);
    string exe = Path.Combine(d, "node.exe");
    File.WriteAllText(exe, "fake");
    if (mtimeUtc.HasValue) { try { Directory.SetLastWriteTimeUtc(d, mtimeUtc.Value); } catch { } }
    return exe;
  }

  void MakeCurrent(string ver) {
    Directory.CreateDirectory(Path.Combine(_root, "versions"));
    File.WriteAllText(Path.Combine(_root, "versions", "current"), ver);
  }

  string ConfiguredFor(string ver) { return Path.Combine(_root, "versions", ver, "node.exe"); }

  [Fact] public void ConfiguredExists_IsUsedVerbatim() {
    string exe = MakeVersion("22.22.2-3");
    string got = NodeLocator.Resolve(exe, out string note);
    Assert.Equal(exe, got);
    Assert.Equal("", note);        // 配置有效时不该产生任何"回退"提示
  }

  [Fact] public void ConfigPathRotatedAway_FallsBackToCurrentPointer() {
    // 配置还指着被回收的 22.22.2-2；盘上只剩 22.22.2-3，且 current 指向它
    string alive = MakeVersion("22.22.2-3");
    MakeCurrent("22.22.2-3");

    string got = NodeLocator.Resolve(ConfiguredFor("22.22.2-2"), out string note);

    Assert.Equal(alive, got);
    Assert.Contains("current", note);
    Assert.Contains("22.22.2-3", note);
  }

  [Fact] public void NoCurrentPointer_FallsBackToNewestVersionDir() {
    // 没有 current 文件（或它指向的目录已不存在）⇒ 按 mtime 取最新
    MakeVersion("22.22.2-2", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
    string newer = MakeVersion("22.22.2-3", new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc));

    string got = NodeLocator.Resolve(ConfiguredFor("22.22.2-9"), out string note);

    Assert.Equal(newer, got);
    Assert.Contains("最新版本目录", note);
  }

  [Fact] public void CurrentPointsAtMissingDir_StillFallsBackToNewestExisting() {
    MakeVersion("22.22.2-3", new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc));
    MakeCurrent("22.22.2-99");                 // 指针悬空

    string got = NodeLocator.Resolve(ConfiguredFor("22.22.2-2"), out string note);

    Assert.Equal(ConfiguredFor("22.22.2-3"), got);
    Assert.Contains("最新版本目录", note);
  }

  [Fact] public void NothingUsable_ReturnsOriginalSoErrorStaysMeaningful() {
    // versions 树整个不存在（比如 DshNodeExe 指向的是完全无关的位置）
    string configured = Path.Combine(_root, "nope", "22.22.2-2", "node.exe");
    string got = NodeLocator.Resolve(configured, out string note);
    Assert.Equal(configured, got);             // 原值返回 ⇒ 上层照旧抛"找不到指定文件"，错误不撒谎
    Assert.NotEqual("", note);
  }

  [Fact] public void BlankConfigured_ReturnsEmptyWithoutThrowing() {
    string got = NodeLocator.Resolve("", out string note);
    Assert.Equal("", got);
    Assert.NotEqual("", note);
    Assert.Equal("", NodeLocator.Resolve(null, out _));   // 无 out 重载也不该炸
  }
}
