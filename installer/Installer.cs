// The Bureau: XCOM Declassified – GPU PhysX telepítő (GTX 900-tól minden NVIDIA GPU; RTX 50-en tesztelve).
//
// Mit csinál (lásd bureau/install.sh és CLAUDE.md "B." szakasz):
//  1. a beágyazott nvcuda.dll proxyt a TheBureau.exe mellé másolja;
//  2. a játék eredeti APEX/PhysX3Gpu DLL-jeinek minden CUDA-fatbinjéhez megkeresi az NVIDIA PhysX System Software
//     (Engine\73BE2921551D) PTX-es párját, ellenőrzi, hogy a kernelek felülete azonos, és cuda_ptx\<hash>.ptx néven
//     kiírja (a proxy ezt tölti be a régi sm_30-as kód helyett);
//  3. létrehozza a cuda_fake_cc jelzőt (compute capability 3.0 a játéknak);
//  4. a PhysXCore.dll-t (PhysX 2.8.4) az ugyanebben a mappában lévő újabb NVIDIA-változatra cseréli (+ cudart32_65.dll),
//     az eredetit a _orig_physx_core mappába menti;
//  5. az XEngine.ini-ben beállítja a részecskeszámot (ParticlePercentage) és a PhysX-memóriát.
// Fordítás: bureau/build.sh (csc, .NET Framework 4, C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

static class Program {
  public static bool Restart;   // set by the language selector: rebuild the window in the new language
  public static int StartTab;   // "--tab n": open on that tab (testing)
  public static string StartGame;   // "--game <dir>": use this game folder (testing)

  [STAThread]
  static int Main(string[] args) {
    L.Load();
    // Elevated helper mode (started by Setup.RunElevatedNv with "runas"): "--nv <errfile> <hexid>=<value> ...",
    // writes DWORD settings on thebureau.exe's driver profile (value -1 = delete the user value).
    if (args.Length >= 3 && args[0] == "--nv") {
      try {
        var ids = new uint[args.Length - 2];
        var vals = new int[args.Length - 2];
        for (int i = 2; i < args.Length; i++) {
          var kv = args[i].Split('=');
          ids[i - 2] = Convert.ToUInt32(kv[0], 16);
          vals[i - 2] = int.Parse(kv[1]);
        }
        Nvdrs.SetDwords("thebureau.exe", "The Bureau: XCOM Declassified", ids, vals);
        return 0;
      } catch (Exception ex) {
        try { File.WriteAllText(args[1], ex.Message); } catch { }
        return 1;
      }
    }
    for (int i = 0; i + 1 < args.Length; i++) {
      if (args[i] == "--tab") int.TryParse(args[i + 1], out StartTab);
      if (args[i] == "--game") StartGame = args[i + 1];
    }
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    do { Restart = false; Application.Run(new SetupForm()); } while (Restart);
    return 0;
  }
}

// ---------------------------------------------------------------- fatbin pairing (port of tools/PairFatbins.java)
static class Fatbin {
  public static List<byte[]> Extract(byte[] d) {
    var r = new List<byte[]>();
    for (int i = 0; i + 16 < d.Length; i++) {
      if (BitConverter.ToUInt32(d, i) != 0xBA55ED50u) continue;
      int hs = BitConverter.ToUInt16(d, i + 6);
      long fs = BitConverter.ToInt64(d, i + 8);
      if (fs <= 0 || i + hs + fs > d.Length) continue;
      var f = new byte[hs + fs];
      Buffer.BlockCopy(d, i, f, 0, f.Length);
      r.Add(f);
    }
    return r;
  }

  public static byte[] Cubin(byte[] f, int arch) {
    int hs = BitConverter.ToUInt16(f, 6);
    long end = hs + BitConverter.ToInt64(f, 8);
    long p = hs;
    while (p + 64 <= end) {
      int kind = BitConverter.ToUInt16(f, (int)p);
      int ehs = BitConverter.ToInt32(f, (int)p + 4);
      long sz = BitConverter.ToInt64(f, (int)p + 8);
      int ar = BitConverter.ToInt32(f, (int)p + 28);
      if (ehs <= 0 || ehs > 4096) break;
      if (kind == 2 && ar == arch) {
        var c = new byte[sz];
        Buffer.BlockCopy(f, (int)(p + ehs), c, 0, (int)sz);
        return c;
      }
      p += ehs + sz;
    }
    return null;
  }

  static string CStr(byte[] e, int o) {
    int z = o;
    while (z < e.Length && e[z] != 0) z++;
    return Encoding.ASCII.GetString(e, o, z - o);
  }

  // Kernel interface: per kernel the parameter records (EIATTR_KPARAM_INFO 0x17, CBANK_PARAM_SIZE 0x19,
  // PARAM_CBANK 0x0a without its symbol index), constant bank 0 sizes, and the names of data symbols.
  public static SortedDictionary<string, string> Interface(byte[] e) {
    var m = new SortedDictionary<string, string>(StringComparer.Ordinal);
    int shoff = BitConverter.ToInt32(e, 0x20);
    int se = BitConverter.ToUInt16(e, 0x2e), sn = BitConverter.ToUInt16(e, 0x30), ss = BitConverter.ToUInt16(e, 0x32);
    int stro = BitConverter.ToInt32(e, shoff + ss * se + 0x10);
    int symOff = -1, symSz = 0, symStr = 0;
    for (int i = 0; i < sn; i++) {
      int s = shoff + i * se;
      string n = CStr(e, stro + BitConverter.ToInt32(e, s));
      int type = BitConverter.ToInt32(e, s + 4), off = BitConverter.ToInt32(e, s + 0x10), sz = BitConverter.ToInt32(e, s + 0x14);
      if (type == 2) { symOff = off; symSz = sz; symStr = BitConverter.ToInt32(e, shoff + BitConverter.ToInt32(e, s + 0x18) * se + 0x10); }
      if (n.StartsWith(".nv.info.")) {
        var h = new StringBuilder();
        int q = off;
        while (q + 4 <= off + sz) {
          int fmt = e[q], attr = e[q + 1], len = fmt == 4 ? BitConverter.ToUInt16(e, q + 2) : 0;
          if (fmt == 4 && q + 4 + len <= off + sz) {
            if (attr == 0x0a && len >= 8) h.Append("cbank:").Append(BitConverter.ToUInt16(e, q + 8)).Append('/').Append(BitConverter.ToUInt16(e, q + 10)).Append(',');
            else if (attr == 0x17 || attr == 0x19) { h.Append(attr.ToString("x2")).Append(':').Append(BitConverter.ToString(e, q + 4, len)).Append(';'); }
          } else if (fmt == 3 && attr == 0x19) {
            h.Append("19:").Append(BitConverter.ToUInt16(e, q + 2)).Append(';');
          }
          q += 4 + len;
        }
        m["K " + n.Substring(9)] = h.ToString();
      } else if (n.StartsWith(".nv.constant0.")) {
        m["C0 " + n.Substring(14)] = sz.ToString();
      }
    }
    if (symOff >= 0) {
      for (int q = symOff; q + 16 <= symOff + symSz; q += 16) {
        string n = CStr(e, symStr + BitConverter.ToInt32(e, q));
        int t = e[q + 12] & 0xf;
        if (n.Length == 0 || n.StartsWith(".")) continue;
        if (t == 0 || t == 1) m["S " + n] = "type" + t + " size" + BitConverter.ToInt32(e, q + 8);
      }
    }
    return m;
  }

