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

  // 历史遗留：2026-09-12 对全项目做了一次读写点核查（grep `\.Ctx\b`），确认它
  // **无任何读写点** —— 既不是通过配置进入的，也没有被 LaunchArgs / 界面用过。
  // 依「宁留注释不删」的惯例留在这里（而不是随切分丢掉）：将来若要支持
  // 「每个模型各自的默认 ctx」，这就是现成的落点。
  public int Ctx;

  // 从配置文件构造。把「Spec 从哪来」的知识收在 Spec 自己身上，
  // 而不是散落在 TrayApp 的构造循环里（S3 搬 Core 时这条转换路径也要一起搬）。
  // 语义与拆分前的 TrayApp.cs 内联代码逐字等价：Batch/Ubatch 仅在 >0 时覆盖。
  public static ServiceSpec From(ServiceConfig sc) {
    var s = new ServiceSpec {
      Name = sc.Name, Port = sc.Port, Model = sc.Model,
      UseMmproj = sc.UseMmproj, Mmproj = sc.Mmproj,
      SpecDecode = sc.SpecDecode, Provider = sc.Provider,
    };
    if (sc.Batch > 0) s.Batch = sc.Batch;
    if (sc.Ubatch > 0) s.Ubatch = sc.Ubatch;
    return s;
  }
}
