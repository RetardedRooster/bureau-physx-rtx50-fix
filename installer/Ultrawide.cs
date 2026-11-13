// 21:9 (and any wider-than-16:9) fix for The Bureau, ported from the Widescreen Fixer plugin
// "Plugin.The Bureau XCOM.dll" (Helifax, 2013, Steam exe v0.1.0.15143): every Scaleform (menu/HUD) viewport is
// narrowed to a centered 16:9 area, so menus are no longer cut at the top/bottom. Gameplay (Hor+) and the
// pre-rendered movies already work at 21:9 and are not touched.
//
// TheBureau.exe patch (file on disk, original bytes verified first; disable restores them):
//   VA 0x20F20F7 (9 B)  fild [esi+0x50]; fld [esi+0x98]   ->  jmp cave; 4x nop
//   VA 0x1FDB864 (28 B, int3 padding between functions):
//     mov dword [esi+0x50], <16:9 width>     ; Scaleform viewport Width
//     mov dword [esi+0x48], <left offset>    ; Scaleform viewport Left
//     fild [esi+0x50]; fld [esi+0x98]; jmp 0x20F2100
using System;
using System.Diagnostics;
using System.IO;

static class Ultrawide {
  const string ExeVersion = "0.1.0.15143";
  const uint HookVa = 0x20F20F7, CaveVa = 0x1FDB864, ReturnVa = 0x20F2100;
  static readonly byte[] HookOrig = { 0xDB, 0x46, 0x50, 0xD9, 0x86, 0x98, 0x00, 0x00, 0x00 };
  const int CaveLen = 28;

  public static string ExePath(string game) { return Path.Combine(Setup.BinDir(game), "TheBureau.exe"); }

  // 16:9 width for the given height, and the left offset that centers it on screenWidth.
  public static void Geometry(int screenW, int screenH, out int width, out int left) {
    width = (int)Math.Round(screenH * 16.0 / 9.0);
    left = Math.Max(0, (screenW - width) / 2);
  }

  static long FileOffset(byte[] pe, uint va) {
    int p = BitConverter.ToInt32(pe, 0x3c), ns = BitConverter.ToUInt16(pe, p + 6), oh = BitConverter.ToUInt16(pe, p + 20);
    uint imageBase = BitConverter.ToUInt32(pe, p + 24 + 28);
    int st = p + 24 + oh;
    for (int i = 0; i < ns; i++) {
      int h = st + i * 40;
      uint rva = BitConverter.ToUInt32(pe, h + 12), rawSize = BitConverter.ToUInt32(pe, h + 16), raw = BitConverter.ToUInt32(pe, h + 20);
      uint v = imageBase + rva;
      if (va >= v && va < v + rawSize) return raw + (va - v);
    }
    throw new Exception(L.F("A cím (0x{0}) nincs a TheBureau.exe-ben.", va.ToString("x")));
  }

  static byte[] Hook() {
    var b = new byte[9];
    b[0] = 0xE9;
    BitConverter.GetBytes(unchecked((int)(CaveVa - (HookVa + 5)))).CopyTo(b, 1);
    for (int i = 5; i < 9; i++) b[i] = 0x90;
    return b;
  }

  static byte[] Cave(int width, int left) {
    var b = new byte[CaveLen];
    int o = 0;
    b[o++] = 0xC7; b[o++] = 0x46; b[o++] = 0x50; BitConverter.GetBytes(width).CopyTo(b, o); o += 4;
    b[o++] = 0xC7; b[o++] = 0x46; b[o++] = 0x48; BitConverter.GetBytes(left).CopyTo(b, o); o += 4;
    Array.Copy(HookOrig, 0, b, o, HookOrig.Length); o += HookOrig.Length;
    b[o++] = 0xE9; BitConverter.GetBytes(unchecked((int)(ReturnVa - (CaveVa + CaveLen)))).CopyTo(b, o); o += 4;
    return b;
  }

  static bool Eq(byte[] a, int off, byte[] b) { for (int i = 0; i < b.Length; i++) if (a[off + i] != b[i]) return false; return true; }
  static bool AllCc(byte[] a, int off, int n) { for (int i = 0; i < n; i++) if (a[off + i] != 0xCC) return false; return true; }

  // Translated description: "on (W x L)", "off", or why the exe cannot be patched. off = pristine original.
  public static string State(string game, out bool on) { bool off; return State(game, out on, out off); }

  public static string State(string game, out bool on, out bool off) {
    on = false; off = false;
    var exe = ExePath(game);
    if (!File.Exists(exe)) return L.T("nincs TheBureau.exe");
    // The version string is "0,1,0,2177831"; the numeric fields give 0.1.0.15143 (2177831 & 0xFFFF).
    var fv = FileVersionInfo.GetVersionInfo(exe);
    var ver = fv.FileMajorPart + "." + fv.FileMinorPart + "." + fv.FileBuildPart + "." + fv.FilePrivatePart;
    if (ver != ExeVersion) return L.F("ismeretlen exe-verzió ({0}), a javítás csak a {1}-hoz készült", ver, ExeVersion);
    var d = File.ReadAllBytes(exe);
    long ho = FileOffset(d, HookVa), co = FileOffset(d, CaveVa);
    if (Eq(d, (int)ho, HookOrig) && AllCc(d, (int)co, CaveLen)) { off = true; return L.T("ki"); }
    if (Eq(d, (int)ho, Hook())) {
      on = true;
      return L.F("be ({0} széles, {1} eltolás)", BitConverter.ToInt32(d, (int)co + 3), BitConverter.ToInt32(d, (int)co + 10));
    }
    return L.T("az exe ezen a helyen ismeretlen módon módosítva");
  }

  public static void Apply(string game, bool enable, int screenW, int screenH, Action<string> log) {
    var exe = ExePath(game);
    bool on, off;
    var state = State(game, out on, out off);
    if (!on && !off) throw new Exception(L.F("A 21:9-es javítás nem alkalmazható: {0}", state));
    var d = File.ReadAllBytes(exe);
    long ho = FileOffset(d, HookVa), co = FileOffset(d, CaveVa);
    var bak = exe + ".bak_orig";
    if (!File.Exists(bak) && !on) File.Copy(exe, bak);
    using (var f = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite)) {
      if (enable) {
        int w, l;
        Geometry(screenW, screenH, out w, out l);
        f.Seek(co, SeekOrigin.Begin); f.Write(Cave(w, l), 0, CaveLen);
        f.Seek(ho, SeekOrigin.Begin); f.Write(Hook(), 0, 9);
        log(L.F("21:9: a menük és a HUD egy középső {0}×{1}-es területre kerülnek (eltolás {2}).", w, screenH, l));
      } else {
        f.Seek(ho, SeekOrigin.Begin); f.Write(HookOrig, 0, HookOrig.Length);
        var cc = new byte[CaveLen]; for (int i = 0; i < CaveLen; i++) cc[i] = 0xCC;
        f.Seek(co, SeekOrigin.Begin); f.Write(cc, 0, CaveLen);
        log(L.T("21:9: a TheBureau.exe visszaállítva az eredetire."));
      }
    }
  }
}