  public static uint Fnv(byte[] p) {
    uint h = 2166136261u;
    foreach (byte b in p) { h ^= b; h *= 16777619u; }
    return h;
  }

  static HashSet<string> Kernels(SortedDictionary<string, string> m) {
    return new HashSet<string>(m.Keys.Where(k => k.StartsWith("K ")));
  }

  // Writes outDir\<hash>.ptx for every fatbin of oldDll whose counterpart in newDll has the same interface.
  // Returns (written, problems).
  public static int[] Pair(string oldDll, string newDll, string outDir, Action<string> log) {
    var olds = Extract(File.ReadAllBytes(oldDll));
    var news = Extract(File.ReadAllBytes(newDll));
    var newIf = new List<KeyValuePair<byte[], SortedDictionary<string, string>>>();
    foreach (var nf in news) {
      var c = Cubin(nf, 30);
      if (c != null) newIf.Add(new KeyValuePair<byte[], SortedDictionary<string, string>>(nf, Interface(c)));
    }
    int ok = 0, bad = 0;
    foreach (var of in olds) {
      var oc = Cubin(of, 30);
      if (oc == null) { log(L.F("  ! nincs sm_30 kód egy modulban: {0}", Path.GetFileName(oldDll))); bad++; continue; }
      var oi = Interface(oc);
      var ok_ = Kernels(oi);
      string hash = Fnv(of).ToString("x8");
      var match = newIf.FirstOrDefault(p => Kernels(p.Value).SetEquals(ok_));
      if (match.Key == null) { log(L.F("  ! {0}: nincs párja", hash)); bad++; continue; }
      var keys = new SortedSet<string>(oi.Keys.Concat(match.Value.Keys), StringComparer.Ordinal);
      var diffs = keys.Where(k => { string a, b; oi.TryGetValue(k, out a); match.Value.TryGetValue(k, out b); return a != b; }).ToList();
      if (diffs.Count > 0) { log(L.F("  ! {0}: eltérő felület ({1})", hash, diffs[0])); bad++; continue; }
      File.WriteAllBytes(Path.Combine(outDir, hash + ".ptx"), match.Key);
      ok++;
    }
    log(bad > 0 ? L.F("  {0}: {1} modul OK, {2} hiba", Path.GetFileName(oldDll), ok, bad) : L.F("  {0}: {1} modul OK", Path.GetFileName(oldDll), ok));
    return new[] { ok, bad };
  }
}

// ---------------------------------------------------------------- installer logic
static class Setup {
  public const string UpdateDir = @"C:\Program Files (x86)\NVIDIA Corporation\PhysX\Engine\73BE2921551D";
  public static readonly string[] ApexModules = { "BasicFS", "BasicIOS", "FieldSampler", "IOFX", "TurbulenceFS" };
  public const int ExpectedModules = 26;

