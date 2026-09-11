namespace QwenTray {
public class Service {
  // —— 配置（S2 2026-09-12：已切出为独立 ServiceSpec，见 ServiceSpec.cs）——
  // 下面 10 个转发属性存在的唯一目的：让既有 `svc.Name` / `svc.Model` 形式的访问点
  // **一行都不用改**就能继续编译。它们不是"设计"，是迁移期的兼容层；
  // JIT 会把转发内联掉，无性能损耗。
  // 新增代码请优先直接写 `svc.Spec.X`（明确区分"读配置"与"读运行时"）。
  public ServiceSpec Spec = new ServiceSpec();
  public string Name { get => Spec.Name; set => Spec.Name = value; }
  public int Port { get => Spec.Port; set => Spec.Port = value; }
  public string Model { get => Spec.Model; set => Spec.Model = value; }
  public bool UseMmproj { get => Spec.UseMmproj; set => Spec.UseMmproj = value; }
  public string Mmproj { get => Spec.Mmproj; set => Spec.Mmproj = value; }
  public int Ctx { get => Spec.Ctx; set => Spec.Ctx = value; }
  public int Batch { get => Spec.Batch; set => Spec.Batch = value; }
  public int Ubatch { get => Spec.Ubatch; set => Spec.Ubatch = value; }
  public bool SpecDecode { get => Spec.SpecDecode; set => Spec.SpecDecode = value; }
  public string Provider { get => Spec.Provider; set => Spec.Provider = value; }

  // —— 运行时状态 ——
  // S2 刻意**不动**这一块：把 ServiceRuntime 也独立出来对 S3（抽 Core）没有任何收益，
  // 而 S5 拆 ServiceManager 时本来就要重做它 —— 提前拆只是徒增一层间接。
  // 日志缓冲：S0 起由裸 StringBuilder 换成有界、线程安全的 LogSink（三写一读零同步 → P1-1；只增不减 → P1-2）
  // 调用点语义保持不变：log.Append/AppendLine 写入、log.Length 当单调游标、log.Read(游标) 增量取
  public System.Diagnostics.Process proc; public LogSink log = new LogSink(); public long lastLen=0;
  // —— 运行状态（2026-09-11：菜单状态圆点/启动中判定/服务端实测）——
  public volatile bool Starting=false;   // 已拉起进程、端口尚未就绪（Tick 里 /health 探活，就绪后清掉）
  public int startMs=0;                  // Environment.TickCount（仅用于算启动耗时）
  public volatile int runCtx=0;          // /props 实测 n_ctx（0=未探测）
  public volatile int runVision=-1;      // /props 实测 modalities.vision：-1=未探测 0=不支持 1=支持
  public volatile bool PortBusy=false;   // 端口上有 llama 在服务但 tray 未托管（外部/上一实例启动）→ 允许「停止模型」按端口兜底回收
  public bool Running { get { return proc!=null && !proc.HasExited; } }
  public string State { get { return Starting ? "启动中" : Running ? "运行" : "停止"; } }
  public string RamGb { get { try { if(Running&&proc!=null) return (proc.WorkingSet64/1073741824.0).ToString("0.00")+"G"; } catch {} return "-"; } }
  public bool? RunVision { get { return runVision<0 ? (bool?)null : runVision==1; } }
}
}
