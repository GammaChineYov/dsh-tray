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
}

// 应用环境配置
public class AppConfig {
  public string LlamaServerExe { get; set; } = @"C:\llama.cpp\llama-server.exe";
  public string DshUrl { get; set; } = "http://127.0.0.1:3080/";
  public string SettingsYamlPath { get; set; } = @"C:\Users\<you>\.dsh\settings.yaml";
  public string OfficialDeepSeekUrl { get; set; } = "https://chat.deepseek.com";
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

  public static AppConfig Load() {
    try {
      if (File.Exists(Path_)) {
        var s = File.ReadAllText(Path_);
        var c = JsonSerializer.Deserialize<AppConfig>(s);
        if (c != null && c.Services != null && c.Services.Count > 0) {
          var d = Default();
          if (string.IsNullOrEmpty(c.LlamaServerExe)) c.LlamaServerExe = d.LlamaServerExe;
          if (string.IsNullOrEmpty(c.DshUrl)) c.DshUrl = d.DshUrl;
          if (string.IsNullOrEmpty(c.SettingsYamlPath)) c.SettingsYamlPath = d.SettingsYamlPath;
          if (string.IsNullOrEmpty(c.OfficialDeepSeekUrl)) c.OfficialDeepSeekUrl = d.OfficialDeepSeekUrl;
          return c;
        }
      }
    } catch {}
    var def = Default();
    try { File.WriteAllText(Path_, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true })); } catch {}
    return def;
  }
}