  public static string FindGame() {
    var libs = new List<string>();
    var steamRoots = new List<string>();
    foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" }) {
      try {
        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key)) {
          var p = k == null ? null : k.GetValue("InstallPath") as string;
          if (p != null) steamRoots.Add(p);
        }
      } catch { }
    }
    try {
      using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam")) {
        var p = k == null ? null : k.GetValue("SteamPath") as string;
        if (p != null) steamRoots.Add(p.Replace('/', '\\'));
      }
    } catch { }
    steamRoots.Add(@"C:\Program Files (x86)\Steam");
    foreach (var root in steamRoots.Distinct()) {
      libs.Add(root);
      var vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
      if (!File.Exists(vdf)) continue;
      foreach (Match mm in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
        libs.Add(mm.Groups[1].Value.Replace(@"\\", @"\"));
    }
    foreach (var lib in libs.Distinct()) {
      var bin = Path.Combine(lib, @"steamapps\common\The Bureau\Binaries\Win32");
      if (File.Exists(Path.Combine(bin, "TheBureau.exe"))) return Path.GetFullPath(Path.Combine(bin, @"..\.."));
    }
    return null;
  }

  public static string BinDir(string game) { return Path.Combine(game, @"Binaries\Win32"); }
  public static string IniPath(string game) { return Path.Combine(game, @"XGame\Config\XEngine.ini"); }

  public static bool IsInstalled(string game) {
    var bin = BinDir(game);
    return File.Exists(Path.Combine(bin, "nvcuda.dll")) && Directory.Exists(Path.Combine(bin, "cuda_ptx"));
  }

  public static void Install(string game, int particles, int debrisIslands, int debrisShapes, Action<string> log) {
    var bin = BinDir(game);
    if (!File.Exists(Path.Combine(bin, "TheBureau.exe"))) throw new Exception(L.F("A TheBureau.exe nem található itt: {0}", bin));
    if (!Directory.Exists(UpdateDir))
      throw new Exception(L.F("Nem található az NVIDIA PhysX-frissítés:\n{0}\n\nTelepítsd az NVIDIA PhysX System Software-t (a GeForce-driverrel együtt is felkerül).", UpdateDir));
    if (!File.Exists(@"C:\Windows\SysWOW64\nvcuda.dll")) throw new Exception(L.T("Nem található a 32 bites NVIDIA CUDA-driver (SysWOW64\\nvcuda.dll)."));

    // The game's own DLLs must be the originals (an earlier manual swap would break the ABI).
    foreach (var m in ApexModules) {
      var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(bin, "APEX_" + m + "CHECKED_x86.dll")).FileVersion;
      if (v != null && v.Trim() != "1.2.3.0")
        throw new Exception(L.F("APEX_{0}CHECKED_x86.dll verziója {1}, nem az eredeti 1.2.3.0.\nÁllítsd helyre a játékfájlokat (Steam: Fájlok sértetlenségének ellenőrzése).", m, v));
    }

    log(L.T("Proxy (nvcuda.dll) másolása…"));
    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("nvcuda.dll"))
    using (var f = File.Create(Path.Combine(bin, "nvcuda.dll"))) s.CopyTo(f);
    File.WriteAllBytes(Path.Combine(bin, "cuda_fake_cc"), new byte[0]);

    log(L.T("GPU-modulok párosítása az NVIDIA PTX-es változataival…"));
    var ptx = Path.Combine(bin, "cuda_ptx");
    if (Directory.Exists(ptx)) Directory.Delete(ptx, true);
    Directory.CreateDirectory(ptx);
    int total = 0, bad = 0;
    foreach (var m in ApexModules) {
      var r = Fatbin.Pair(Path.Combine(bin, "APEX_" + m + "CHECKED_x86.dll"), Path.Combine(UpdateDir, "APEX_" + m + "CHECKED_x86.dll"), ptx, log);
      total += r[0]; bad += r[1];
    }
    var rp = Fatbin.Pair(Path.Combine(bin, "PhysX3GpuCHECKED_x86.dll"), Path.Combine(UpdateDir, @"ImplicitLibs\PhysX3GpuCHECKED_x86.dll"), ptx, log);
    total += rp[0]; bad += rp[1];
    log(L.F("Cseremodulok: {0} / {1}", total, ExpectedModules));
    if (bad > 0 || total != ExpectedModules) throw new Exception(L.F("Nem minden GPU-modul párosítható ({0}/{1}, hibák: {2}).", total, ExpectedModules, bad));

    UpdatePhysXCore(bin, log);
    EnsureDriverProfile(log);
    SetParticles(game, particles, debrisIslands, debrisShapes, log);
    log(L.T("Kész. Indítsd a játékot; a PhysX legyen bekapcsolva a menüben."));
  }

  // PhysX 2.8.4 core: the game loads the PhysXCore.dll next to TheBureau.exe. NVIDIA's update folder for the APEX
  // 1.2.3 games (the one the PTX modules come from) has a newer 2.8.4 build with sm_52 PTX; it needs cudart32_65.dll.
  // The game's own files are kept in _orig_physx_core (once), Uninstall puts them back.
  public const string CoreBackupDir = "_orig_physx_core";
  static Version DllVersion(string path) {
    if (!File.Exists(path)) return null;
    var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
    return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
  }
  public static string PhysXCoreState(string game) {
    var v = DllVersion(Path.Combine(BinDir(game), "PhysXCore.dll"));
    return v == null ? null : L.F("PhysXCore.dll: {0}", v);
  }
  static bool SameFile(string a, string b) {
    if (!File.Exists(a) || !File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length) return false;
    return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
  }
  public static void UpdatePhysXCore(string bin, Action<string> log) {
    string src = Path.Combine(UpdateDir, "PhysXCore.dll"), dst = Path.Combine(bin, "PhysXCore.dll");
    string cudaSrc = Path.Combine(UpdateDir, @"ImplicitLibs\cudart32_65.dll"), cudaDst = Path.Combine(bin, "cudart32_65.dll");
    var nv = DllVersion(src);
    var cur = DllVersion(dst);
    if (nv == null || !File.Exists(cudaSrc)) { log(L.T("PhysXCore.dll: az NVIDIA-frissítésben nincs újabb változat, a játéké marad.")); return; }
    if (cur == null) { log(L.T("PhysXCore.dll: a játék mappájában nincs ilyen fájl, kimarad.")); return; }
    if (SameFile(src, dst) && SameFile(cudaSrc, cudaDst)) { log(L.F("PhysXCore.dll: már a frissített {0} van fent.", nv)); return; }
    if (cur > nv) { log(L.F("PhysXCore.dll: a játék mappájában újabb van ({0}), marad.", cur)); return; }
    var bak = Path.Combine(bin, CoreBackupDir);
    if (!Directory.Exists(bak)) {
      Directory.CreateDirectory(bak);
      File.Copy(dst, Path.Combine(bak, "PhysXCore.dll"));
      if (File.Exists(cudaDst)) File.Copy(cudaDst, Path.Combine(bak, "cudart32_65.dll"));
    }
    File.Copy(src, dst, true);
    File.Copy(cudaSrc, cudaDst, true);
    foreach (var p in new[] { dst, cudaDst }) File.SetAttributes(p, File.GetAttributes(p) & ~FileAttributes.ReadOnly);
    log(L.F("PhysXCore.dll frissítve: {0} → {1} (+ cudart32_65.dll; az eredeti: {2}).", cur, nv, CoreBackupDir));
  }
  public static void RestorePhysXCore(string bin, Action<string> log) {
    var bak = Path.Combine(bin, CoreBackupDir);
    if (!Directory.Exists(bak)) return;
    var core = Path.Combine(bak, "PhysXCore.dll");
    if (File.Exists(core)) File.Copy(core, Path.Combine(bin, "PhysXCore.dll"), true);
    var cuda = Path.Combine(bak, "cudart32_65.dll");
    var cudaDst = Path.Combine(bin, "cudart32_65.dll");
    if (File.Exists(cuda)) File.Copy(cuda, cudaDst, true);
    else if (File.Exists(cudaDst)) File.Delete(cudaDst);
    Directory.Delete(bak, true);
    log(L.T("PhysXCore.dll: a játék eredeti példánya visszaállítva."));
  }

  // RTX 50 cards expose 32-bit CUDA only to games whose driver profile has the "32-bit GPU PhysX" switch
  // (Nvdrs.Setting32BitPhysX). The Bureau is not on NVIDIA's list, so set it like Profile Inspector would.
  // Writing driver profiles needs administrator rights: run ourselves elevated for that single step.
  public static string DriverProfileState() {
    var gpu = Nvdrs.FindRtx50();
    if (gpu == null) return null;
    int v;
    try { v = Nvdrs.Get32BitPhysX("thebureau.exe"); } catch (Exception ex) { return L.F("{0}: a driverprofil nem olvasható ({1})", gpu, ex.Message); }
    return v == 1 ? L.F("{0}: 32 bites GPU PhysX a driverprofilban BE", gpu) : L.F("{0}: 32 bites GPU PhysX a driverprofilban KI", gpu);
  }

  public static void EnsureDriverProfile(Action<string> log) {
    var gpu = Nvdrs.FindRtx50();
    if (gpu == null) { log(L.T("Nem RTX 50-es kártya: a driverprofil-kapcsoló nem kell.")); return; }
    if (Nvdrs.Get32BitPhysX("thebureau.exe") == 1) { log(L.F("{0}: a 32 bites PhysX-kapcsoló már be van kapcsolva a driverprofilban.", gpu)); return; }
    log(L.F("{0}: a 32 bites PhysX-kapcsoló bekapcsolása a driverprofilban (rendszergazdai jóváhagyás kell)…", gpu));
    RunElevatedNv(L.T("Enélkül RTX 50-en a PhysX a CPU-n fut."), Nvdrs.Setting32BitPhysX.ToString("x8") + "=1");
    if (Nvdrs.Get32BitPhysX("thebureau.exe") != 1) throw new Exception(L.T("A driverprofil-kapcsoló írása után sem olvasható vissza 1-ként."));
    log(L.T("Driverprofil: 32 bites GPU PhysX bekapcsolva (thebureau.exe)."));
  }

  // Writing driver profiles needs administrator rights: run ourselves elevated ("--nv") for that step only.
  public static void RunElevatedNv(string consequence, params string[] pairs) {
    var err = Path.Combine(Path.GetTempPath(), "bureau_nvprofile_err.txt");
    if (File.Exists(err)) File.Delete(err);
    var psi = new System.Diagnostics.ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                                                      "--nv \"" + err + "\" " + string.Join(" ", pairs)) {
      Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
    System.Diagnostics.Process p;
    try { p = System.Diagnostics.Process.Start(psi); }
    catch (System.ComponentModel.Win32Exception) {
      throw new Exception(L.F("A rendszergazdai jóváhagyás elmaradt, a driverprofil nem változott.\n{0}", consequence));
    }
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception(L.F("A driverprofil írása nem sikerült: {0}", File.Exists(err) ? File.ReadAllText(err) : L.F("kód {0}", p.ExitCode)));
  }

  // FPS cap. The game's own limiter (bSmoothFrameRate/MaxSmoothedFPS) is overridden by its in-game frame-rate
  // option (it stayed at 60 whatever the ini said), so the cap goes into the driver profile ("Max Frame Rate",
  // which the game cannot touch) and the engine limiter is switched off. 0 = unlimited; "122" = game original.
  public const int OriginalFps = 122;
  public static int ReadDriverFps() {
    try { return Nvdrs.GetDword("thebureau.exe", Nvdrs.SettingFrameRateLimiter); } catch { return -1; }
  }
  public static void ApplyDriverFps(int fps, Action<string> log) {
    int value = (fps == 0 || fps == OriginalFps) ? -1 : fps;
    if (ReadDriverFps() == (value < 0 ? -1 : value) || (value < 0 && ReadDriverFps() <= 0)) return;
    log(value < 0 ? L.T("Driverprofil: FPS-korlát törlése (rendszergazdai jóváhagyás kell)…")
                  : L.F("Driverprofil: FPS-korlát {0} fps (rendszergazdai jóváhagyás kell)…", value));
    RunElevatedNv(L.T("Az FPS-korlát nem változott."), Nvdrs.SettingFrameRateLimiter.ToString("x8") + "=" + value);
    log(value < 0 ? L.T("Driverprofil: FPS-korlát = nincs.") : L.F("Driverprofil: FPS-korlát = {0} fps.", value));
  }

  // MSAA corrupts the image in this game: always keep it off.
  public static string CurrentGame;   // set by the UI

  // 21:9 switch: exe patch first (if it cannot be applied, nothing is written), then the matching resolution:
  // on = the monitor's native resolution, off = 16:9 with the same height.
  // Physical resolution of the primary display. Screen.Bounds is DPI-virtualized for this (DPI-unaware) process:
  // e.g. at 125% scaling a 2560x1440 display shows up as 2048x1152.
  [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
  static extern bool EnumDisplaySettingsW(string device, int mode, byte[] devmode);
  public static Size PhysicalScreen() {
    var dm = new byte[220];                              // DEVMODEW
    BitConverter.GetBytes((short)220).CopyTo(dm, 68);   // dmSize
    if (EnumDisplaySettingsW(null, -1 /*ENUM_CURRENT_SETTINGS*/, dm)) {
      int w = BitConverter.ToInt32(dm, 172), h = BitConverter.ToInt32(dm, 176);   // dmPelsWidth, dmPelsHeight
      if (w > 0 && h > 0) return new Size(w, h);
    }
    return Screen.PrimaryScreen.Bounds.Size;
  }

  public static void ApplyUltrawide(string game, bool enable, Action<string> log) {
    var scr = PhysicalScreen();
    Ultrawide.Apply(game, enable, scr.Width, scr.Height, log);
    if (enable && scr.Width * 9 <= scr.Height * 16) log(L.T("21:9: a monitor nem szélesebb 16:9-nél, a javításnak nincs látható hatása."));
    int h = scr.Height, w = enable ? scr.Width : (int)Math.Round(h * 16.0 / 9.0);
    bool ro;
    bool ok = EditIni(game, t => SetValue(SetValue(t, "SystemSettings", "ResX", w.ToString()), "SystemSettings", "ResY", h.ToString()), out ro);
    if (ok) log(L.F("21:9: játékfelbontás {0}×{1}.", w, h));
    else log(L.T("Az XEngine.ini még nem létezik – indítsd el egyszer a játékot."));
  }

  public static string ForceSafe(string t) {
    t = SetValue(t, "SystemSettings", "MaxMultiSamples", "1");
    t = SetValue(t, "SystemSettings", "bAllowD3D9MSAA", "False");
    return t;
  }

  // Rewrites XEngine.ini through f(text). Keeps a one-time backup and a read-only attribute (the user may keep
  // the file read-only so the game cannot overwrite the tweaks). Returns false if the ini does not exist yet.
  public static bool EditIni(string game, Func<string, string> f, out bool readOnly) {
    readOnly = false;
    var ini = IniPath(game);
    if (!File.Exists(ini)) return false;
    var bak = ini + ".bak_physx_setup";
    if (!File.Exists(bak)) { File.Copy(ini, bak); File.SetAttributes(bak, File.GetAttributes(bak) & ~FileAttributes.ReadOnly); }
    var attrs = File.GetAttributes(ini);
    readOnly = (attrs & FileAttributes.ReadOnly) != 0;
    if (readOnly) File.SetAttributes(ini, attrs & ~FileAttributes.ReadOnly);
    try {
      var enc = Encoding.Default;
      File.WriteAllText(ini, f(File.ReadAllText(ini, enc)), enc);
    } finally {
      if (readOnly) File.SetAttributes(ini, File.GetAttributes(ini) | FileAttributes.ReadOnly);
    }
    return true;
  }

  // [section] bounds: from the header line to the next line starting with '['.
  public static bool SectionBounds(string t, string section, out int start, out int end) {
    var m = Regex.Match(t, @"(?m)^\[" + Regex.Escape(section) + @"\]\r?$");
    start = end = -1;
    if (!m.Success) return false;
    start = m.Index + m.Length;
    var n = Regex.Match(t.Substring(start), @"(?m)^\[");
    end = n.Success ? start + n.Index : t.Length;
    return true;
  }

  public static string GetValue(string t, string section, string key) {
    int s, e;
    if (!SectionBounds(t, section, out s, out e)) return null;
    var m = Regex.Match(t.Substring(s, e - s), @"(?mi)^" + Regex.Escape(key) + @"=(.*?)\r?$");
    return m.Success ? m.Groups[1].Value.Trim() : null;
  }

  public static string SetValue(string t, string section, string key, string value) {
    int s, e;
    if (!SectionBounds(t, section, out s, out e)) return t;
    var body = t.Substring(s, e - s);
    var re = new Regex(@"(?mi)^" + Regex.Escape(key) + @"=.*?(?=\r?$)");
    if (re.IsMatch(body)) body = re.Replace(body, key + "=" + value, 1);
    else {
      var trimmed = body.TrimEnd('\r', '\n');
      body = trimmed + "\r\n" + key + "=" + value + body.Substring(trimmed.Length);
    }
    return t.Substring(0, s) + body + t.Substring(e);
  }

  // APEX destruction debris: the game ships 2500 chunk islands / 0 (= unlimited) shapes. The debris is simulated on the
  // CPU, so big fights with lots of it on the ground are CPU-bound; lower limits reduce the frame drops.
  public const int DebrisIslandsOrig = 2500, DebrisShapesOrig = 0;
  public const int DebrisIslandsRec = 1500, DebrisShapesRec = 1500;   // shown by default while the ini has the game's values

  public static void ReadDebris(string game, out int islands, out int shapes) {
    islands = DebrisIslandsOrig; shapes = DebrisShapesOrig;
    try {
      var t = File.ReadAllText(IniPath(game), Encoding.Default);
      int v;
      if (int.TryParse((GetValue(t, "SystemSettings", "ApexDestructionMaxChunkIslandCount") ?? "").Split('.')[0].Trim(), out v)) islands = v;
      if (int.TryParse((GetValue(t, "SystemSettings", "ApexDestructionMaxShapeCount") ?? "").Split('.')[0].Trim(), out v)) shapes = v;
    } catch { }
  }

  public static void SetParticles(string game, int percent, int debrisIslands, int debrisShapes, Action<string> log) {
    bool ro;
    bool ok = EditIni(game, t => {
      t = SetValue(t, "SystemSettings", "ApexDestructionMaxChunkIslandCount", debrisIslands.ToString());
      t = SetValue(t, "SystemSettings", "ApexDestructionMaxShapeCount", debrisShapes.ToString());
      t = Regex.Replace(t, @"(\[Engine\.PhysicsLODVerticalEmitter\]\r?\nParticlePercentage=)\d+", "${1}" + percent);
      t = Regex.Replace(t, @"(?m)^ApexLODResourceBudget=.*$", "ApexLODResourceBudget=1000000020040877300000.000000");
      t = Regex.Replace(t, @"(?m)^PhysXGpuHeapSize=\d+", "PhysXGpuHeapSize=256");
      t = Regex.Replace(t, @"(?m)^PhysXMeshCacheSize=\d+", "PhysXMeshCacheSize=64");
      return ForceSafe(t);
    }, out ro);
    if (!ok) { log(L.T("Az XEngine.ini még nem létezik (indítsd el egyszer a játékot), a részecskebeállítás kimaradt.")); return; }
    log(L.F("XEngine.ini: ParticlePercentage={0}, APEX-keret korlátlan, PhysX GPU-heap 256 MB.", percent) + (ro ? L.T(" (írásvédett marad)") : ""));
    log(L.F("XEngine.ini: törmelék-korlát {0} darabcsoport, {1} darab.", debrisIslands, debrisShapes == 0 ? L.T("korlátlan") : debrisShapes.ToString()));
  }

  public static int ReadParticles(string game) {
    try {
      var m = Regex.Match(File.ReadAllText(IniPath(game), Encoding.Default), @"\[Engine\.PhysicsLODVerticalEmitter\]\r?\nParticlePercentage=(\d+)");
      if (m.Success) return int.Parse(m.Groups[1].Value);
    } catch { }
    return 100;
  }

  public static void Uninstall(string game, bool restoreIni, Action<string> log) {
    var bin = BinDir(game);
    foreach (var f in new[] { "nvcuda.dll", "cuda_fake_cc" }) {
      var p = Path.Combine(bin, f);
      if (File.Exists(p)) File.Delete(p);
    }
    foreach (var d in new[] { "cuda_ptx", "cuda_dump" }) {
      var p = Path.Combine(bin, d);
      if (Directory.Exists(p)) Directory.Delete(p, true);
    }
    foreach (var f in Directory.GetFiles(bin, "bureau_cuda*.log")) File.Delete(f);
    RestorePhysXCore(bin, log);
    log(L.T("Proxy és cseremodulok eltávolítva. (A driverprofil 32 bites PhysX-kapcsolója maradt, az nem árt.)"));
    var ini = IniPath(game);
    var bak = ini + ".bak_physx_setup";
    if (restoreIni && File.Exists(bak)) {
      if (File.Exists(ini)) File.SetAttributes(ini, File.GetAttributes(ini) & ~FileAttributes.ReadOnly);
      File.Copy(bak, ini, true); File.Delete(bak);
      log(L.T("XEngine.ini visszaállítva (az írásvédelem levéve)."));
    }
  }
}

