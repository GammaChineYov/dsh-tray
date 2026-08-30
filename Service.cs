using System.Text;

namespace QwenTray {
public class Service {
  public string Name; public int Port; public string Model; public bool UseMmproj=false; public string Mmproj=""; public int Ctx; public int Batch; public int Ubatch; public bool SpecDecode=false; public string Provider="";
  public System.Diagnostics.Process proc; public StringBuilder log = new StringBuilder(); public long lastLen=0;
  public bool Running { get { return proc!=null && !proc.HasExited; } }
  public string State { get { return Running ? "运行" : "停止"; } }
  public string RamGb { get { try { if(Running&&proc!=null) return (proc.WorkingSet64/1073741824.0).ToString("0.00")+"G"; } catch {} return "-"; } }
}
}
