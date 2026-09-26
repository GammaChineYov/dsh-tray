namespace QwenTray;

// ============================================================================
// 参数解析（2026-09-26）：「本模型的参数」与「托盘全局默认」合成**生效值**。
//
// 语义变更的背景：菜单里的参数面板（上下文 / KV / 缓存内存 / 切分 / GPU1 占比 / MTP /
// 参数组 / 监听 / GPU）原先是**启动时覆盖所有模型**的全局值 —— 改一处，四个模型下次
// 启动全都跟着变。用户要的是「**跟随当前选中的模型**」，且改完要**持久化到该模型的
// 配置**（dsh-tray-config.json 的服务项），而不是下次启动就没了的临时覆盖。
//
// 合成规则：**本模型覆盖优先，缺失的回落全局默认**（逐项独立，不是整组覆盖）。
//   为什么逐项：用户给 8082 调了 KV 类型，不代表他想把 8081 的上下文一起改掉；
//   整组覆盖会让"只动一项"变成"动了一片"，而且无法回退（全局值当场被顶掉）。
//   ⇒ 覆盖的项目逐个记进 Overrides，菜单据此显示"本模型覆盖了几项"。
//
// 为什么做成**纯函数结构**：解析结果要同时喂给 启动(Start) / 状态行渲染(ToView) /
//   配置行渲染(SvcLines.CfgLines) / 启动日志，四处必须**完全同值**；
//   散在各处各算一遍 ⇒ 日志说 192K、实际起的是 128K 这类"说一套做一套"。
//   纯 ⇒ 可以被 dotnet test 穷举（见 tests/QwenTray.Tests/SvcParamsTests.cs）。
// ============================================================================
public struct SvcParams {
  public int Ctx;             // -c
  public int KvMode;          // 0=默认 f16 1=q8_0 2=f16
  public int CacheRam;        // --cache-ram（MiB）
  public int SplitMode;       // 0=按层 layer 1=张量并行 tensor
  public int TsGpu1;          // 张量并行时 GPU1 占比%
  public int MtpLevel;        // 0=无 1..N=MTP（--spec-draft-n-max）
  public int ParamMode;       // 0=通用思考 1=编码思考 2=Instruct
  public bool BindAll;        // --host 0.0.0.0
  public string GpuCfg;        // GpuSelection.CfgString()；空/无 = 用全局（判定走 IsNullOrEmpty，不依赖 "" 初值）
  public int Overrides;       // 本模型覆盖了几项（0 = 全部走全局默认）

  // —— 逐项覆盖标记（2026-09-26）：Resolve 时与 Overrides 一起记 ——
  // 为什么不用 Overrides 反推：菜单「运行时配置」行只打印**覆盖了的项**（未覆盖项 = 参数面板
  // 里的全局默认，再打一遍是纯重复，用户 09-26 明确去掉）。逐项 bool 让 DescribeOverrides
  // 不必回头再摸 ServiceSpec（Resolve 是唯一知道"哪项被覆盖"的地方，就在这里记下来）。
  public bool OvCtx, OvKv, OvCacheRam, OvSplit, OvTs, OvMtp, OvParam, OvBind;
  // ⚠️ OvGpu 必须是 Resolve 记下的覆盖标记，不能写成 `GpuCfg.Length>0`：
  //    GpuCfg 存的是**生效值**（未覆盖时=全局串，非空），拿它判覆盖会把全局默认误报成覆盖。
  public bool OvGpu;

  /// <summary>本模型的 GPU 选择：覆盖串为空则回落全局。</summary>
  public GpuSelection GpuOrDefault(GpuSelection global)
    => string.IsNullOrEmpty(GpuCfg) ? global : GpuSelection.FromCfg(GpuCfg);

  /// <summary>逐项合成。global* 一组 = 托盘全局默认（dsh-tray.cfg 读出来的值）。</summary>
  public static SvcParams Resolve(ServiceSpec sp, int gCtx, int gKv, int gCacheRam, int gSplit,
                                  int gTs, int gMtp, int gParam, bool gBind, string gGpu) {    var p = new SvcParams();
    p.Ctx       = sp.Ctx       ?? gCtx;
    p.KvMode    = sp.KvMode    ?? gKv;
    p.CacheRam  = sp.CacheRam  ?? gCacheRam;
    p.SplitMode = sp.SplitMode ?? gSplit;
    p.TsGpu1    = sp.TsGpu1    ?? gTs;
    p.MtpLevel  = sp.MtpLevel  ?? gMtp;
    p.ParamMode = sp.ParamMode ?? gParam;
    p.BindAll   = sp.BindAll   ?? gBind;
    p.GpuCfg    = string.IsNullOrEmpty(sp.GpuSel) ? (gGpu ?? "") : sp.GpuSel;
    p.OvCtx=sp.Ctx.HasValue; p.OvKv=sp.KvMode.HasValue; p.OvCacheRam=sp.CacheRam.HasValue;
    p.OvSplit=sp.SplitMode.HasValue; p.OvTs=sp.TsGpu1.HasValue; p.OvMtp=sp.MtpLevel.HasValue;
    p.OvParam=sp.ParamMode.HasValue; p.OvBind=sp.BindAll.HasValue;
    p.OvGpu=sp.GpuSel.Length>0;
    p.Overrides = (sp.Ctx.HasValue?1:0) + (sp.KvMode.HasValue?1:0) + (sp.CacheRam.HasValue?1:0)
                + (sp.SplitMode.HasValue?1:0) + (sp.TsGpu1.HasValue?1:0) + (sp.MtpLevel.HasValue?1:0)
                + (sp.ParamMode.HasValue?1:0) + (sp.BindAll.HasValue?1:0) + (sp.GpuSel.Length>0?1:0);
    return p;
  }

