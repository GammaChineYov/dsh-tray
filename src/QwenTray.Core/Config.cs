using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace QwenTray;

// 单个服务的配置（全部可由用户编辑）
public class ServiceConfig {
  public string Name { get; set; } = "";
  public int Port { get; set; }
  public string Model { get; set; } = "";
  public bool UseMmproj { get; set; }
  public string Mmproj { get; set; } = "";
  public int Batch { get; set; }
  public int Ubatch { get; set; }
  public bool SpecDecode { get; set; }
  public string Provider { get; set; } = "";   // DSH provider 名（打开会话时写入 agent-default-model）
  public bool Enabled { get; set; } = true;

  // —— per-service 覆盖（2026-09-20）——
  // 为什么需要：托盘原本只有**一个全局 exe**（AppConfig.LlamaServerExe）和**一套写死的
  // llama-server 参数模板**（LaunchArgs.Build）。而 llm-deploy 的 kvmem 移植用的是另一个
  // 可执行文件（llama-kvmem-server.exe）、另一套参数方言（--kv-dtype / --kvmem-*）和四个
  // CUDA 环境变量。让它出现在托盘服务列表里，就必须让**服务项**能覆盖这三样。
  // 三个字段都是**可选**的：全留空 = 该服务与改动前逐字节一致（零回归）。
  public string Exe { get; set; } = "";    // 覆盖 AppConfig.LlamaServerExe；空 = 用全局
  public string Args { get; set; } = "";   // 完整参数串；非空 ⇒ 完全取代内置模板（见 LaunchPlan）
  public string Env { get; set; } = "";    // 追加环境变量："K=V"，**一行一项**（换行分隔）；同名覆盖内置值。⛔ 不用分号：PATH 的值本身就含 ';'

  // —— 参数型覆盖（2026-09-26）——
  // 语义变更：菜单里的「上下文 / KV / 缓存内存 / 切分 / GPU1 占比 / MTP / 参数组 / 监听」从
  // 「启动时覆盖所有模型」改为「**改的就是当前选中模型的持久化配置**」：
  //   null  = 本模型未设置 ⇒ 用全局默认（dsh-tray.cfg）；
  //   非 null = 本模型自己的值，**启动该模型时优先用它**（写进 dsh-tray-config.json，重启托盘后仍在）。
  // ⚠️ 为什么用 int? / bool? 而不是 0：0 是合法值（如 cacheRam=0=禁用、bind=127.0.0.1），
  //    用非空类型才能区分"没写"与"写了 0"（与 Batch/Ubatch/Provider 同一条守卫惯例）。
  public int? Ctx { get; set; }
  public int? KvMode { get; set; }
  public int? CacheRam { get; set; }
  public int? SplitMode { get; set; }
  public int? TsGpu1 { get; set; }
  public int? MtpLevel { get; set; }
  public int? ParamMode { get; set; }
  public bool? BindAll { get; set; }
  public string GpuSel { get; set; } = "";   // 空 = 用全局 GPU 选择（GpuSelection.FromCfg 的串）
}

// 应用环境配置
public class AppConfig {
  public string LlamaServerExe { get; set; } = @"C:\llama.cpp\llama-server.exe";
  public string DshUrl { get; set; } = "http://127.0.0.1:3080/";
  public string SettingsYamlPath { get; set; } = @"C:\Users\<you>\.dsh\settings.yaml";
  public string OfficialDeepSeekUrl { get; set; } = "https://chat.deepseek.com";
  // DSH（本机 DSH Web 服务）控制：托盘「启动/重启/停止 DSH」用（留空则菜单仅提示配置）
  public string DshNodeExe { get; set; } = "";   // 启动 DSH 用的 node.exe 完整路径
  public string DshCliBinJs { get; set; } = "";  // DSH cli 入口，如 <harness-checkout>\apps\cli\lib\bin.js
  public string DshWorkDir { get; set; } = "";   // DSH 工作目录（harness checkout）
  public string DshOutLog { get; set; } = "";    // DSH stdout 日志（留空=exe 同目录 dsh-web-out.log）
  public string DshErrLog { get; set; } = "";    // DSH stderr 日志（留空=exe 同目录 dsh-web-err.log）
  public string DshHomeDir { get; set; } = "";    // 用户 .dsh 目录（留空=默认 %USERPROFILE%\.dsh；本机 DSH_HOME 非默认时填真实值）
  public string DshWebToken { get; set; } = "";  // 当前 dsh web 启动 token（dsh web 每次启动会变；托盘弹窗用它首登种 cookie。留空=弹窗自行登录）
  // dshtray-status host 插件（~/.dsh/dev-plugins/dshtray-status）令牌文件与会话已读表路径；留空 = 默认 %USERPROFILE%\.dsh 下
  public string DshtrayTokenPath { get; set; } = "";   // dshtray-status.token（插件生成、托盘读取，作 /dshtray-status/api 鉴权）
  public string RecentReadPath { get; set; } = "";     // dshtray-read.json（托盘本地“已点开会话”记录，未读徽标依据）
  public List<ServiceConfig> Services { get; set; } = new List<ServiceConfig>();
}