// ---------------------------------------------------------------- graphics settings (XEngine.ini)
class GfxSetting {
  public string Section, Key, Label, Hint;
  public string[] Options;      // null = on/off
  public string Original, Max;  // game's shipped value / recommended best quality
  public GfxSetting(string section, string key, string label, string hint, string[] options, string original, string max) {
    Section = section; Key = key; Label = label; Hint = hint; Options = options; Original = original; Max = max;
  }
  public bool IsBool { get { return Options == null; } }
}

static class Gfx {
  // Special keys handled in Read/Write: FPS (MaxSmoothedFrameRate + MaxSmoothedFPS + bSmoothFrameRate),
  // TRILINEAR (MipFilter of the texture groups in [SystemSettings]).
  public static readonly GfxSetting[] All = {
    new GfxSetting("Engine.Engine", "FPS", "FPS-korlát", "A driverprofilba kerül (a játék menüje nem írja felül).",
                   new[] { "30", "60", "90", "120", "144", "165", "175", "240", "0" }, "122", "120"),
    new GfxSetting("SystemSettings", "AllowD3D11", "DirectX 11", "DX11-es renderelő a DX9 helyett.", null, "False", "True"),
    new GfxSetting("SystemSettings", "AllowSubsurfaceScattering", "Subsurface scattering", "A bőr fényáteresztése (DX11).", null, "False", "True"),
    new GfxSetting("SystemSettings", "bAllowTileBasedLightingDX11", "Tile-based lighting", "Csempézett világítás (DX11).", null, "False", "True"),
    new GfxSetting("SystemSettings", "MaxAnisotropy", "Anizotróp szűrés", "Ferde felületek textúraélessége.", new[] { "1", "2", "4", "8", "16" }, "8", "16"),
    new GfxSetting("SystemSettings", "FXAATechnique", "FXAA minősége", "Utófeldolgozásos élsimítás.", new[] { "0", "1", "2", "3", "4", "5", "6" }, "3", "6"),
    new GfxSetting("SystemSettings", "bAllowPostprocessMLAA", "MLAA", "Másik utófeldolgozásos élsimítás.", null, "False", "False"),
    new GfxSetting("SystemSettings", "bAllowTemporalAA", "Temporális AA", "Korai UE3-as TAA, szellemképes.", null, "False", "False"),
    new GfxSetting("SystemSettings", "ShadowAASampleNum", "Árnyékélek simítása", "Mintaszám az árnyékok szélén.", new[] { "1", "2", "4", "8" }, "4", "8"),
    new GfxSetting("SystemSettings", "MaxWholeSceneDominantShadowResolution", "Fő árnyékok felbontása", "A fő fényforrás árnyéktérképe.", new[] { "1024", "2048", "4096" }, "2048", "4096"),
    new GfxSetting("SystemSettings", "MaxNumCastingShadowLights", "Árnyékot vető fények", "Egyszerre árnyékot vető fények.", new[] { "4", "8", "12", "16" }, "12", "16"),
    new GfxSetting("SystemSettings", "bEnableForegroundSelfShadowing", "Fegyver önárnyéka", "Az előtér saját árnyéka.", null, "False", "True"),
    new GfxSetting("SystemSettings", "HighPrecisionGBuffers", "Pontos G-buffer", "Kevesebb sávosodás a fényekben.", null, "False", "True"),
    new GfxSetting("SystemSettings", "bUseMaxQualityMode", "Max. minőségű mód", "A motor saját módja (kísérleti).", null, "False", "False"),
    new GfxSetting("TextureStreaming", "PoolSize", "Textúra-keret (MB)", "Textúrák videomemória-kerete.", new[] { "200", "512", "1024", "2048", "3072" }, "200", "200"),
    new GfxSetting("SystemSettings", "TRILINEAR", "Trilineáris szűrés", "Sima átmenet a mip-szintek között.", null, "False", "False"),
  };