  /// <summary>一句话概述（用于菜单「运行时配置」行与启动日志）。</summary>
  public string Describe() {
    return "ctx=" + (Ctx / 1024) + "K | KV=" + SvcLines.KvLabel(KvMode) + " | 缓存内存=" + SvcLines.CacheRamLabel(CacheRam)
         + " | 切分=" + (SplitMode == 0 ? "按层 layer" : "张量并行 " + TsGpu1 + "%")
         + " | MTP=" + SvcLines.MtpLabel(MtpLevel) + " | 监听=" + (BindAll ? "0.0.0.0" : "127.0.0.1");
  }

  /// <summary>
  /// 只描述**本模型覆盖了的项**（2026-09-26）：菜单「运行时配置」行专用。
  /// 未覆盖项的生效值 = 托盘全局默认 = 参数面板里已经逐项显示的值，再打一遍是纯重复
  /// （用户 09-26 圈注指出）。全没覆盖时返回 ""，由调用方决定显示什么。
  /// 启动日志仍用 Describe()（日志要完整生效值，与面板无重复问题）。
  /// globalGpu = 托盘全局 GPU 选择，仅在 OvGpu 时被 GpuOrDefault 用到（取覆盖值）。
  /// </summary>
  public string DescribeOverrides(GpuSelection globalGpu) {
    var parts = new List<string>(9);
    if (OvCtx)      parts.Add("ctx=" + (Ctx / 1024) + "K");
    if (OvKv)       parts.Add("KV=" + SvcLines.KvLabel(KvMode));
    if (OvCacheRam) parts.Add("缓存内存=" + SvcLines.CacheRamLabel(CacheRam));
    if (OvSplit)    parts.Add("切分=" + (SplitMode == 0 ? "按层 layer" : "张量并行 " + TsGpu1 + "%"));
    if (OvTs && SplitMode != 0) parts.Add("GPU1占比=" + TsGpu1 + "%");   // 按层切分时 ts 不生效，打了误导
    if (OvMtp)      parts.Add("MTP=" + SvcLines.MtpLabel(MtpLevel));
    if (OvParam)    parts.Add("参数组=" + SvcLines.ParamLabel(ParamMode));
    if (OvBind)     parts.Add("监听=" + (BindAll ? "0.0.0.0" : "127.0.0.1"));
    if (OvGpu)      parts.Add("GPU=" + GpuOrDefault(globalGpu).ShortLabel());
    return string.Join(" · ", parts);
  }

  /// <summary>
  /// 与托盘默认基线做**值级**差异（2026-09-26）。
  /// 背景：「复制生成模型配置」落地后，每个模型的 9 项覆盖字段都会非空 —— 旧的"非 null=覆盖"
  /// 口径（Overrides / Ov* / DescribeOverrides）从此失去判别力：人人都是 9 项。
  /// 菜单要显示的是"这个模型的配置**不同在哪**"，所以改成与全局基线逐项比**值**。
  /// 返回 (差异数, 差异描述)；文本为 "ctx=32K · KV=16bit f16" 形态，空串 = 与默认完全一致。
  /// global* 传托盘当前全局基线（dsh-tray.cfg 值），gGpu 为全局 GPU 选择。
  /// </summary>
  public (int Count, string Text) DiffVs(int gCtx, int gKv, int gCacheRam, int gSplit,
                                         int gTs, int gMtp, int gParam, bool gBind, GpuSelection gGpu) {
    var parts = new List<string>(9);
    if (Ctx != gCtx)          parts.Add("ctx=" + (Ctx / 1024) + "K");
    if (KvMode != gKv)        parts.Add("KV=" + SvcLines.KvLabel(KvMode));
    if (CacheRam != gCacheRam) parts.Add("缓存内存=" + SvcLines.CacheRamLabel(CacheRam));
    if (SplitMode != gSplit || (SplitMode != 0 && TsGpu1 != gTs))
      parts.Add("切分=" + (SplitMode == 0 ? "按层 layer" : "张量并行 " + TsGpu1 + "%"));
    if (MtpLevel != gMtp)     parts.Add("MTP=" + SvcLines.MtpLabel(MtpLevel));
    if (ParamMode != gParam)  parts.Add("参数组=" + SvcLines.ParamLabel(ParamMode));
    if (BindAll != gBind)     parts.Add("监听=" + (BindAll ? "0.0.0.0" : "127.0.0.1"));
    var g = GpuOrDefault(gGpu);
    if (g.CfgString() != gGpu.CfgString()) parts.Add("GPU=" + g.ShortLabel());
    return (parts.Count, string.Join(" · ", parts));
  }
}
