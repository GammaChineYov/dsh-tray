using System;
using System.Drawing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace QwenTray;

// 托盘内置渲染（瘦客户端）：WebView2 承载本地 HTML 页，JS 经 chrome.webview 消息桥调 C#，
// C# 用 DshRpc（自签 cookie）打 dsh web 的 /api/<method>。完全不加载 dsh 官方客户端 bundle ——
// 官方 bundle 崩溃（如 client-modules import 失败白屏）时，会话仍可看、可续。
// P0 功能：会话列表 / 历史渲染（极简 markdown）/ 发消息续聊 / 中断生成；流式 = 2.5s 轮询 session/page。
public class ThinChatForm : Form {
  readonly DshRpc rpc;
  readonly Action<string>? log;
  WebView2? wv;
  bool ready;

  public ThinChatForm(DshRpc rpc, Icon? icon, Action<string>? log = null){
    this.rpc = rpc; this.log = log;
    Text = "DSH 内置渲染（瘦客户端）";
    if(icon != null) Icon = icon;
    FormBorderStyle = FormBorderStyle.SizableToolWindow;
    StartPosition = FormStartPosition.CenterScreen;
    Size = new Size(1080, 760);
    ShowInTaskbar = false; MinimizeBox = true;
    FormClosing += (s, e) => { if(e.CloseReason == CloseReason.UserClosing){ e.Cancel = true; Hide(); } };
  }

  public async void OpenSession(string sessionId){
    await EnsureReady();
    Show(); Activate();
    if(WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
    if(ready && wv?.CoreWebView2 != null && sessionId.Length > 0)
      wv.CoreWebView2.PostWebMessageAsJson("{\"cmd\":\"open-session\",\"sessionId\":\"" + JsonEncodedText.Encode(sessionId) + "\"}");
  }

  public async void ShowHome(){
    await EnsureReady();
    Show(); Activate();
    if(WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
  }

  async Task EnsureReady(){
    if(ready) return;
    if(wv == null){
      wv = new WebView2{ Dock = DockStyle.Fill };
      Controls.Add(wv);
    }
    await wv.EnsureCoreWebView2Async();
    wv.CoreWebView2.WebMessageReceived += OnWebMessage;
    wv.CoreWebView2.NavigateToString(Page.Html);
    ready = true;
  }

  async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e){
    string id = "0", method = "", argsRaw = "{}";
    try{
      using var doc = JsonDocument.Parse(e.WebMessageAsJson);
      var r = doc.RootElement;
      id = r.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "0";
      method = r.GetProperty("method").GetString() ?? "";
      if(r.TryGetProperty("args", out var a)) argsRaw = a.GetRawText();
    }catch{ return; }

    if(method == "open-url"){
      try{
        using var doc = JsonDocument.Parse(argsRaw);
        var url = doc.RootElement.GetProperty("url").GetString() ?? "";
        if(url.StartsWith("http")) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){ UseShellExecute = true });
      }catch{}
      return;
    }

    // 其余一律透传 dsh RPC（method 形如 session/list，C# 侧不校验，网关自会拒错）
    var res = await rpc.Call(method, argsRaw);
    string reply;
    if(res.ok) reply = "{\"id\":" + id + ",\"ok\":true,\"value\":" + (res.value.Length > 0 ? res.value : "null") + "}";
    else{
      string errJson;
      try{ using var d = JsonDocument.Parse(res.error); errJson = res.error; }catch{ errJson = "{\"message\":" + JsonSerializer.Serialize(res.error) + "}"; }
      reply = "{\"id\":" + id + ",\"ok\":false,\"error\":" + errJson + "}";
    }
    try{ wv?.CoreWebView2?.PostWebMessageAsJson(reply); }catch{}
  }
}