  static bool ParseBool(string v) { return v != null && v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase); }
  const string TexGroups = @"(?m)^(TEXTUREGROUP_(?!Lightmap|Shadowmap|RenderTarget|ColorLookupTable|Bokeh|Terrain_|ImageBasedReflection)[A-Za-z_]+=\([^)]*?)MipFilter=";

  public static string Read(string t, GfxSetting s) {
    if (s.Key == "FPS") {
      // Driver cap set -> that value. Otherwise the engine limiter decides, which the game's menu overrides
      // (effectively ~60 fps): show it as the original mode, so choosing any value really applies it.
      int drv = Setup.ReadDriverFps();
      if (drv > 0) return drv.ToString();
      return ParseBool(Setup.GetValue(t, "Engine.Engine", "bSmoothFrameRate")) ? s.Original : "0";
    }
    if (s.Key == "TRILINEAR") {
      var w = Setup.GetValue(t, "SystemSettings", "TEXTUREGROUP_World");
      return w != null && w.IndexOf("MipFilter=Linear", StringComparison.OrdinalIgnoreCase) >= 0 ? "True" : "False";
    }
    var r = Setup.GetValue(t, s.Section, s.Key);
    if (r == null) return s.Original;
    return s.IsBool ? (ParseBool(r) ? "True" : "False") : r.Split('.')[0];
  }

  public static string Write(string t, GfxSetting s, string value) {
    if (s.Key == "FPS") {   // ini part only; the driver part is Setup.ApplyDriverFps
      int fps = int.Parse(value);
      bool original = fps == Setup.OriginalFps;
      t = Setup.SetValue(t, "Engine.Engine", "bSmoothFrameRate", original ? "TRUE" : "FALSE");
      if (fps > 0) {
        t = Setup.SetValue(t, "Engine.Engine", "MaxSmoothedFrameRate", fps + ".000000");
        t = Setup.SetValue(t, "SystemSettings", "MaxSmoothedFPS", fps + ".000000");
      }
      return t;
    }
    if (s.Key == "TRILINEAR") {
      int a, b;
      if (!Setup.SectionBounds(t, "SystemSettings", out a, out b)) return t;
      var body = t.Substring(a, b - a);
      body = value == "True" ? Regex.Replace(body, TexGroups + "Point", "${1}MipFilter=Linear")
                             : Regex.Replace(body, TexGroups + "Linear", "${1}MipFilter=Point");
      return t.Substring(0, a) + body + t.Substring(b);
    }
    return Setup.SetValue(t, s.Section, s.Key, value);
  }
}

