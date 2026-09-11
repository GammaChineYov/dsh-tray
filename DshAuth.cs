using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QwenTray;

// dsh web 浏览器会话认证：读 ~/.dsh/.credentials.yaml 的 client-connection/browser-session 持久 secret，
// 自签 dsh-auth-<b64u(sha256(authority))> cookie（v1.<b64u(JSON)>.<b64u(HMAC-SHA256(secret, body))>）。
// 与 dsh web 每次启动变化的 ?token= 无关，凭据 30 天有效、重启保留（packages/client/connection/src/browser-auth.ts 契约）。
public static class DshAuth {
  static string B64u(byte[] b){ return Convert.ToBase64String(b).Replace('+','-').Replace('/','_').TrimEnd('='); }
  static byte[] B64uDec(string s){
    s = s.Replace('-','+').Replace('_','/');
    switch(s.Length % 4){ case 2: s += "=="; break; case 3: s += "="; break; }
    return Convert.FromBase64String(s);
  }

  // 从 .credentials.yaml 抽 browser-session secret（只做行级扫描，不引 YAML 库）
  public static string ReadSecret(string dshHome){
    try{
      string p = Path.Combine(dshHome, ".credentials.yaml");
      if(!File.Exists(p)) return "";
      bool inRecord = false;
      foreach(var raw in File.ReadLines(p)){
        var l = raw.TrimEnd();
        if(l.StartsWith("  client-connection/browser-session:")){ inRecord = true; continue; }
        if(inRecord){
          if(l.Length > 2 && l[0] == ' ' && l[1] != ' ') { /* 仍是 records 子级 */ }
          if(l.StartsWith("  ") && !l.StartsWith("    ") && !l.StartsWith("  client-connection/browser-session:")) inRecord = false;
          var t = l.Trim();
          if(inRecord && t.StartsWith("secret:")){
            var v = t.Substring(7).Trim().Trim('\'', '"');
            return v;
          }
        }
      }
    }catch{}
    return "";
  }

  // 生成 (cookieName, cookieValue)；authority 形如 127.0.0.1:3080（必须与请求 Host 头一致）
  public static (string name, string value) MintCookie(string secretB64, string authority, long nowMs, long lifetimeMs){
    byte[] secret = B64uDec(secretB64);
    string payload = "{\"version\":1,\"authority\":\"" + authority + "\",\"issuedAt\":" + nowMs + ",\"expiresAt\":" + (nowMs + lifetimeMs) + "}";
    string body = B64u(Encoding.UTF8.GetBytes(payload));
    string sig;
    using(var h = new HMACSHA256(secret)) sig = B64u(h.ComputeHash(Encoding.ASCII.GetBytes(body)));
    string name;
    using(var s = SHA256.Create()) name = "dsh-auth-" + B64u(s.ComputeHash(Encoding.ASCII.GetBytes(authority)));
    return (name, "v1." + body + "." + sig);
  }
}
