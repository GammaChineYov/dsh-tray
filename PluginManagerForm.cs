using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QwenTray;

// 插件管理弹窗：列出 bundle 插件（dump-config 合成树）与 super-injector 注入插件（registry.json），
// 勾选 = 启用；取消勾选 = 加入托盘禁用清单。「应用」写 cfg + 重建 --patch overlay + registry 手术；
// 「应用并重启 DSH」额外执行 DshRestart（托盘进程外干净重启，避开会话内 restart 撞 3080 的坑）。
// 官方白名单条目（dsh-base/dsh-web-app/用户层官方包）锁定不可禁用。
public class PluginManagerForm : Form {
  public class State {
    public PluginInventory Inv = new();
    public bool SafeMode;
    public HashSet<string> DisabledEntries = new();     // entry id 级禁用清单（自定义模式）
    public HashSet<string> DisabledDev = new();          // 注入插件禁用清单（包名）
  }

  readonly State st;
  public State St => st;   // 外部（托盘）重扫后回写 Inv/SafeMode 再 Populate
  readonly Func<bool, HashSet<string>, HashSet<string>, string> onApply;   // (safeMode, disabledEntries, disabledDev) -> 结果说明
  readonly Action onRestart;
  readonly Action<bool> onSafeModeChanged;
  readonly ListView list;
  readonly CheckBox chkSafe;
  readonly Label note;
  readonly TextBox logBox;
  readonly Button btnApply, btnApplyRestart, btnRefresh;   // 提升为字段：Apply 需要异步恢复它们的可用状态