// ---------------------------------------------------------------- UI
class SetupForm : Form {
  TextBox pathBox = new TextBox();
  Label status = new Label();
  TrackBar slider = new TrackBar();
  NumericUpDown debrisIslands = new NumericUpDown(), debrisShapes = new NumericUpDown();
  Label sliderValue = new Label();
  Button install = new Button(), apply = new Button(), uninstall = new Button(), browse = new Button();
  Button gfxSave = new Button(), gfxOriginal = new Button(), gfxMax = new Button(), gfxReload = new Button();
  TextBox logBox = new TextBox();
  Dictionary<GfxSetting, Control> gfxControls = new Dictionary<GfxSetting, Control>();
  Label uwMonitor = new Label(), uwState = new Label();
  CheckBox uwCheck = new CheckBox();
  Button uwApply = new Button();
  CutscenesTab cutscenes;

  void LoadUltrawide() {
    var g = Game;
    if (g == null) { uwState.Text = ""; uwCheck.Enabled = uwApply.Enabled = false; return; }
    bool on, off;
    string st;
    try { st = Ultrawide.State(g, out on, out off); } catch (Exception ex) { st = ex.Message; on = off = false; }
    uwState.Text = L.F("Jelenlegi állapot: {0}", st);
    uwState.ForeColor = on || off ? Color.DarkGreen : Color.DarkRed;
    uwCheck.Checked = on;
    uwCheck.Enabled = uwApply.Enabled = on || off;
  }

  public SetupForm() {
    Text = L.T("The Bureau – GPU PhysX és grafika");
    Font = new Font("Segoe UI", 9f);
    ClientSize = new Size(620, 660);
    FormBorderStyle = FormBorderStyle.FixedDialog;
    MaximizeBox = false;
    StartPosition = FormStartPosition.CenterScreen;
    try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

    var gl = new Label { Text = L.T("Játék mappája:"), Location = new Point(14, 12), AutoSize = true };
    var langLabel = new Label { Text = L.T("Nyelv:"), Location = new Point(400, 12), Size = new Size(100, 18), TextAlign = ContentAlignment.TopRight };
    var lang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(506, 8), Size = new Size(100, 23) };
    lang.Items.AddRange(L.Names);
    lang.SelectedIndex = Math.Max(0, Array.IndexOf(L.Codes, L.Cur));
    lang.SelectedIndexChanged += (s, e) => {
      var code = L.Codes[lang.SelectedIndex];
      if (code == L.Cur) return;
      L.Save(code);
      Program.Restart = true;
      Close();
    };
    pathBox.Location = new Point(14, 34); pathBox.Size = new Size(500, 23);
    browse.Text = L.T("Tallózás…"); browse.Location = new Point(522, 33); browse.Size = new Size(84, 25);
    status.Location = new Point(14, 62); status.Size = new Size(592, 20);

    var tabs = new TabControl { Location = new Point(14, 84), Size = new Size(592, 420) };
    var tabPhysx = new TabPage("PhysX");
    var tabGfx = new TabPage(L.T("Grafika"));
    var tabUw = new TabPage("21:9");
    var tabVid = new TabPage(L.T("AI-felskálázott átvezetők"));
    tabs.TabPages.Add(tabPhysx); tabs.TabPages.Add(tabGfx); tabs.TabPages.Add(tabUw); tabs.TabPages.Add(tabVid);
    if (Program.StartTab > 0 && Program.StartTab < tabs.TabPages.Count) tabs.SelectedIndex = Program.StartTab;

    // --- 21:9 tab
    var scrB = Setup.PhysicalScreen();
    var uwIntro = new Label {
      Text = L.T("Ultraszéles (pl. 21:9) monitorhoz: a játék a monitor natív felbontásán fut, a 3D-s kép kitölti a képernyőt, " +
                 "a menük és a HUD pedig egy középső 16:9-es területre kerülnek, így nem vágódnak le. A videók 16:9-ben maradnak."),
      Location = new Point(10, 10), Size = new Size(564, 64) };
    var uwSource = new Label {
      Text = L.T("A menüjavítás módszerét Helifax Widescreen Fixer-pluginja („The Bureau XCOM”) mutatta meg; a kód önálló, saját megvalósítás. A TheBureau.exe-t módosítja, mentéssel, és kikapcsolva visszaállítja."),
      Location = new Point(10, 78), Size = new Size(564, 52), ForeColor = SystemColors.GrayText };
    uwMonitor.Location = new Point(10, 140); uwMonitor.Size = new Size(564, 20);
    uwMonitor.Text = L.F("Monitor: {0}×{1}", scrB.Width, scrB.Height);
    uwState.Location = new Point(10, 164); uwState.Size = new Size(564, 36);
    uwCheck.Text = L.T("21:9 / ultraszéles javítás bekapcsolva"); uwCheck.Location = new Point(10, 208); uwCheck.Size = new Size(400, 24);
    uwApply.Text = L.T("Alkalmaz"); uwApply.Location = new Point(10, 242); uwApply.Size = new Size(170, 32);
    tabUw.Controls.AddRange(new Control[] { uwIntro, uwSource, uwMonitor, uwState, uwCheck, uwApply });
    uwApply.Click += (s, e) => { Run(() => Setup.ApplyUltrawide(Game, uwCheck.Checked, Log)); LoadUltrawide(); };

