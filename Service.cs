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
  // 2026-09-26：ServiceSpec.Ctx 从 int 升为 int?（"本模型未设置 ⇒ 走全局默认"，与其余 8 项同制）。
  // 转发属性跟着放宽为 int?：读点的 int → int? 隐式兼容，写点 `svc.Ctx = 196608` 照旧。
  public int? Ctx { get => Spec.Ctx; set => Spec.Ctx = value; }
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
  // —— 两项运行时用量指标（2026-09-13 加：悬浮 tooltip 的 KV cache / 内存缓存）——
  // KV：由 SvcProbe.KvUsage(port) 每 5s 后台探一次（/props 取总量、/slots 求和取用量）；kvTotal=0 表示未探到。
  public volatile int kvUsed=0;
  public volatile int kvTotal=0;
  // prompt cache（--cache-ram）：只能从 llama stdout 的 trace 行「cache state: N prompts, X MiB (limits: Y MiB…)」拿。
  // ⚠️ 该行需 llama-server 以 **-lv 4** 启动才输出；本托盘默认不加该开关 ⇒ 常态下 cacheRamLimitMib 保持 0（tooltip 显示「无」）。
  public volatile int cacheRamUsedMib=0;
  public volatile int cacheRamLimitMib=0;
  // —— 工作集主动回收（2026-09-13）——
  // 就绪瞬间打标：由 Tick 的后台探活线程在 Starting→就绪 的那一次置位，交 UI 线程的 Tick 执行一次回收。
  // 为什么不直接在探活线程里回收：回收是阻塞 P/Invoke（含 120ms 等待），而探活线程还要接着探别的服务；
  // 走标志位回到 Tick 既串行化（不会两个服务同时 trim 抢内核锁），也把日志写在 UI 线程上。
  public volatile bool pendingTrimOnReady=false;
  // 该实例上次回收的时间戳（Environment.TickCount）；0=从未。空闲自动回收的间隔判据用它。
  public long lastTrimMs=0;
  // 上次回收的回执行（给菜单状态行显示，避免用户"点了不知道有没有用"）
  public volatile string lastTrimLine="";
  // —— 启动预热（2026-09-13，L3 硬盘缓存）——
  // 与 trim 同款"就绪打标、Tick 消费"：预热要起 python 子进程（秒级），绝不能挂在探活线程上。
  // 为什么不在 Tick 里直接探"有没有条目"：那要多打一次磁盘；用标志位让就绪这一刻**只消费一次**，
  // 常态下零开销。
  public volatile bool pendingWarmupOnReady=false;
  // 上次预热的回执行（菜单状态行显示；空 = 本次托盘生命周期内还没预热过）
  public volatile string lastWarmupLine="";
  public bool Running { get { return proc!=null && !proc.HasExited; } }
  public string State { get { return Starting ? "启动中" : Running ? "运行" : "停止"; } }
  public string RamGb { get { try { if(Running&&proc!=null) return (proc.WorkingSet64/1073741824.0).ToString("0.00")+"G"; } catch {} return "-"; } }
  public bool? RunVision { get { return runVision<0 ? (bool?)null : runVision==1; } }
}
}