public static class Config {
  public static string Path_ { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dsh-tray-config.json"); } }

  public static AppConfig Default() {
    var c = new AppConfig();
    c.Services.Add(new ServiceConfig{ Name="Qwen3.6-35B (icompact)", Port=8081, Model=@"C:\models\<35B-model>.gguf", Batch=1024, Ubatch=1024, Provider="llama-local-icompact", Enabled=true });
    c.Services.Add(new ServiceConfig{ Name="Qwen3.8-27B (thinking)", Port=8082, Model=@"C:\models\<27B-model>.gguf", UseMmproj=true, Mmproj=@"C:\models\mmproj-F16.gguf", Batch=2048, Ubatch=512, SpecDecode=true, Provider="llama-local-thinking", Enabled=true });
    c.Services.Add(new ServiceConfig{ Name="Qwen3.5-9B (vision)", Port=8083, Model=@"C:\models\<9B-model>.gguf", UseMmproj=true, Mmproj=@"C:\models\mmproj-<9B>.gguf", Batch=2048, Ubatch=512, Provider="llama-local", Enabled=true });
    return c;
  }

  // S4（2026-09-12）：把「解析 + 空值回填」从 Load() 里抽成**纯函数（无 IO）** ⇒ 可被单测穷举。
  // 返回 null = 这份 JSON 不可用（空串 / 非法 JSON / 解析出来是 null / 没有 services）
  //          ⇒ 调用方应回落 Default() 并写盘（原 Load() 的行为，一字未改）。
  public static AppConfig? MergeFrom(string? json) {
    if (string.IsNullOrEmpty(json)) return null;
    AppConfig? c;
    try { c = JsonSerializer.Deserialize<AppConfig>(json); } catch { return null; }
    if (c == null || c.Services == null || c.Services.Count == 0) return null;
    return Merge(c);
  }

  // 空值回填：**只补这 4 个字符串字段** —— 这是既有行为，S4 如实保留。
  // ⚠️ Dsh* 系列（DshNodeExe/DshCliBinJs/DshWorkDir/…）**刻意不回填**：留空 = 菜单仅提示配置，
  //    补默认值会让 "本机 DSH_HOME 非默认时填真实值" 这个有意留空失效。改动前先看 MEMORY/skill。
  public static AppConfig Merge(AppConfig c) {
    var d = Default();
    if (string.IsNullOrEmpty(c.LlamaServerExe)) c.LlamaServerExe = d.LlamaServerExe;
    if (string.IsNullOrEmpty(c.DshUrl)) c.DshUrl = d.DshUrl;
    if (string.IsNullOrEmpty(c.SettingsYamlPath)) c.SettingsYamlPath = d.SettingsYamlPath;
    if (string.IsNullOrEmpty(c.OfficialDeepSeekUrl)) c.OfficialDeepSeekUrl = d.OfficialDeepSeekUrl;
    return c;
  }

  /// <summary>把内存里的配置**整份**写回 dsh-tray-config.json（2026-09-26）。
  /// 只用于「改了参数就落到对应模型配置」这条新语义 —— ⚠️ 它会抹掉文件里所有未走托盘写入的
  /// 手工修改，所以调用点必须传**从 Load() 读出来的同一份对象**（cfg），别传重建的默认值。</summary>
  public static void Save(AppConfig c) {
    if (c == null) return;
    try { File.WriteAllText(Path_, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true })); } catch {}
  }

  public static AppConfig Load() {
    try {
      if (File.Exists(Path_)) {
        var c = MergeFrom(File.ReadAllText(Path_));
        if (c != null) return c;
      }
    } catch {}
    var def = Default();
    try { File.WriteAllText(Path_, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true })); } catch {}
    return def;
  }
}
