namespace QwenTray;

// ============================================================================
// S2（2026-09-12）：「配置描述」从运行时状态袋里切出来。
//
// 要解决的问题（治理报告 P1-6「分层倒置」）：
//   LaunchArgs.Build 与 PerfRuntime.OnStart/OnReady/OnFail/OnLlamaLine 只读
//   Name / Port / Model / UseMmproj / Mmproj / Batch / Ubatch / SpecDecode / Provider
//   这些**配置**字段，签名却声明成接受 `Service` —— 而 `Service` 同时还装着
//   Process / LogSink / 「启动中」标志等**运行时**状态。两个纯逻辑模块因此被迫
//   依赖一个带着 IO 句柄的状态袋，S3 也就无法把它们搬进不引用 WinForms 的 Core 工程。
//
// 切法：
//   ServiceSpec = 配置（本文件；不引用 System.Diagnostics.Process / WinForms）
//   Service     = 组合 ServiceSpec + 运行时状态（见 Service.cs，转发属性保旧访问点零改动）
//   ⇒ 改完之后，LaunchArgs 与 PerfRuntime 的签名里**不再出现 Service**。
//
// 为什么用组合而不是继承（`class Service : ServiceSpec`）：
//   Service「has-a 配置」，不是「is-a 配置」。继承能省掉 Service.cs 里那 10 行转发属性，
//   但会把「运行时对象是一种配置」这个错误语义固化下来（未来若给 Spec 加不可变/相等性
//   约束，Service 会被迫一起继承）。11 行转发属性的代价，换一个不会反噬的边界，划算。
//
// 本文件刻意不写 using：csproj 开了 ImplicitUsings，且 ServiceConfig 同命名空间。
// ============================================================================
public class ServiceSpec {
  public string Name = "";
  public int Port;
  public string Model = "";
  public bool UseMmproj = false;
  public string Mmproj = "";
  public int Batch;
  public int Ubatch;
  public bool SpecDecode = false;
  public string Provider = "";   // DSH provider 名（打开会话时写入 agent-default-model）

  // —— per-service 覆盖（2026-09-20，对应 ServiceConfig 的同名三项）——
  // 默认空 ⇒ 走全局 exe 与 LaunchArgs.Build 的内置模板 = 改动前的行为。
  // 合成规则（谁覆盖谁、自定义参数时环境变量怎么算）**全部收在 LaunchPlan 一处**，
  // 本结构只负责"承载用户写了什么"，不判断怎么用。
  public string Exe = "";
  public string Args = "";
  public string Env = "";

  // —— 参数型覆盖（2026-09-26；与 ServiceConfig 同名字段一一对应）——
  // null = 未设置 ⇒ 走全局默认；非 null = 本模型自己的值（启动优先用它）。解析在 SvcParams.Resolve。
  // ⚠️ 下面这一组取代了原先那枚 `public int Ctx;`（同名冲突，已合并为一处）：
  //    2026-09-12 全项目读写点核查（`grep \.Ctx\b`）确认旧字段**零读写点** —— 既不是通过配置
  //    进入的，也没被 LaunchArgs / 界面用过，纯历史遗留。合并到这里的语义更准：将来若要支持
  //    「每个模型各自的默认 ctx」，这就是现成的落点（旧注释的原意完整保留）。
  public int? Ctx;
  public int? KvMode;
  public int? CacheRam;
  public int? SplitMode;
  public int? TsGpu1;
  public int? MtpLevel;
  public int? ParamMode;
  public bool? BindAll;
  public string GpuSel = "";

  // 从配置文件构造。把「Spec 从哪来」的知识收在 Spec 自己身上，
  // 而不是散落在 TrayApp 的构造循环里（S3 搬 Core 时这条转换路径也要一起搬）。
  // 语义与拆分前的 TrayApp.cs 内联代码逐字等价：Batch/Ubatch 仅在 >0 时覆盖。
  public static ServiceSpec From(ServiceConfig sc) {
    var s = new ServiceSpec {
      Name = sc.Name, Port = sc.Port, Model = sc.Model,
      UseMmproj = sc.UseMmproj, Mmproj = sc.Mmproj,
      SpecDecode = sc.SpecDecode, Provider = sc.Provider,
      Exe = sc.Exe, Args = sc.Args, Env = sc.Env,
      Ctx = sc.Ctx, KvMode = sc.KvMode, CacheRam = sc.CacheRam, SplitMode = sc.SplitMode,
      TsGpu1 = sc.TsGpu1, MtpLevel = sc.MtpLevel, ParamMode = sc.ParamMode,
      BindAll = sc.BindAll, GpuSel = sc.GpuSel,
    };
    if (sc.Batch > 0) s.Batch = sc.Batch;
    if (sc.Ubatch > 0) s.Ubatch = sc.Ubatch;
    return s;
  }
}