// 内嵌页：单文件 HTML+JS（无 CDN 依赖，离线可用）。markdown 是极简子集（围栏代码/行内码/粗体/标题/列表）。
static class Page {
  public const string Html = """
<!DOCTYPE html>
<html lang="zh">
<head>
<meta charset="utf-8">
<title>DSH 内置渲染</title>
<style>
:root{--bg:#ffffff;--panel:#f6f6f4;--border:#e2e2de;--text:#1f1f1d;--muted:#7a7a74;--accent:#185FA5;--user:#E6F1FB;--code:#f1efe8}
*{box-sizing:border-box}
html,body{height:100%;margin:0;font:13px/1.65 "Microsoft YaHei UI",system-ui,sans-serif;color:var(--text);background:var(--bg)}
#app{display:flex;flex-direction:column;height:100%}
#bar{display:flex;align-items:center;gap:8px;padding:6px 10px;border-bottom:1px solid var(--border);background:var(--panel)}
#bar .title{font-weight:600}
#bar button{font:12px/1 inherit;padding:5px 10px;border:1px solid var(--border);border-radius:6px;background:#fff;cursor:pointer}
#bar button:hover{border-color:var(--accent)}
#status{margin-left:auto;color:var(--muted);font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:46%}
#main{flex:1;display:flex;min-height:0}
#side{width:270px;border-right:1px solid var(--border);overflow-y:auto;background:var(--panel)}
.sess{padding:7px 10px;border-bottom:1px solid var(--border);cursor:pointer}
.sess:hover{background:#eee}
.sess.active{background:var(--user);border-left:3px solid var(--accent)}
.sess .t{font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.sess .m{color:var(--muted);font-size:11px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.dot{display:inline-block;width:8px;height:8px;border-radius:50%;background:#c8c8c2;margin-right:5px}
.dot.run{background:#3B6D11;animation:blink 1s infinite}
@keyframes blink{50%{opacity:.35}}
.subbadge{display:inline-block;font-size:10px;color:#fff;background:#6d5bd0;border-radius:4px;padding:0 4px;margin-right:5px;vertical-align:1px}
#input:disabled{background:#f0f0ec;color:var(--muted)}
#send:disabled{opacity:.45;cursor:default}
#chat{flex:1;display:flex;flex-direction:column;min-width:0}
#msgs{flex:1;overflow-y:auto;padding:12px 16px}
.msg{margin:0 0 12px;max-width:86%}
.msg .who{font-size:11px;color:var(--muted);margin-bottom:2px}
.msg .body{padding:8px 12px;border-radius:10px;border:1px solid var(--border);background:#fff;overflow-wrap:anywhere}
.msg.user{margin-left:auto}
.msg.user .who{text-align:right}
.msg.user .body{background:var(--user);border-color:#B5D4F4}
.msg pre{background:var(--code);border:1px solid var(--border);border-radius:6px;padding:8px;overflow-x:auto;font:12px/1.5 Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere}
.msg code{background:var(--code);border-radius:4px;padding:1px 4px;font-family:Consolas,monospace;font-size:12px}
.msg pre code{background:none;padding:0}
details{border:1px solid var(--border);border-radius:8px;margin:6px 0;background:var(--panel)}
details summary{cursor:pointer;padding:5px 10px;color:var(--muted);font-size:12px;user-select:none}
details pre{border:none;background:none;margin:0 10px 8px}
.turnend{text-align:center;color:var(--muted);font-size:11px;margin:10px 0}
#composer{display:flex;gap:8px;padding:10px;border-top:1px solid var(--border);background:var(--panel)}
#input{flex:1;resize:none;height:56px;padding:8px;border:1px solid var(--border);border-radius:8px;font:13px/1.5 inherit}
#send,#cancelBtn{padding:0 18px;border:1px solid var(--accent);border-radius:8px;background:var(--accent);color:#fff;cursor:pointer}
#cancelBtn{border-color:#A32D2D;background:#A32D2D;display:none}
#err{display:none;padding:8px 12px;background:#FCEBEB;color:#791F1F;border-bottom:1px solid #F09595;font-size:12px;white-space:pre-wrap}
.hint{color:var(--muted);text-align:center;margin-top:40px}

/* ===== 新增：模型选择下拉框 ===== */
.model-seat{display:flex;align-items:center;gap:6px;padding:6px 10px;border-bottom:1px solid var(--border);background:var(--panel)}
.model-btn{font:12px/1 inherit;padding:5px 12px;border:1px solid var(--border);border-radius:6px;background:#fff;cursor:pointer;display:flex;align-items:center;gap:6px}
.model-btn:hover{border-color:var(--accent)}
.model-btn .icon{font-size:14px}
.model-btn .current{color:var(--muted);font-size:11px;max-width:150px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.model-menu{position:absolute;bottom:100%;left:10px;margin-bottom:4px;background:#fff;border:1px solid var(--border);border-radius:8px;box-shadow:0 -4px 12px rgba(0,0,0,.1);min-width:280px;max-height:400px;overflow-y:auto;display:none;z-index:100}
.model-menu.open{display:block}
.model-group{padding:8px 0;border-bottom:1px solid var(--border)}
.model-group:last-child{border-bottom:none}
.model-group-title{padding:4px 12px;font-size:11px;color:var(--muted);font-weight:600;text-transform:uppercase}
.model-item{padding:6px 12px;cursor:pointer;display:flex;justify-content:space-between;align-items:center}
.model-item:hover{background:#f0f0ec}
.model-item.selected{background:var(--user)}
.model-item .name{font-size:12px}
.model-item .effort{font-size:10px;color:var(--muted)}

/* ===== 新增：审批 UI ===== */
.approval-card{border:1px solid #F09595;border-radius:8px;padding:12px;margin:8px 0;background:#FCEBEB;max-width:86%}
.approval-card h4{margin:0 0 8px;font-size:13px;color:#791F1F}
.approval-card .desc{font-size:12px;margin-bottom:10px;white-space:pre-wrap}
.approval-actions{display:flex;gap:8px}
.approval-actions button{padding:6px 14px;border:1px solid var(--border);border-radius:6px;cursor:pointer;font-size:12px}
.approval-actions .approve{background:#3B6D11;color:#fff;border-color:#3B6D11}
.approval-actions .reject{background:#A32D2D;color:#fff;border-color:#A32D2D}
.approval-actions button:hover{opacity:.85}

/* ===== 新增：工作区分组 ===== */
.workspace-group{border-bottom:1px solid var(--border)}
.workspace-header{padding:8px 10px;background:#e8e8e4;font-size:11px;color:var(--muted);display:flex;justify-content:space-between;align-items:center}
.workspace-header .title{font-weight:600;color:var(--text)}
.workspace-header .path{font-size:10px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:60%}
.workspace-sessions{background:#fff}
</style>
</head>
<body>
<div id="app">
  <div id="bar">
    <span class="title">DSH 内置渲染</span>
    <button id="refresh">刷新会话</button>
    <button id="create">新建会话</button>
    <button id="official">官方 UI</button>
    <span id="status">初始化…</span>
  </div>
  <div id="err"></div>
  <div id="main">
    <div id="side"></div>
    <div id="chat">
      <div id="msgs"><div class="hint">选择左侧会话开始；本页直连 dsh RPC，不经过官方客户端 bundle</div></div>
      
<!-- 模型选择下拉框 -->
<div class="model-seat" id="modelSeat">
  <button class="model-btn" id="modelBtn">
    <span class="icon">🤖</span>
    <span class="current" id="currentModel">选择模型</span>
  </button>
  <div class="model-menu" id="modelMenu">
    <div class="model-loading">加载中...</div>
  </div>
</div>
<div id="composer">
        <textarea id="input" placeholder="输入消息，Enter 发送，Shift+Enter 换行"></textarea>
        <button id="send">发送</button>
        <button id="cancelBtn">中断</button>
      </div>
    </div>
  </div>
</div>
<script>
(function(){
"use strict";
var seq = 0, pending = {};
var sessions = [], curSid = "", cursor = {}, pollTimer = null, sending = false;
var subInfo = {}; // sid -> { parent, mode, label, modeFixed }，子代理会话 durable 地址信息（origin=subagent 时必须用父地址 page）
var $ = function(id){ return document.getElementById(id); };

function rpc(method, args){
  return new Promise(function(resolve, reject){
    var id = ++seq;
    pending[id] = { resolve: resolve, reject: reject };
    window.chrome.webview.postMessage({ id: id, method: method, args: args || {} });
  });
}
window.chrome.webview.addEventListener("message", function(e){
  var d = e.data;
  if(d.cmd === "open-session"){ openSession(d.sessionId); return; }
  var p = pending[d.id]; if(!p) return;
  delete pending[d.id];
  if(d.ok) p.resolve(d.value);
  else p.reject(d.error || { message: "unknown" });
});

function esc(s){ return String(s == null ? "" : s).replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;"); }
function md(src){
  var s = esc(src);
  s = s.replace(/```(\w*)\n?([\s\S]*?)```/g, function(m, l, c){ return "<pre><code>" + c.replace(/\n$/,"") + "</code></pre>"; });
  s = s.replace(/`([^`\n]+)`/g, "<code>$1</code>");
  s = s.replace(/^######\s?(.*)$/gm, "<h6>$1</h6>").replace(/^#####\s?(.*)$/gm, "<h5>$1</h5>")
       .replace(/^####\s?(.*)$/gm, "<h4>$1</h4>").replace(/^###\s?(.*)$/gm, "<h3>$1</h3>")
       .replace(/^##\s?(.*)$/gm, "<h2>$1</h2>").replace(/^#\s?(.*)$/gm, "<h1>$1</h1>");
  s = s.replace(/\*\*([^*\n]+)\*\*/g, "<b>$1</b>");
  s = s.replace(/((?:^[ \t]*(?:[-*]|\d+\.)[ \t]+.*(?:\n|$))+)/gm, function(block){
    var items = block.trimEnd().split("\n").map(function(l){
      return "<li>" + l.replace(/^[ \t]*(?:[-*]|\d+\.)[ \t]+/, "") + "</li>";
    }).join("");
    return "<ul>" + items + "</ul>";
  });
  s = s.replace(/\n{2,}/g, "<br><br>").replace(/\n/g, "<br>");
  return s;
}
function relTime(ms){
  if(!ms) return "";
  var d = Date.now() - ms;
  if(d < 60000) return "刚刚";
  if(d < 3600000) return Math.floor(d/60000) + "分钟前";
  if(d < 86400000) return Math.floor(d/3600000) + "小时前";
  return Math.floor(d/86400000) + "天前";
}
function setStatus(t){ $("status").textContent = t; }
function showErr(t){ var e = $("err"); if(!t){ e.style.display = "none"; return; } e.style.display = "block"; e.textContent = t; }
function errText(err){
  if(!err) return "unknown";
  if(typeof err === "string") return err;
  return (err.code ? err.code + ": " : "") + (err.message || JSON.stringify(err));
}

// 从 list 响应登记子代理会话的 durable 地址信息（含被 blank 过滤掉的项，保证直接打开也能命中）
function noteSubagents(items){
  (items || []).forEach(function(it){
    if(it.origin === "subagent" && it.parentSessionId){
      var pv = (it.projections && it.projections.values) || {};
      var old = subInfo[it.sessionId];
      subInfo[it.sessionId] = {
        parent: it.parentSessionId,
        mode: (old && old.modeFixed) ? old.mode : ((pv.subagent && pv.subagent.mode) || (old && old.mode) || "continuable"),
        modeFixed: !!(old && old.modeFixed),
        label: (pv.subagent && pv.subagent.label) || (old && old.label) || ""
      };
    }
  });
}
function isSub(sid){ return !!subInfo[sid]; }
function addrFor(sid){
  var si = subInfo[sid];
  if(si) return { kind: "subagent", parentSessionId: si.parent, childSessionId: sid, mode: si.mode };
  return { kind: "session", sessionId: sid };
}
function flipMode(sid){
  var si = subInfo[sid]; if(!si) return;
  si.mode = si.mode === "continuable" ? "one-shot" : "continuable";
  si.modeFixed = true;
}
function updateInputState(){
  var ro = isSub(curSid);
  $("input").disabled = ro;
  $("send").disabled = ro;
  $("input").placeholder = ro ? "子代理会话只读（由父会话驱动，请打开父会话继续）" : "输入消息，Enter 发送，Shift+Enter 换行";
}

function loadSessions(keepSid){
  setStatus("加载会话列表…");
  return rpc("session/list", { _request: {} }).then(function(v){
    noteSubagents(v.items);
    // 子代理会话不进列表（由父会话驱动、只读）；subInfo 仍登记以兼容"最近会话"直开
    sessions = (v.items || []).filter(function(s){ return !s.blank && s.origin !== "subagent"; });
    sessions.sort(function(a,b){ return (b.updatedAt||0) - (a.updatedAt||0); });
    // 加载工作区并渲染
    return loadWorkspaces().then(function(){
      renderWorkspaceSessions();
      updateInputState();
      setStatus(sessions.length + " 个会话");
      showErr("");
      if(keepSid && sessions.some(function(s){ return s.sessionId === keepSid; })) return keepSid;
      return "";
    });
  }).catch(function(err){ setStatus("会话列表失败"); showErr("session/list 失败：" + errText(err) + "\n（dsh web 未启动或凭据失效时，请先用托盘启动 DSH）"); return ""; });
}
function renderSessions(){
  // 使用工作区分组渲染
  renderWorkspaceSessions();
}

function fetchPage(sid, throughSeq){
  var req = function(ts){ return rpc("session/page", { request: { address: addrFor(sid), throughSeq: ts, maxMessages: 60 } }); };
  return req(throughSeq).then(null, function(err){
    var msg = (err && err.message) || "";
    var m = /past cursor (\d+)/.exec(msg);
    if(m){ cursor[sid] = parseInt(m[1], 10); return req(cursor[sid]); }
    // 投影给的 mode 与服务端不一致（罕见）：换另一种 mode 重试一次
    if(/mode does not match/.test(msg) && isSub(sid)){ flipMode(sid); return req(throughSeq); }
    // 子代理会话但本地还没有 durable 地址（如列表未加载完就被直接打开）：刷新列表后用父地址重试
    if(/durable parent address/.test(msg) && !isSub(sid)){
      return loadSessions(sid).then(function(){ return req(throughSeq); });
    }
    throw err;
  });
}
function openSession(sid){
  curSid = sid; renderWorkspaceSessions(); updateInputState();
  $("msgs").innerHTML = "<div class='hint'>加载历史…</div>";
  setStatus("加载历史 " + sid.slice(-8) + "…");
  fetchPage(sid, cursor[sid] || 9007199254740991).then(function(v){
    renderEvents(v.records || []);
    setStatus("会话 " + sid.slice(-8) + (isSub(sid) ? "（子代理 · 只读）" : ""));
    showErr("");
    schedulePoll();
    loadModels(); // 加载模型列表
  }).catch(function(err){ $("msgs").innerHTML = "<div class='hint'>历史加载失败</div>"; showErr("session/page 失败：" + errText(err)); });
}

function partText(parts){
  var out = [];
  (parts || []).forEach(function(p){ if(p && p.type === "text" && p.text) out.push(p.text); });
  return out.join("\n");
}
function renderEvents(recs){
  var box = $("msgs"); box.innerHTML = "";
  var rendered = 0;
  recs.forEach(function(r){
    var ev = r.event || {}; var t = ev.type; var d = ev.data || {};
    if(t === "user/message"){
      var txt = partText(d.content);
      if(!txt) return;
      box.appendChild(bubble("user", "我", md(txt))); rendered++;
    }else if(t === "assistant/message"){
      var msg = d.message || {}; var parts = msg.content || [];
      var body = "";
      parts.forEach(function(p){
        if(p.type === "reasoning" && p.text) body += "<details><summary>思考过程</summary><pre>" + esc(p.text) + "</pre></details>";
        else if(p.type === "text" && p.text) body += md(p.text);
      });
      if(!body) return;
      box.appendChild(bubble("assistant", "助手", body)); rendered++;
    }else if(t === "tool/call"){
      var args = d.arguments || "";
      try{ args = JSON.stringify(JSON.parse(args), null, 2); }catch(e){}
      if(args.length > 4000) args = args.slice(0, 4000) + "\n…(截断)";
      var det = document.createElement("details");
      det.innerHTML = "<summary>工具调用：" + esc(d.name || "?") + "</summary><pre>" + esc(args) + "</pre>";
      box.appendChild(det);
    }else if(t === "tool/result"){
      var res = d.result;
      var txt2 = typeof res === "string" ? res : JSON.stringify(res, null, 2);
      if(!txt2) return;
      if(txt2.length > 2000) txt2 = txt2.slice(0, 2000) + "\n…(截断)";
      var det2 = document.createElement("details");
      det2.innerHTML = "<summary>工具结果</summary><pre>" + esc(txt2) + "</pre>";
      box.appendChild(det2);
    }else if(t === "turn/end"){
      var te = document.createElement("div");
      te.className = "turnend";
      te.textContent = "— 本轮结束（" + (d.reason || "?") + "）—";
      box.appendChild(te);
    }
  });
  if(!rendered && !box.children.length) box.innerHTML = "<div class='hint'>（此会话暂无可显示消息）</div>";
  box.scrollTop = box.scrollHeight;
}
function bubble(role, who, html){
  var d = document.createElement("div");
  d.className = "msg " + role;
  d.innerHTML = "<div class='who'>" + esc(who) + "</div><div class='body'>" + html + "</div>";
  return d;
}

function currentRunning(){
  var s = sessions.find(function(x){ return x.sessionId === curSid; });
  return s && s.running;
}
function schedulePoll(){
  if(pollTimer) clearTimeout(pollTimer);
  pollTimer = setTimeout(function(){
    poll();
    checkApprovals(); // 检查审批请求
  }, 2500);
}
function poll(){
  if(!curSid){ schedulePoll(); return; }
  rpc("session/list", { _request: {} }).then(function(v){
    var items = v.items || [];
    noteSubagents(items);
    var me = items.find(function(x){ return x.sessionId === curSid; });
    sessions.forEach(function(s){ var f = items.find(function(x){ return x.sessionId === s.sessionId; }); if(f){ s.running = f.running; s.updatedAt = f.updatedAt; } });
    renderSessions();
    if(me && me.running){
      // 子代理会话的 prompt/cancel 由 subagent routing 持有，瘦客户端不提供中断
      $("cancelBtn").style.display = isSub(curSid) ? "none" : "block";
      return fetchPage(curSid, cursor[curSid] || 9007199254740991).then(function(pg){ renderEvents(pg.records || []); });
    }else{
      $("cancelBtn").style.display = "none";
    }
  }).catch(function(){}).then(function(){ schedulePoll(); });
}

function send(){
  var text = $("input").value.trim();
  if(!text || sending) return;
  if(!curSid){ showErr("请先选择或新建一个会话"); return; }
  if(isSub(curSid)){ showErr("子代理会话由父会话驱动（session/agent-busy: owned by subagent routing），瘦客户端只读。请打开父会话继续，或回官方 UI 操作。"); return; }
  sending = true;
  setStatus("发送中…");
  rpc("session/prompt", { request: { requestId: "tray-" + Date.now(), sessionId: curSid, mode: "queue", content: [{ type: "text", text: text }], clientTimeZone: "Asia/Shanghai" } })
    .then(function(){
      $("input").value = "";
      setStatus("已发送，等待回复…");
      return fetchPage(curSid, cursor[curSid] || 9007199254740991).then(function(pg){ renderEvents(pg.records || []); });
    })
    .catch(function(err){ showErr("发送失败：" + errText(err)); setStatus("发送失败"); })
    .then(function(){ sending = false; schedulePoll(); });
}
function cancel(){
  if(!curSid || isSub(curSid)) return;
  rpc("session/cancel", { request: { sessionId: curSid } })
    .then(function(){ setStatus("已请求中断"); })
    .catch(function(err){ showErr("中断失败：" + errText(err)); });
}
function createSession(){
  setStatus("新建会话…");
  rpc("session/create", { request: {} }).then(function(v){
    if(v && v.sessionId){ curSid = v.sessionId; cursor[v.sessionId] = 0; $("msgs").innerHTML = "<div class='hint'>新会话已创建，直接输入消息</div>"; }
    return loadSessions(curSid);
  }).catch(function(err){ showErr("新建会话失败：" + errText(err)); setStatus("新建失败"); });
}

$("refresh").onclick = function(){ loadSessions(curSid); };
$("create").onclick = createSession;
$("official").onclick = function(){ window.chrome.webview.postMessage({ id: 0, method: "open-url", args: { url: "http://127.0.0.1:3080/" } }); };
$("send").onclick = send;
$("cancelBtn").onclick = cancel;
$("input").addEventListener("keydown", function(e){
  if(e.key === "Enter" && !e.shiftKey){ e.preventDefault(); send(); }
});


;

// ===== 模型选择功能 =====
let modelDirectory = null;
let currentModelSelection = null;

async function loadModels() {
  try {
    setStatus('加载模型列表...');
    const data = await rpc('session/models', { sessionId: curSid });
    modelDirectory = data;
    renderModelMenu();
    updateCurrentModelDisplay();
  } catch (err) {
    console.error('加载模型失败:', err);
    $('modelMenu').innerHTML = '<div class="model-error">加载失败</div>';
  }
}

function renderModelMenu() {
  if (!modelDirectory || !modelDirectory.groups) {
    $('modelMenu').innerHTML = '<div class="model-error">无可用模型</div>';
    return;
  }
  
  let html = '';
  modelDirectory.groups.forEach(group => {
    html += '<div class="model-group">';
    html += '<div class="model-group-title">' + group.provider + '</div>';
    group.models.forEach(model => {
      const isSelected = currentModelSelection && 
                         currentModelSelection.provider === group.provider && 
                         currentModelSelection.model === model.model;
      html += '<div class="model-item' + (isSelected ? ' selected' : '') + '" data-provider="' + group.provider + '" data-model="' + model.model + '">';
      html += '<span class="name">' + model.model + '</span>';
      if (model.defaultEffort) {
        html += '<span class="effort">' + model.defaultEffort + '</span>';
      }
      html += '</div>';
    });
    html += '</div>';
  });
  
  $('modelMenu').innerHTML = html;
  
  // 绑定点击事件
  document.querySelectorAll('.model-item').forEach(item => {
    item.onclick = function() {
      const provider = this.getAttribute('data-provider');
      const model = this.getAttribute('data-model');
      selectModel(provider, model);
    };
  });
}

async function selectModel(provider, model) {
  try {
    setStatus('切换模型...');
    await rpc('session/selectModel', {
      sessionId: curSid,
      provider: provider,
      model: model
    });
    currentModelSelection = { provider, model };
    updateCurrentModelDisplay();
    $('modelMenu').classList.remove('open');
    setStatus('已切换模型');
  } catch (err) {
    showErr('切换模型失败：' + errText(err));
  }
}

function updateCurrentModelDisplay() {
  const el = $('currentModel');
  if (currentModelSelection) {
    el.textContent = currentModelSelection.model;
  } else {
    el.textContent = '选择模型';
  }
}

// 模型按钮点击事件
$('modelBtn').onclick = function() {
  const menu = $('modelMenu');
  if (menu.classList.contains('open')) {
    menu.classList.remove('open');
  } else {
    if (!modelDirectory) {
      loadModels();
    }
    menu.classList.add('open');
  }
};

// 点击其他地方关闭菜单
document.addEventListener('click', function(e) {
  if (!e.target.closest('#modelSeat')) {
    $('modelMenu').classList.remove('open');
  }
});


// ===== 审批 UI 功能 =====
let pendingApprovals = new Map();

function renderApprovalCard(approvalId, request) {
  const card = document.createElement('div');
  card.className = 'approval-card';
  card.id = 'approval-' + approvalId;
  
  let desc = request.description || request.message || '需要审批的操作';
  if (request.command) {
    desc += '\n\n命令：' + request.command;
  }
  if (request.filePath) {
    desc += '\n文件：' + request.filePath;
  }
  
  // 使用 innerHTML 设置内容
  const html = '<h4>⚠️ 需要审批</h4>' +
    '<div class="desc">' + desc + '</div>' +
    '<div class="approval-actions">' +
    '<button class="approve" onclick="handleApproval(\'' + approvalId + '\', true)">✅ 批准</button>' +
    '<button class="reject" onclick="handleApproval(\'' + approvalId + '\', false)">❌ 拒绝</button>' +
    '</div>';
  card.innerHTML = html;
  
  // 插入到消息流中
  const msgs = $('msgs');
  msgs.appendChild(card);
  msgs.scrollTop = msgs.scrollHeight;
  
  pendingApprovals.set(approvalId, request);
}

async function handleApproval(approvalId, approved) {
  try {
    const card = $('approval-' + approvalId);
    if (card) {
      card.innerHTML = '<h4>' + (approved ? '✅ 已批准' : '❌ 已拒绝') + '</h4>';
    }
    
    await rpc('approval/answer', {
      sessionId: curSid,
      approvalId: approvalId,
      approved: approved
    });
    
    pendingApprovals.delete(approvalId);
    setStatus(approved ? '已批准' : '已拒绝');
  } catch (err) {
    showErr('审批失败：' + errText(err));
  }
}

// 监听审批请求（在轮询中检查）
async function checkApprovals() {
  try {
    const data = await rpc('approval/list', { sessionId: curSid });
    if (data && data.approvals) {
      data.approvals.forEach(approval => {
        if (!pendingApprovals.has(approval.approvalId)) {
          renderApprovalCard(approval.approvalId, approval);
        }
      });
    }
  } catch (err) {
    // 静默失败，不影响主流程
    console.debug('检查审批失败:', err);
  }
}


// ===== 工作区分组功能 =====
let workspaces = [];
let workspaceMap = new Map(); // workspaceId -> workspace

async function loadWorkspaces() {
  try {
    const data = await rpc('workspace/baseline', {});
    if (data && data.items) {
      workspaces = data.items;
      workspaceMap.clear();
      workspaces.forEach(ws => {
        workspaceMap.set(ws.workspaceId, ws);
      });
    }
  } catch (err) {
    console.error('加载工作区失败:', err);
    // 如果加载失败，使用空数组
    workspaces = [];
  }
}

function renderWorkspaceSessions() {
  // 重新渲染会话列表，按工作区分组
  const side = $('side');
  let html = '';
  
  // 按工作区分组
  workspaces.forEach(ws => {
    const sessions = sessions.filter(s => ws.sessionIds.includes(s.sessionId));
    if (sessions.length === 0) return;
    
    html += '<div class="workspace-group">';
    html += '<div class="workspace-header">';
    html += '<span class="title">' + ws.title + '</span>';
    html += '<span class="path">' + ws.path + '</span>';
    html += '</div>';
    html += '<div class="workspace-sessions">';
    
    sessions.forEach(s => {
      html += renderSessionItem(s);
    });
    
    html += '</div>';
    html += '</div>';
  });
  
  // 未分配工作区的会话
  const unassigned = sessions.filter(s => !workspaces.some(ws => ws.sessionIds.includes(s.sessionId)));
  if (unassigned.length > 0) {
    html += '<div class="workspace-group">';
    html += '<div class="workspace-header">';
    html += '<span class="title">未分配</span>';
    html += '</div>';
    html += '<div class="workspace-sessions">';
    
    unassigned.forEach(s => {
      html += renderSessionItem(s);
    });
    
    html += '</div>';
    html += '</div>';
  }
  
  side.innerHTML = html;
  
  // 重新绑定事件
  document.querySelectorAll('.sess').forEach(el => {
    el.onclick = function() {
      openSession(this.getAttribute('data-sid'));
    };
  });
}

function renderSessionItem(s) {
  const title = s.title || s.sessionId.substring(0, 8) + '...';
  const time = s.updatedAt ? new Date(s.updatedAt).toLocaleString() : '';
  const isActive = s.sessionId === curSid;
  const isRunning = s.running ? 'run' : '';
  const isSub = s.origin === 'subagent' ? '<span class="subbadge">子</span>' : '';
  
  return '<div class="sess' + (isActive ? ' active' : '') + '" data-sid="' + s.sessionId + '">';
  return '<span class="dot ' + isRunning + '"></span>' + isSub + '<div class="t">' + title + '</div>';
  return '<div class="m">' + time + '</div>';
  return '</div>';
}



loadSessions("").then(function(){ schedulePoll(); });
})();</script>
</body>
</html>
""";
}