using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace QwenTray;

// dsh web JSON-RPC 客户端：POST /api/<method>，信封 {type:client-request, rpcId, method, payload:{args}}，
// 响应 {type:server-response, result:{ok, value|error}}。认证用 DshAuth 自签 cookie（免 token）。
public class DshRpc {
  static readonly HttpClient http = new HttpClient{ Timeout = TimeSpan.FromSeconds(30) };
  readonly string baseUrl; readonly string authority; readonly string dshHome;
  string secret = ""; long cookieIssuedAt = 0; string cookieHeader = "";
  static int rpcSeq = 0;

  public string LastError = "";

  public DshRpc(string baseUrl, string dshHome){
    this.baseUrl = baseUrl.TrimEnd('/');
    this.dshHome = dshHome;
    var u = new Uri(this.baseUrl);
    authority = u.Authority; // 127.0.0.1:3080
  }

  public bool EnsureAuth(){
    if(cookieHeader.Length > 0 && Environment.TickCount64 - cookieIssuedAt < 20L*24*3600*1000) return true;
    if(secret.Length == 0) secret = DshAuth.ReadSecret(dshHome);
    if(secret.Length == 0){ LastError = "未找到 " + Path.Combine(dshHome, ".credentials.yaml") + " 的 browser-session secret（dsh web 至少成功启动过一次才会生成）"; return false; }
    var c = DshAuth.MintCookie(secret, authority, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 30L*24*3600*1000);
    cookieHeader = c.name + "=" + c.value;
    cookieIssuedAt = Environment.TickCount64;
    return true;
  }

  // 调 RPC；ok=true 时 value 为 result.value 的原始 JSON。argsJson 是 args 对象的 JSON 文本（可为 "{}"）。
  public async Task<(bool ok, string value, string error)> Call(string method, string argsJson){
    if(!EnsureAuth()) return (false, "", LastError);
    try{
      string id = "tray-" + (++rpcSeq);
      string env = "{\"type\":\"client-request\",\"rpcId\":\"" + id + "\",\"method\":\"" + method + "\",\"payload\":{\"args\":" + (string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson) + "}}";
      using(var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/" + method)){
        req.Content = new StringContent(env, Encoding.UTF8, "application/json");
        req.Headers.Add("cookie", cookieHeader);
        using(var resp = await http.SendAsync(req)){
          string body = await resp.Content.ReadAsStringAsync();
          if((int)resp.StatusCode == 401) return (false, "", "401 unauthorized（凭据失效？可尝试删除 .credentials.yaml 的 browser-session 记录后重启 dsh）");
          if(!resp.IsSuccessStatusCode) return (false, "", "HTTP " + (int)resp.StatusCode + ": " + (body.Length > 200 ? body.Substring(0,200) : body));
          using(var doc = JsonDocument.Parse(body)){
            var r = doc.RootElement;
            if(!r.TryGetProperty("result", out var res)) return (false, "", "响应缺 result: " + (body.Length > 200 ? body.Substring(0,200) : body));
            if(res.TryGetProperty("ok", out var okEl) && okEl.GetBoolean()){
              return (true, res.TryGetProperty("value", out var v) ? v.GetRawText() : "null", "");
            }
            string err = res.TryGetProperty("error", out var e) ? e.GetRawText() : "unknown";
            return (false, "", err);
          }
        }
      }
    }catch(Exception ex){ return (false, "", ex.Message); }
  }
}
