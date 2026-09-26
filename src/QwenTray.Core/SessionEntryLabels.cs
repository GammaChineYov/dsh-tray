using System;

namespace QwenTray;

// 2026-09-12：会话入口文案的唯一源 —— 标签必须能预测行为（否则 = 执行鸿沟）。
//
// 现象（用户报）：没勾选「使用托盘内置渲染打开」，点会话却用内置渲染打开了。
// 事实（代码核对）：三条打开路径都受 builtinRender 门控，cfg 与勾选态也是自洽的
//   （OpenPop / OpenSessionById → 门控；--dump-menu 与 cfg 一致）。
// 真缺陷：开关 ON 时，子菜单项**仍叫**「启动 DSH 会话」却打开内置渲染；入口处也没有任何
//   「当前模式」提示 ⇒ 用户无法从标签预判行为，只能先点坏一次才知道。这正是执行鸿沟。
//
// 修法（不动行为，只让标签说真话）：勾选后给「启动 DSH 会话」「最近会话」补「（内置渲染）」后缀，
//   并把 openDsh 的 tooltip 换成该模式下的真实行为。文案与后缀收在这里，改一处即可，且可直接单测。
public static class SessionEntryLabels {
  /// <summary>子菜单项基础名；官方 Web UI 与内置渲染两种模式共用（模式靠后缀区分）。</summary>
  public const string OpenDsh = "启动 DSH 会话";
  /// <summary>「最近会话」分组标题基础名。</summary>
  public const string RecentHeader = "最近会话";
  /// <summary>builtinRender 勾选时的文案后缀：一眼看出这一项会走内置渲染。</summary>
  public const string BuiltinSuffix = "（内置渲染）";

  /// <summary>「启动 DSH 会话」项文案（随开关变）。</summary>
  public static string OpenDshText(bool builtinRender) => OpenDsh + (builtinRender ? BuiltinSuffix : string.Empty);

  /// <summary>「最近会话」标题文案（随开关变：标题下每一项点击都走同一模式）。</summary>
  public static string RecentHeaderText(bool builtinRender) => RecentHeader + (builtinRender ? BuiltinSuffix : string.Empty);

  /// <summary>「启动内置对话」项基础名：始终走托盘内置瘦客户端，**不受内置渲染开关影响**。</summary>
  /// 2026-09-26 起这两个会话入口都上移到了「当前模型」组（作用于当前选中模型），
  /// 这条文案因此需要一个归属 Core 的落点，避免又出现"同一句话散在两个文件里"。
  public const string OpenBuiltin = "启动内置对话";

  /// <summary>「启动内置对话」项文案：与内置渲染开关无关（开关只影响「启动 DSH 会话」那条）。</summary>
  public static string OpenBuiltinText() => OpenBuiltin;

  /// <summary>「启动 DSH 会话」项 tooltip：按当前模式描述**真实行为**，并给出切回另一种模式的路径。</summary>
  public static string OpenDshTooltip(bool builtinRender) => builtinRender
    ? "当前「使用托盘内置渲染打开」已勾选 ⇒ 实际用托盘内置渲染（瘦客户端，不加载官方客户端 bundle）打开，并把 agent-default-model 指向本模型。要用官方 Web UI：先取消勾选。"
    : "用官方 DSH Web UI 打开会话，并把 agent-default-model 指向本模型（同步 ctx/视觉/maxTokens 到 settings.yaml，30s 后还原默认模型）。";

  /// <summary>开关项自身的 tooltip：把「切换它会改哪些入口的文案」讲清楚（用户学的是一次耦合规则）。</summary>
  public static string ToggleTooltip() =>
    "勾选后「启动 DSH 会话」与「最近会话」的入口文案会加上「（内置渲染）」（标签=真实行为），点击走托盘内置瘦客户端（本地页 + RPC 直连 3080，不加载官方客户端 bundle）；「启动内置对话」始终走内置渲染。官方 Web UI 因插件 bundle 崩溃白屏时用它续会话。";

  /// <summary>切换开关时写进日志窗口的回执行（即时反馈：菜单已经跟着变了）。</summary>
  public static string ToggleLogSuffix(bool builtinRender) =>
    "    菜单已同步: 各模型「" + OpenDsh + "」与「" + RecentHeader + "」的文案" + (builtinRender ? "加上" : "去掉")
    + "了「" + BuiltinSuffix + "」后缀";

  /// <summary>
  /// 日志行里「这次走哪条路」的前缀基名。**故意不等于**菜单项名：写这条日志的两个入口
  /// （「启动 DSH 会话」与「启动内置对话」）都会调 SyncSessionConfig，而开关 ON 时两者行为相同，
  /// 所以日志只记「实际走的路」、不记「点了哪一项」——用中性基名避免误导。
  /// </summary>
  public const string OpenLogBase = "打开会话";

  /// <summary>打开会话时写进日志的行动模式（事后追「这次到底走的哪条路」）。</summary>
  public static string OpenLogPrefix(bool builtinRender) =>
    OpenLogBase + (builtinRender ? "（内置渲染·瘦客户端）" : "（官方 Web UI）");
}
