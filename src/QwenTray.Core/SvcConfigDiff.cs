using System;
using System.Collections.Generic;

namespace QwenTray;

// ============================================================================
// S5-4（2026-09-12）：「重读配置」的**判定内核**从 TrayApp 搬进 Core。
//
// 场景：菜单「重启模型」会先重读 dsh-tray-config.json，只把**确实变了**的字段覆盖到内存里的
// 服务项上，并打一行「配置已更新: 模型 A → B；端口 8081 → 8082」。整段逻辑原先长在
// TrayApp.ReloadSvcConfig 里，混合了 IO（读文件/JS 反序列化）、日志、UI 刷新，一行都测不了。
//
// 这里只搬两件**纯判定**：
//   Find  —— 从解析结果里挑出对应的服务项（先按 Name 忽略大小写，再退到同 Port）
//   Apply —— 把差异就地写进 ServiceSpec，并返回人可读的差异列表（空列表 = 配置无变化）
//
// 为什么值得测：三条**守卫**决定了用户手改的配置能不能生效，而它们都非常容易写错 ——
//   · Batch/Ubatch 只在 sc 里 > 0 时才覆盖（配置里没写 = 0，不能被当成"改成 0"抹掉内存里的值）
//   · Provider 只在 sc 里非空时才覆盖（留空 = 不表态，不是"清空 provider"）
//   · Mmproj 的**双条件**：UseMmproj 或 Mmproj 任一变化才算变化（只勾开关不换文件也要认）
// 这些"配置不生效 / 被莫名抹掉"类投诉的根因就在这里，而原来没有任何自动断言。
// ============================================================================
public static class SvcConfigDiff {

  // 在刚解析出来的配置里找这个服务：先 Name（忽略大小写），再退到同 Port；都没有 → null。
  // 顺序不能反：同一端口被改过 Name 时，Name 命中的那个才是用户想改的那一项。
  public static ServiceConfig? Find(AppConfig? fresh, string name, int port) {
    var list = fresh?.Services;
    if (list == null) return null;
    foreach (var x in list)
      if (string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) return x;
    foreach (var x in list)
      if (x.Port == port) return x;
    return null;
  }

  // 把 sc 的差异**就地**应用到 cur（ServiceSpec 是引用类型，调用方传 svc.Spec 即写穿到服务项），
  // 返回人可读的差异列表 —— 空列表表示"配置无变化"，调用方据此打不同日志。
  // 差异描述里出现的文件名一律走 SvcLines.FileName：完整 GGUF 路径会把日志行撑爆。
  public static List<string> Apply(ServiceConfig sc, ServiceSpec cur) {
    var diffs = new List<string>();

    if (sc.Model != cur.Model) {
      diffs.Add("模型 " + SvcLines.FileName(cur.Model) + " → " + SvcLines.FileName(sc.Model));
      cur.Model = sc.Model;
    }
    if (sc.Port != cur.Port) {
      diffs.Add("端口 " + cur.Port + " → " + sc.Port);
      cur.Port = sc.Port;
    }
    if (sc.UseMmproj != cur.UseMmproj || sc.Mmproj != cur.Mmproj) {
      diffs.Add("mmproj " + SvcLines.FileName(cur.Mmproj) + " → " + (sc.UseMmproj ? SvcLines.FileName(sc.Mmproj) : "关"));
      cur.UseMmproj = sc.UseMmproj;
      cur.Mmproj = sc.Mmproj;
    }
    // 只在 > 0 时覆盖：配置里没写这项（=0）表示"不表态"，不能把内存里的值抹成 0
    if (sc.Batch > 0 && sc.Batch != cur.Batch) {
      diffs.Add("批 " + cur.Batch + " → " + sc.Batch);
      cur.Batch = sc.Batch;
    }
    if (sc.Ubatch > 0 && sc.Ubatch != cur.Ubatch) {
      diffs.Add("ubatch " + cur.Ubatch + " → " + sc.Ubatch);
      cur.Ubatch = sc.Ubatch;
    }
    if (sc.SpecDecode != cur.SpecDecode) {
      diffs.Add("SpecDecode " + cur.SpecDecode + " → " + sc.SpecDecode);
      cur.SpecDecode = sc.SpecDecode;
    }
    // 同理：留空 = 不表态，不是"清空 provider"
    if (!string.IsNullOrEmpty(sc.Provider) && sc.Provider != cur.Provider) {
      diffs.Add("provider " + cur.Provider + " → " + sc.Provider);
      cur.Provider = sc.Provider;
    }

    return diffs;
  }
}
