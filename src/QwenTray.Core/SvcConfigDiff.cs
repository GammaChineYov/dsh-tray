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

    // —— per-service 覆盖（2026-09-20）：同样"留空 = 不表态" ——
    // 即：从 JSON 里删掉 Exe/Args/Env 三项**不会**让本服务退回内置模板，只是维持上次读到的值
    // （与 Provider/Batch 的既有惯例一致）。要真正退回内置行为，改 JSON 后**重启托盘**。
    // 为什么不让"删掉 = 清空"：那样一次误删键就把服务打回通用模板，症状（换了 exe/参数却无声恢复）
    // 比"删了没反应"难查得多。
    if (!string.IsNullOrEmpty(sc.Exe) && sc.Exe != cur.Exe) {
      diffs.Add("exe " + (cur.Exe.Length > 0 ? SvcLines.FileName(cur.Exe) : "（全局）") + " → " + SvcLines.FileName(sc.Exe));
      cur.Exe = sc.Exe;
    }
    // —— 参数型覆盖（2026-09-26）：「重启模型」重读配置时要把它们一起读回来 ——
    // "null / 空 = 不表态" 与上面 Exe/Env 同一条守卫：JSON 里没写这项就不覆盖内存里的值。
    // 返回**本项最终取值**：from 有值就用它（记一条 diff），否则保持原值（本模型未设置 ⇒ 走全局默认）。
    // 写成"返回新值"而不是"就地改 cur"，是因为 cur 是对象、各字段要各自独立回落，没法用同一个 ref 串起来。
    int? Take(int? from, int? cur, string key, List<string> d) {
      if (from.HasValue && from.Value != cur) {
        d.Add(key + " " + (cur.HasValue ? cur.Value.ToString() : "（全局）") + " → " + from.Value);
        return from.Value;
      }
      return cur;
    }
    cur.Ctx       = Take(sc.Ctx,       cur.Ctx,       "ctx",       diffs);
    cur.KvMode    = Take(sc.KvMode,    cur.KvMode,    "KV",        diffs);
    cur.CacheRam  = Take(sc.CacheRam,  cur.CacheRam,  "缓存内存",  diffs);
    cur.SplitMode = Take(sc.SplitMode, cur.SplitMode, "切分",      diffs);
    cur.TsGpu1    = Take(sc.TsGpu1,    cur.TsGpu1,    "GPU1占比",  diffs);
    cur.MtpLevel  = Take(sc.MtpLevel,  cur.MtpLevel,  "MTP",       diffs);
    cur.ParamMode = Take(sc.ParamMode, cur.ParamMode, "参数组",    diffs);
    if (sc.BindAll.HasValue && sc.BindAll.Value != cur.BindAll) { diffs.Add("监听 " + (cur.BindAll == true ? "0.0.0.0" : "127.0.0.1") + " → " + (sc.BindAll.Value ? "0.0.0.0" : "127.0.0.1")); cur.BindAll = sc.BindAll.Value; }
    if (!string.IsNullOrEmpty(sc.GpuSel) && sc.GpuSel != cur.GpuSel) { diffs.Add("GPU 选择 " + (cur.GpuSel.Length > 0 ? cur.GpuSel : "（全局）") + " → " + sc.GpuSel); cur.GpuSel = sc.GpuSel; }

    // Args/Env 长度可以到数百字符，日志里只报"变了"与长度，不把整串塞进一行日志
    if (!string.IsNullOrEmpty(sc.Args) && sc.Args != cur.Args) {
      diffs.Add("参数串 自定义（" + sc.Args.Length + " 字符）");
      cur.Args = sc.Args;
    }
    if (!string.IsNullOrEmpty(sc.Env) && sc.Env != cur.Env) {
      diffs.Add("环境变量 自定义（" + sc.Env.Length + " 字符）");
      cur.Env = sc.Env;
    }

    return diffs;
  }
}