  public PluginManagerForm(State st, Func<bool, HashSet<string>, HashSet<string>, string> onApply, Action onRestart, Action<bool> onSafeModeChanged, Icon icon){
    this.st = st; this.onApply = onApply; this.onRestart = onRestart; this.onSafeModeChanged = onSafeModeChanged;
    Text = "DSH 插件管理"; if(icon != null) Icon = icon;
    Size = new Size(760, 560); StartPosition = FormStartPosition.CenterScreen; MinimizeBox = false; MaximizeBox = false;

    chkSafe = new CheckBox{
      Text = "安全模式启动（仅官方插件白名单，屏蔽全部外部 bundle 与注入插件）",
      Left = 12, Top = 10, Width = 720, Checked = st.SafeMode
    };
    chkSafe.CheckedChanged += (s, e) => { st.SafeMode = chkSafe.Checked; onSafeModeChanged(chkSafe.Checked); RefreshListEnabled(); };

    list = new ListView{ Left = 12, Top = 38, Width = 720, Height = 380, View = View.Details, CheckBoxes = true, FullRowSelect = true, GridLines = true };
    list.Columns.Add("插件", 240);
    list.Columns.Add("来源", 110);
    list.Columns.Add("entry id", 170);
    list.Columns.Add("当前状态", 180);
    list.ItemCheck += (s, e) => {
      if(e.CurrentValue == CheckState.Unchecked) return; // 勾选动作无需拦截
    };

    btnApply = new Button{ Text = "应用", Left = 12, Top = 428, Width = 110, Height = 30 };
    btnApplyRestart = new Button{ Text = "应用并重启 DSH", Left = 132, Top = 428, Width = 150, Height = 30 };
    btnRefresh = new Button{ Text = "重新扫描", Left = 292, Top = 428, Width = 100, Height = 30 };
    var btnClose = new Button{ Text = "关闭", Left = 632, Top = 428, Width = 100, Height = 30 };
    btnApply.Click += (s, e) => Apply(false);
    btnApplyRestart.Click += (s, e) => Apply(true);
    btnRefresh.Click += (s, e) => { CollectChecks(); if(RefreshAll != null) RefreshAll(); };
    btnClose.Click += (s, e) => Close();

    note = new Label{ Left = 402, Top = 433, Width = 220, Height = 24, ForeColor = Color.Gray, Text = "启停在下次启动 DSH 后生效" };
    logBox = new TextBox{ Left = 12, Top = 466, Width = 720, Height = 70, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

    Controls.Add(chkSafe); Controls.Add(list); Controls.Add(btnApply); Controls.Add(btnApplyRestart);
    Controls.Add(btnRefresh); Controls.Add(btnClose); Controls.Add(note); Controls.Add(logBox);
    Populate();
  }

  public Action RefreshAll;   // 由外部赋值为"重新 Scan 并 Populate"

  public void Populate(){
    list.Items.Clear();
    // 组：外部 bundle（可调）→ 注入插件（可调）→ 官方（锁定）
    var ext = st.Inv.Entries.Where(x => !x.Official).ToList();
    var off = st.Inv.Entries.Where(x => x.Official).ToList();
    foreach(var e in ext){
      var it = new ListViewItem(e.Name.Length > 0 ? e.Name : e.Id);
      it.SubItems.Add("bundle");
      it.SubItems.Add(e.Id);
      it.SubItems.Add(e.DisabledEffective ? "已被配置禁用" : e.DisabledDynamic ? "动态禁用(!!js)" : "启用中");
      it.Checked = !(st.DisabledEntries.Contains(e.Id) || e.DisabledEffective);
      it.Tag = e;
      if(e.DisabledEffective){ it.ForeColor = Color.Gray; }
      list.Items.Add(it);
    }
    foreach(var d in st.Inv.DevPlugins){
      var it = new ListViewItem(d.Name);
      it.SubItems.Add("注入(dev)");
      it.SubItems.Add("-");
      it.SubItems.Add(d.DisabledByTray ? "已被托盘禁用" : "注入中（重启 DSH 加载）");
      it.Checked = !d.DisabledByTray;
      it.Tag = d;
      if(d.DisabledByTray) it.ForeColor = Color.Gray;
      list.Items.Add(it);
    }
    foreach(var e in off){
      var it = new ListViewItem(e.Name.Length > 0 ? e.Name : e.Id);
      it.SubItems.Add("官方(锁定)");
      it.SubItems.Add(e.Id);
      it.SubItems.Add(e.DisabledEffective ? "已被配置禁用" : "白名单");
      it.Checked = true;
      it.ForeColor = Color.Gray;
      it.Tag = "locked";
      list.Items.Add(it);
    }
    if(!st.Inv.DumpOk) Log("dump-config 不可用：" + st.Inv.DumpError + "（entry 列表为静态推导）");
    foreach(var n in st.Inv.Notes) Log(n);
    RefreshListEnabled();
  }

  void RefreshListEnabled(){
    // 安全模式开启时外部项全部不可勾选（一律被白名单屏蔽），仅注入插件清单与说明可看
    foreach(ListViewItem it in list.Items){
      if(it.Tag is string) { continue; }
      it.ForeColor = st.SafeMode ? Color.Gray : (it.Checked ? Color.Black : it.ForeColor);
    }
  }

  void CollectChecks(){
    st.DisabledEntries.Clear();
    st.DisabledDev.Clear();
    foreach(ListViewItem it in list.Items){
      if(it.Tag is PluginEntryInfo e && !it.Checked && !e.DisabledEffective) st.DisabledEntries.Add(e.Id);
      if(it.Tag is DevPluginInfo d && !it.Checked) st.DisabledDev.Add(d.Name);
    }
  }

  // S0-⑥（报告 P1-4）：原来在 UI 线程同步调 onApply —— 它内部会跑 dump-config 子进程（WaitForExit 上限 60s），
  // 期间托盘菜单点不开、日志窗口不刷新。现在把 onApply 整体丢到后台线程；其中真正碰 UI 的少数动作
  // （菜单勾选刷新）由 TrayApp 侧自行 marshal 回 UI 线程，这里只负责按钮忙闲状态与结果回贴。
  void Apply(bool restart){
    CollectChecks();
    var sm = st.SafeMode;
    var de = new HashSet<string>(st.DisabledEntries, StringComparer.OrdinalIgnoreCase);
    var dd = new HashSet<string>(st.DisabledDev, StringComparer.OrdinalIgnoreCase);
    SetBusy(true);
    Log("（后台执行中：落配置 → registry 手术 → 重扫插件 → 重建 patch overlay）");
    Task.Run(() => {
      string msg;
      try { msg = onApply(sm, de, dd); } catch (Exception ex) { msg = "应用失败: " + ex.Message; }
      Action done = () => {
        try {
          SetBusy(false);
          Log(msg);
          if (restart && onRestart != null) { Log("正在重启 DSH（杀 3080 → 带 patch 重启）…"); onRestart(); }
        } catch { }
      };
      try { if (IsHandleCreated) BeginInvoke(done); else done(); } catch { }
    });
  }

  void SetBusy(bool busy){
    try { btnApply.Enabled = !busy; btnApplyRestart.Enabled = !busy; btnRefresh.Enabled = !busy; } catch { }
  }

  public void Log(string s){ try{ logBox.AppendText(s + "\r\n"); }catch{} }
}
