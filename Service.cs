namespace QwenTray {
public class Service {
  public string Name; public int Port; public string Model; public bool UseMmproj=false; public string Mmproj=""; public int Ctx; public int Batch; public int Ubatch; public bool SpecDecode=false; public string Provider="";
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
