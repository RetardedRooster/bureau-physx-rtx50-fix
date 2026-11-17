// "AI-felskálázott átvezetők" tab: optional AI-upscaled cutscenes – install a downloaded video pack, or upscale the game's own videos locally
// (Real-ESRGAN on the GPU + DOF protection + Bink 1, per-movie recipes). The work is done by BureauVideoTool.exe /
// BureauBink32.exe (bureau/video/BureauVideo.cs), embedded here and extracted to %LOCALAPPDATA%\BureauPhysXSetup\video.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class CutscenesTab {
  const string RadPage = "https://www.radgametools.com/bnkdown.htm";
  readonly Func<string> game;
  readonly Action<string> log;
  readonly Control owner;
  Label state = new Label(), esrState = new Label(), radState = new Label(), progressText = new Label();
  TextBox workBox = new TextBox();
  Button packBtn = new Button(), startBtn = new Button(), stopBtn = new Button(), restoreBtn = new Button(),
         esrBtn = new Button(), radPageBtn = new Button(), radBrowse = new Button(), workBrowse = new Button();
  ProgressBar bar = new ProgressBar();
  Process proc;
  string radPath, runningWork;
  DateTime runStart; long runFirstDone = -1, lastDone, lastTotal;

  static string AppDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BureauPhysXSetup"); } }
  static string ToolDir { get { return Path.Combine(AppDir, "video"); } }
  static string EsrganDir { get { return Path.Combine(AppDir, "realesrgan"); } }
  static string SettingsFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BureauPhysXSetup", "video.txt"); } }

  public CutscenesTab(TabPage page, Control owner, Func<string> game, Action<string> log) {
    this.owner = owner; this.game = game; this.log = log;
    var intro = new Label {
      Text = L.T("Opcionális: a játék 47 átvezetője 1280×736 helyett 2560×1472-ben, mesterséges intelligenciás felskálázással (Real-ESRGAN). " +
                 "A szándékosan elmosott részek (mélységélesség, mozgás) az eredeti szerint maradnak. Az eredeti videók mentésre kerülnek, és visszaállíthatók."),
      Location = new Point(8, 6), Size = new Size(568, 46) };
    state.Location = new Point(8, 54); state.Size = new Size(568, 18); state.Font = new Font("Segoe UI", 9f, FontStyle.Bold);

    var gPack = new GroupBox { Text = L.T("Letöltött videócsomag"), Location = new Point(8, 74), Size = new Size(568, 56) };
    var packHint = new Label { Text = L.T("Válaszd ki bármelyik részt (…part1.zip); a többinek ugyanabban a mappában kell lennie."),
                               Location = new Point(10, 22), Size = new Size(380, 30), ForeColor = SystemColors.GrayText };
    packBtn.Text = L.T("Csomag telepítése…"); packBtn.Location = new Point(400, 20); packBtn.Size = new Size(158, 28);
    gPack.Controls.AddRange(new Control[] { packHint, packBtn });

    var gLocal = new GroupBox { Text = L.T("Helyi felskálázás (a saját gépeden)"), Location = new Point(8, 134), Size = new Size(568, 222) };
    var time = new Label {
      Text = L.T("Időigény: kb. 36 000 képkocka. Gyors, mai videókártyán kb. 16 óra, középkategóriás kártyán 1–2 nap, " +
                 "gyengébbeken több. Vulkan-képes videókártya és kb. 40 GB szabad hely kell a munkamappában. Bármikor leállítható, és később ott folytatódik, ahol abbamaradt."),
      Location = new Point(10, 18), Size = new Size(550, 46), ForeColor = Color.DarkSlateBlue };
    var wl = new Label { Text = L.T("Munkamappa:"), Location = new Point(10, 70), Size = new Size(84, 18) };
    workBox.Location = new Point(96, 67); workBox.Size = new Size(372, 23);
    workBrowse.Text = L.T("Tallózás…"); workBrowse.Location = new Point(474, 66); workBrowse.Size = new Size(84, 25);
    esrState.Location = new Point(10, 98); esrState.Size = new Size(370, 18);
    esrBtn.Text = L.T("Letöltés"); esrBtn.Location = new Point(388, 94); esrBtn.Size = new Size(170, 25);
    radState.Location = new Point(10, 126); radState.Size = new Size(280, 18);
    radPageBtn.Text = L.T("Letöltési oldal"); radPageBtn.Location = new Point(296, 122); radPageBtn.Size = new Size(110, 25);
    radBrowse.Text = L.T("radvideo64.exe…"); radBrowse.Location = new Point(412, 122); radBrowse.Size = new Size(146, 25);
    startBtn.Text = L.T("Felskálázás indítása"); startBtn.Location = new Point(10, 154); startBtn.Size = new Size(170, 28);
    stopBtn.Text = L.T("Leállítás"); stopBtn.Location = new Point(186, 154); stopBtn.Size = new Size(100, 28); stopBtn.Enabled = false;
    bar.Location = new Point(296, 156); bar.Size = new Size(262, 24); bar.Maximum = 1000;
    progressText.Location = new Point(10, 188); progressText.Size = new Size(550, 30);
    gLocal.Controls.AddRange(new Control[] { time, wl, workBox, workBrowse, esrState, esrBtn, radState, radPageBtn, radBrowse, startBtn, stopBtn, bar, progressText });

    restoreBtn.Text = L.T("Eredeti videók visszaállítása"); restoreBtn.Location = new Point(8, 362); restoreBtn.Size = new Size(210, 28);
    var note = new Label { Text = L.T("A felskálázott videók a játék tartalmából készülnek: csak saját használatra."),
                           Location = new Point(226, 364), Size = new Size(350, 28), ForeColor = SystemColors.GrayText, Font = new Font("Segoe UI", 8f) };
    page.Controls.AddRange(new Control[] { intro, state, gPack, gLocal, restoreBtn, note });

    LoadSettings();
    packBtn.Click += (s, e) => {
      using (var d = new OpenFileDialog { Filter = L.T("Videócsomag") + " (*.zip)|*.zip", Title = L.T("Videócsomag kiválasztása") })
        if (d.ShowDialog(owner) == DialogResult.OK) RunTool(new[] { "installpack", game(), d.FileName }, L.T("Videócsomag telepítése…"));
    };
    restoreBtn.Click += (s, e) => {
      if (MessageBox.Show(owner, L.T("Visszaállítsam az eredeti átvezetőket? (A felskálázott videók törlődnek a játék mappájából.)"), L.T("AI-felskálázott átvezetők"),
                          MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
        RunTool(new[] { "restore", game() }, L.T("Eredeti videók visszaállítása…"));
    };
    esrBtn.Click += (s, e) => RunTool(new[] { "get-esrgan", EsrganDir }, L.T("Real-ESRGAN letöltése (45 MB)…"));
    radPageBtn.Click += (s, e) => {
      MessageBox.Show(owner, L.T("A RAD Video Tools ingyenes, de csak a RAD Game Tools oldaláról tölthető le (a csomag jelszavát az oldal megadja). " +
                                 "Telepítsd a szokásos módon, utána a telepítő magától megtalálja; ha nem, válaszd ki a radvideo64.exe-t."),
                      L.T("RAD Video Tools"), MessageBoxButtons.OK, MessageBoxIcon.Information);
      try { Process.Start(RadPage); } catch { }
    };
    radBrowse.Click += (s, e) => {
      using (var d = new OpenFileDialog { Filter = "radvideo64.exe|radvideo64.exe", Title = L.T("radvideo64.exe kiválasztása") })
        if (d.ShowDialog(owner) == DialogResult.OK) { radPath = d.FileName; SaveSettings(); Refresh(); }
    };
    workBrowse.Click += (s, e) => {
      using (var d = new FolderBrowserDialog { Description = L.T("Munkamappa a felskálázáshoz (kb. 40 GB szabad hely)") })
        if (d.ShowDialog(owner) == DialogResult.OK) { workBox.Text = d.SelectedPath; SaveSettings(); Refresh(); }
    };
    workBox.Leave += (s, e) => { SaveSettings(); Refresh(); };
    startBtn.Click += (s, e) => Start();
    stopBtn.Click += (s, e) => Stop();
    Refresh();
  }

  public bool Running { get { return proc != null && !proc.HasExited; } }

  // ---------------------------------------------------------------------------------------------------------- state
  void LoadSettings() {
    try {
      foreach (var l in File.ReadAllLines(SettingsFile)) {
        var i = l.IndexOf('='); if (i < 0) continue;
        var k = l.Substring(0, i); var v = l.Substring(i + 1);
        if (k == "work") workBox.Text = v; else if (k == "rad") radPath = v;
      }
    } catch { }
  }

  void SaveSettings() {
    try {
      Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
      File.WriteAllLines(SettingsFile, new[] { "work=" + workBox.Text.Trim(), "rad=" + (radPath ?? "") });
    } catch { }
  }

  static string FindRad() {
    foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\RADVideo", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RADVideo" })
      foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        try {
          using (var k = hive.OpenSubKey(root)) {
            if (k == null) continue;
            foreach (var val in new[] { "InstallLocation", "UninstallString", "DisplayIcon" }) {
              var v = (k.GetValue(val) as string ?? "").Trim('"', ' ');
              if (v.Length == 0) continue;
              var dir = Directory.Exists(v) ? v : Path.GetDirectoryName(v);
              var exe = Path.Combine(dir, "radvideo64.exe");
              if (File.Exists(exe)) return exe;
            }
          }
        } catch { }
    foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
      foreach (var sub in new[] { "RADVideo", "RAD Video Tools" }) {
        var exe = Path.Combine(pf, sub, "radvideo64.exe");
        if (File.Exists(exe)) return exe;
      }
    return null;
  }

  public void Refresh() {
    var g = game();
    bool busy = Running;
    if (g == null) { state.Text = L.T("A játék nem található."); state.ForeColor = Color.DarkRed; }
    else {
      int hd = 0, orig = 0, total = 0;
      var movies = Path.Combine(g, "XGame", "Movies");
      foreach (var r in Recipes.Table) {
        var p = Path.Combine(movies, r[0] + ".bik"); if (!File.Exists(p)) continue;
        total++; int w = BinkWidth(p); if (w >= 2560) hd++; else orig++;
      }
      state.Text = L.F("Állapot: {0} / {1} átvezető AI-felskálázott, {2} eredeti.", hd, total, orig);
      state.ForeColor = hd == total && total > 0 ? Color.DarkGreen : SystemColors.ControlText;
    }
    if (string.IsNullOrEmpty(workBox.Text) && g != null) workBox.Text = Path.Combine(Path.GetPathRoot(g), "BureauAIUpscaleWork");
    bool esr = File.Exists(Path.Combine(EsrganDir, "realesrgan-ncnn-vulkan.exe"));
    esrState.Text = esr ? L.T("Real-ESRGAN: megvan.") : L.T("Real-ESRGAN: hiányzik (ingyenes, BSD-licenc, GitHub).");
    esrState.ForeColor = esr ? Color.DarkGreen : Color.DarkRed;
    if (radPath == null || !File.Exists(radPath)) radPath = FindRad();
    bool rad = radPath != null && File.Exists(radPath);
    radState.Text = rad ? L.T("RAD Video Tools: megvan.") : L.T("RAD Video Tools: hiányzik.");
    radState.ForeColor = rad ? Color.DarkGreen : Color.DarkRed;
    esrBtn.Enabled = !esr && !busy;
    packBtn.Enabled = restoreBtn.Enabled = g != null && !busy;
    startBtn.Enabled = g != null && esr && rad && !busy;
    stopBtn.Enabled = busy && runningWork != null;
    radBrowse.Enabled = workBrowse.Enabled = workBox.Enabled = !busy;
  }

  static int BinkWidth(string path) {
    try { using (var r = new BinaryReader(File.OpenRead(path))) { r.ReadBytes(20); return r.ReadInt32(); } } catch { return 0; }
  }

  // ------------------------------------------------------------------------------------------------------------ run
  static string ExtractTools() {
    Directory.CreateDirectory(ToolDir);
    foreach (var n in new[] { "BureauVideoTool.exe", "BureauBink32.exe" }) {
      using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(n)) {
        var ms = new MemoryStream(); s.CopyTo(ms); var bytes = ms.ToArray();
        var dst = Path.Combine(ToolDir, n);
        if (!File.Exists(dst) || !File.ReadAllBytes(dst).SequenceEqual(bytes)) File.WriteAllBytes(dst, bytes);
      }
    }
    return Path.Combine(ToolDir, "BureauVideoTool.exe");
  }

  void Start() {
    var g = game(); var work = workBox.Text.Trim();
    if (g == null || work.Length == 0) return;
    if (Process.GetProcessesByName("TheBureau").Length > 0) {
      MessageBox.Show(owner, L.T("Előbb lépj ki a játékból."), L.T("AI-felskálázott átvezetők"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
    }
    try {
      Directory.CreateDirectory(work);
      var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(work))).AvailableFreeSpace;
      if (free < 40L << 30 &&
          MessageBox.Show(owner, L.F("A munkamappa meghajtóján csak {0} GB szabad hely van (kb. 40 GB ajánlott). Folytatod?", free >> 30), L.T("AI-felskálázott átvezetők"),
                          MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
    } catch (Exception ex) { log(L.F("HIBA: {0}", ex.Message)); return; }
    SaveSettings();
    runningWork = work;
    RunTool(new[] { "upscale", g, work, EsrganDir, radPath }, L.T("Helyi felskálázás indul…"));
  }

  void Stop() {
    if (runningWork == null || !Running) return;
    try { File.WriteAllText(Path.Combine(runningWork, "stop"), ""); } catch { }
    log(L.T("Leállítás… (a következő indításkor ott folytatódik, ahol abbamaradt)"));
    stopBtn.Enabled = false;
  }

  // Closing the window while the upscale runs: stop it (resumable).
  public bool ConfirmClose() {
    if (!Running) return true;
    if (MessageBox.Show(owner, L.T("A felskálázás még fut. Leállítsam? Később ott folytatódik, ahol abbamaradt."), L.T("AI-felskálázott átvezetők"),
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return false;
    Stop();
    if (proc != null) proc.WaitForExit(15000);
    return true;
  }

  void RunTool(string[] args, string title) {
    if (Running) return;
    string exe;
    try { exe = ExtractTools(); } catch (Exception ex) { log(L.F("HIBA: {0}", ex.Message)); return; }
    log(title);
    var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(a => "\"" + a.TrimEnd('\\') + "\"")))
      { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = ToolDir };
    runStart = DateTime.Now; runFirstDone = -1; lastDone = lastTotal = 0;
    bar.Value = 0; progressText.Text = "";
    proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
    proc.OutputDataReceived += (s, e) => { if (e.Data != null) owner.BeginInvoke((Action)(() => OnLine(e.Data))); };
    proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) owner.BeginInvoke((Action)(() => log(e.Data))); };
    proc.Exited += (s, e) => owner.BeginInvoke((Action)(() => { runningWork = null; Refresh(); }));
    try { proc.Start(); proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); }
    catch (Exception ex) { log(L.F("HIBA: {0}", ex.Message)); proc = null; runningWork = null; }
    Refresh();
  }

  void OnLine(string l) {
    if (!l.StartsWith("@")) { log(l); return; }
    var f = l.Split(' ');
    switch (f[0]) {
      case "@PROGRESS": {
        long done = long.Parse(f[1]), total = long.Parse(f[2]);
        if (runFirstDone < 0) { runFirstDone = done; runStart = DateTime.Now; }
        lastDone = done; lastTotal = total;
        bar.Value = total > 0 ? (int)Math.Min(1000, done * 1000 / total) : 0;
        string eta = L.T("becslés az első adag után…");
        if (done > runFirstDone && total > done) {
          var secs = (DateTime.Now - runStart).TotalSeconds / (done - runFirstDone) * (total - done) * 1.05;   // +5%: encoding
          var ts = TimeSpan.FromSeconds(secs);
          eta = ts.TotalHours >= 1 ? L.F("hátra kb. {0} óra {1} perc", (int)ts.TotalHours, ts.Minutes) : L.F("hátra kb. {0} perc", Math.Max(1, (int)Math.Ceiling(ts.TotalMinutes)));
        } else if (total > 0 && done >= total) eta = L.T("kódolás és telepítés…");
        progressText.Text = L.F("{0:N0} / {1:N0} képkocka ({2:0.0}%) – {3}", done, total, total > 0 ? done * 100.0 / total : 0, eta);
        break;
      }
      case "@MOVIE":
        if (runningWork == null) { progressText.Text = L.F("{0} / {1}: {2}", f[1], f[2], f[3]); bar.Value = (int)Math.Min(1000, long.Parse(f[1]) * 1000 / Math.Max(1, long.Parse(f[2]))); }
        else log(L.F("Videó {0} / {1}: {2}", f[1], f[2], f[3]));
        break;
      case "@DONE":
        log(f[1] == "ok" ? L.T("Kész.") : f[1] == "stopped" ? L.T("Leállítva.") : L.T("Hiba történt (részletek fent)."));
        if (f[1] == "ok" && runningWork != null) { bar.Value = 1000; progressText.Text = L.T("Minden átvezető kész."); }
        Refresh();
        break;
    }
  }
}