    // --- PhysX tab
    var intro = new Label {
      Text = L.T("GPU-s PhysX (füst, részecskék) NVIDIA kártyákon a GTX 900-as sorozattól (csak RTX 50-en tesztelve).\n" +
                 "A játék DLL-jeit nem módosítja: egy nvcuda.dll proxyt és az NVIDIA PTX-es GPU-kódját teszi mellé."),
      Location = new Point(10, 8), Size = new Size(564, 52)
    };
    var group = new GroupBox { Text = L.T("Részecskék mennyisége"), Location = new Point(10, 64), Size = new Size(560, 108) };
    slider.Minimum = 50; slider.Maximum = 200; slider.TickFrequency = 10; slider.SmallChange = 10; slider.LargeChange = 25;
    slider.Location = new Point(10, 22); slider.Size = new Size(460, 45); slider.Value = 100;
    sliderValue.Location = new Point(476, 26); sliderValue.Size = new Size(76, 24); sliderValue.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
    var hint = new Label { Text = L.T("100% = a játék eredeti maximuma. 100% felett több részecske, mint amennyit a fejlesztők terveztek."),
                           Location = new Point(10, 66), Size = new Size(545, 36), ForeColor = SystemColors.GrayText };
    group.Controls.AddRange(new Control[] { slider, sliderValue, hint });
    var dgroup = new GroupBox { Text = L.T("Törmelék (roncsolás)"), Location = new Point(10, 176), Size = new Size(560, 100) };
    var dl1 = new Label { Text = L.T("Darabcsoportok:"), Location = new Point(10, 24), Size = new Size(130, 20) };
    debrisIslands.Minimum = 100; debrisIslands.Maximum = 10000; debrisIslands.Increment = 100;
    debrisIslands.Location = new Point(142, 21); debrisIslands.Size = new Size(80, 23);
    var dl2 = new Label { Text = L.T("Darabok (0 = korlátlan):"), Location = new Point(244, 24), Size = new Size(170, 20) };
    debrisShapes.Minimum = 0; debrisShapes.Maximum = 20000; debrisShapes.Increment = 100;
    debrisShapes.Location = new Point(416, 21); debrisShapes.Size = new Size(80, 23);
    var dhint = new Label { Text = L.T("Egyszerre szimulált roncsdarabok. A törmeléket a CPU számolja: sok darabnál harcban leesik az fps. Gyári: 2500 / 0 (korlátlan); ajánlott 1500 / 1500, kevesebb = simább."),
                            Location = new Point(10, 50), Size = new Size(545, 46), ForeColor = SystemColors.GrayText };
    dgroup.Controls.AddRange(new Control[] { dl1, debrisIslands, dl2, debrisShapes, dhint });
    install.Text = L.T("Telepítés"); install.Location = new Point(10, 284); install.Size = new Size(170, 32);
    apply.Text = L.T("Csak a beállítások mentése"); apply.Location = new Point(190, 284); apply.Size = new Size(210, 32);
    uninstall.Text = L.T("Eltávolítás"); uninstall.Location = new Point(410, 284); uninstall.Size = new Size(160, 32);
    var note = new Label {
      Text = L.T("Követelmény: NVIDIA PhysX System Software (a GeForce-driverrel felkerül) és 32 bites CUDA-támogatás a driverben.\n" +
                 "Az XEngine.ini lehet írásvédett: a telepítő csak mentéskor veszi le a védelmet, utána visszateszi."),
      Location = new Point(10, 324), Size = new Size(564, 60), ForeColor = SystemColors.GrayText };
    tabPhysx.Controls.AddRange(new Control[] { intro, group, dgroup, install, apply, uninstall, note });

    // --- Graphics tab: one row per setting
    var experimental = new Label {
      Text = L.T("Kísérleti beállítások: nem garantált, hogy jól működnek, vagy hogy egyáltalán bármit is változtatnak a játékban."),
      Location = new Point(8, 6), Size = new Size(568, 32), ForeColor = Color.DarkRed, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    var panel = new Panel { Location = new Point(0, 40), Size = new Size(584, 308), AutoScroll = true };
    int y = 4;
    foreach (var s in Gfx.All) {
      var lbl = new Label { Text = L.T(s.Label), Location = new Point(10, y + 3), Size = new Size(170, 20), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
      Control c;
      if (s.IsBool) c = new CheckBox { Text = L.T("be"), Location = new Point(186, y + 1), Size = new Size(90, 22) };
      else {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(186, y), Size = new Size(90, 23) };
        foreach (var o in s.Options) cb.Items.Add(OptionText(s, o));
        c = cb;
      }
      var desc = new Label { Text = L.F("{0} Eredeti: {1}.", L.T(s.Hint), Pretty(s, s.Original)), Location = new Point(284, y + 1), Size = new Size(280, 30),
                             ForeColor = SystemColors.GrayText, Font = new Font("Segoe UI", 8f) };
      panel.Controls.AddRange(new Control[] { lbl, c, desc });
      gfxControls[s] = c;
      y += 32;
    }
    gfxReload.Text = L.T("Beolvasás"); gfxReload.Location = new Point(8, 354); gfxReload.Size = new Size(100, 30);
    gfxOriginal.Text = L.T("Eredeti értékek"); gfxOriginal.Location = new Point(114, 354); gfxOriginal.Size = new Size(120, 30);
    gfxMax.Text = L.T("Ajánlott maximum"); gfxMax.Location = new Point(240, 354); gfxMax.Size = new Size(130, 30);
    gfxSave.Text = L.T("Grafika mentése"); gfxSave.Location = new Point(444, 354); gfxSave.Size = new Size(130, 30);
    tabGfx.Controls.AddRange(new Control[] { experimental, panel, gfxReload, gfxOriginal, gfxMax, gfxSave });

    logBox.Multiline = true; logBox.ReadOnly = true; logBox.ScrollBars = ScrollBars.Vertical;
    logBox.Location = new Point(14, 512); logBox.Size = new Size(592, 134); logBox.Font = new Font("Consolas", 8.5f);

    Controls.AddRange(new Control[] { gl, langLabel, lang, pathBox, browse, status, tabs, logBox });

    slider.ValueChanged += (s, e) => { int v = (int)Math.Round(slider.Value / 10.0) * 10; if (v != slider.Value) slider.Value = v; sliderValue.Text = slider.Value + "%"; };
    browse.Click += (s, e) => {
      using (var d = new FolderBrowserDialog { Description = L.T("Válaszd ki a The Bureau mappáját (amelyikben a Binaries mappa van)") })
        if (d.ShowDialog(this) == DialogResult.OK) { pathBox.Text = d.SelectedPath; Refresh_(); LoadGfx(); }
    };
    pathBox.Leave += (s, e) => { Refresh_(); LoadGfx(); };
    install.Click += (s, e) => Run(() => Setup.Install(Game, slider.Value, (int)debrisIslands.Value, (int)debrisShapes.Value, Log));
    apply.Click += (s, e) => Run(() => Setup.SetParticles(Game, slider.Value, (int)debrisIslands.Value, (int)debrisShapes.Value, Log));
    uninstall.Click += (s, e) => {
      var r = MessageBox.Show(this, L.T("Visszaállítsam az XEngine.ini-t is a telepítés előtti állapotra?\n(Ez a grafikai beállításokat is visszaállítja.)"),
                              L.T("Eltávolítás"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
      if (r == DialogResult.Cancel) return;
      Run(() => Setup.Uninstall(Game, r == DialogResult.Yes, Log));
      LoadGfx();
    };
    gfxReload.Click += (s, e) => LoadGfx();
    gfxOriginal.Click += (s, e) => { foreach (var kv in gfxControls) SetControl(kv.Key, kv.Key.Original); };
    gfxMax.Click += (s, e) => { foreach (var kv in gfxControls) SetControl(kv.Key, kv.Key.Max); };
    gfxSave.Click += (s, e) => Run(SaveGfx);

    pathBox.Text = Program.StartGame ?? Setup.FindGame() ?? "";
    cutscenes = new CutscenesTab(tabVid, this, () => Game, Log);
    FormClosing += (s, e) => { if (!cutscenes.ConfirmClose()) e.Cancel = true; };
    Refresh_();
    if (Game != null) slider.Value = Math.Max(50, Math.Min(200, Setup.ReadParticles(Game)));
    LoadDebris();
    sliderValue.Text = slider.Value + "%";
    LoadGfx();
  }

  void LoadDebris() {
    int i = Setup.DebrisIslandsOrig, sh = Setup.DebrisShapesOrig;
    if (Game != null) Setup.ReadDebris(Game, out i, out sh);
    if (i == Setup.DebrisIslandsOrig && sh == Setup.DebrisShapesOrig) { i = Setup.DebrisIslandsRec; sh = Setup.DebrisShapesRec; }
    debrisIslands.Value = Math.Max(debrisIslands.Minimum, Math.Min(debrisIslands.Maximum, i));
    debrisShapes.Value = Math.Max(debrisShapes.Minimum, Math.Min(debrisShapes.Maximum, sh));
  }

  static string OptionText(GfxSetting s, string o) { return s.Key == "FPS" && o == "0" ? L.T("korlátlan") : o; }

  static string Pretty(GfxSetting s, string v) {
    if (s.IsBool) return v == "True" ? L.T("be") : L.T("ki");
    return OptionText(s, v);
  }

  void SetControl(GfxSetting s, string v) {
    var c = gfxControls[s];
    if (s.IsBool) { ((CheckBox)c).Checked = v == "True"; return; }
    var cb = (ComboBox)c;
    int i = Array.IndexOf(s.Options, v);
    if (i < 0) {   // value not in the list (e.g. the game's original 122 fps): add it so it is shown as-is
      var opts = new List<string>(s.Options); opts.Add(v); s.Options = opts.ToArray();
      cb.Items.Add(OptionText(s, v)); i = s.Options.Length - 1;
    }
    cb.SelectedIndex = i;
  }

  string GetControl(GfxSetting s) {
    var c = gfxControls[s];
    if (s.IsBool) return ((CheckBox)c).Checked ? "True" : "False";
    var cb = (ComboBox)c;
    return cb.SelectedIndex < 0 ? null : s.Options[cb.SelectedIndex];
  }

  void LoadGfx() {
    LoadUltrawide();
    bool ok = Game != null && File.Exists(Setup.IniPath(Game));
    foreach (var c in gfxControls.Values) c.Enabled = ok;
    gfxSave.Enabled = gfxOriginal.Enabled = gfxMax.Enabled = gfxReload.Enabled = ok;
    if (!ok) return;
    Setup.CurrentGame = Game;
    var t = File.ReadAllText(Setup.IniPath(Game), Encoding.Default);
    foreach (var s in Gfx.All) SetControl(s, Gfx.Read(t, s));
  }

  void SaveGfx() {
    bool ro;
    var changed = new List<string>();
    string fps = null;
    Setup.CurrentGame = Game;
    bool ok = Setup.EditIni(Game, t => {
      foreach (var s in Gfx.All) {
        var v = GetControl(s);
        if (v == null) continue;
        if (Gfx.Read(t, s) == v) continue;
        changed.Add(L.T(s.Label) + " = " + Pretty(s, v));
        t = Gfx.Write(t, s, v);
        if (s.Key == "FPS") fps = v;
      }
      return Setup.ForceSafe(t);
    }, out ro);
    if (!ok) { Log(L.T("Az XEngine.ini még nem létezik – indítsd el egyszer a játékot.")); return; }
    if (changed.Count == 0) Log(L.T("Grafika: nincs változás."));
    foreach (var c in changed) Log(L.F("Grafika: {0}", c));
    if (ro) Log(L.T("(Az XEngine.ini írásvédett maradt.)"));
    if (fps != null) Setup.ApplyDriverFps(int.Parse(fps), Log);
    LoadGfx();
  }

  string Game {
    get {
      var p = pathBox.Text.Trim();
      if (p.Length == 0) return null;
      if (File.Exists(Path.Combine(p, "TheBureau.exe"))) p = Path.GetFullPath(Path.Combine(p, @"..\.."));   // Binaries\Win32 given
      return File.Exists(Path.Combine(Setup.BinDir(p), "TheBureau.exe")) ? p : null;
    }
  }

  void Refresh_() {
    var g = Game;
    if (cutscenes != null) cutscenes.Refresh();
    bool upd = Directory.Exists(Setup.UpdateDir);
    if (g == null) { status.Text = L.T("A játék nem található. Add meg a mappát."); status.ForeColor = Color.DarkRed; }
    else if (!upd) { status.Text = L.F("Hiányzik az NVIDIA PhysX-frissítés ({0}).", Setup.UpdateDir); status.ForeColor = Color.DarkRed; }
    else {
      status.Text = Setup.IsInstalled(g) ? L.T("GPU PhysX telepítve.") : L.T("GPU PhysX nincs telepítve.");
      var dp = Setup.DriverProfileState();
      if (dp != null) status.Text += "   " + dp;
      var core = Setup.PhysXCoreState(g);
      if (core != null) status.Text += "   " + core;
      status.ForeColor = Color.DarkGreen;
    }
    install.Enabled = g != null && upd;
    install.Text = g != null && Setup.IsInstalled(g) ? L.T("Újratelepítés") : L.T("Telepítés");
    apply.Enabled = uninstall.Enabled = g != null && Setup.IsInstalled(g);
  }

  void Log(string s) { logBox.AppendText(s + Environment.NewLine); logBox.Update(); }

  void Run(Action a) {
    if (Game == null) { Refresh_(); return; }
    if (System.Diagnostics.Process.GetProcessesByName("TheBureau").Length > 0) {
      MessageBox.Show(this, L.T("Előbb lépj ki a játékból."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }
    Cursor = Cursors.WaitCursor;
    try { a(); }
    catch (UnauthorizedAccessException ex) {
      Log(L.F("HIBA: {0}", ex.Message));
      MessageBox.Show(this, L.F("Nincs írási jog. Indítsd a telepítőt rendszergazdaként.\n\n{0}", ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    catch (Exception ex) { Log(L.F("HIBA: {0}", ex.Message)); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    finally { Cursor = Cursors.Default; Refresh_(); }
  }
}
