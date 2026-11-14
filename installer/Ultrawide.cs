// 21:9 (and any wider-than-16:9) fix for The Bureau. Approach from Helifax's Widescreen Fixer plugin
// "Plugin.The Bureau XCOM.dll" (2013, Steam exe v0.1.0.15143), studied without running it and independently
// reimplemented here (no plugin code or files are used): the Scaleform (menu/HUD) viewport is narrowed to a
// centered 16:9 area, so menus are no longer cut at the top/bottom. Gameplay is Hor+ already.
// The movie that shows the pre-rendered videos (2560x1440 stage, scale mode NoScale = 0) must keep the full
// screen, otherwise the video is cropped and shifted right. It is first set up like a menu movie and only then
// switched to NoScale, which re-runs the setup with the stored (already narrowed) viewport, so a NoScale movie
// that still carries our narrowed values gets the full screen back (measured in game with a debug build).
//
// TheBureau.exe patch (file on disk, original bytes verified first; disable restores them):
//   VA 0x20F20F7 (9 B, Scaleform viewport setup, esi = movie)  fild [esi+0x50]; fld [esi+0x98]
//     ->  jmp cave; 4x nop
//   cave VA 0x10EA612 (71 B in a 78-byte int3 padding, relative jumps only; the exe is ASLR-relocated):
//     cmp dword [esi+0x88], 0                ; scale mode NoScale?
//     je noscale
//     mov dword [esi+0x50], <16:9 width>     ; viewport Width
//     mov dword [esi+0x48], <left offset>    ; viewport Left
//     jmp orig
//   noscale:
//     cmp dword [esi+0x50], <16:9 width>; jne orig
//     cmp dword [esi+0x48], <left offset>; jne orig
//     mov dword [esi+0x50], <screen width>; mov dword [esi+0x48], 0
//   orig: fild [esi+0x50]; fld [esi+0x98]; jmp 0x20F2100
// Installer 1.0.0/1.0.1 used a 28-byte cave at VA 0x1FDB864 that narrowed every movie; it is recognised and
// replaced/removed.
using System;
using System.Diagnostics;
using System.IO;

static class Ultrawide {
  const string ExeVersion = "0.1.0.15143";
  const uint HookVa = 0x20F20F7, CaveVa = 0x10EA612, ReturnVa = 0x20F2100, OldCaveVa = 0x1FDB864;
  static readonly byte[] HookOrig = { 0xDB, 0x46, 0x50, 0xD9, 0x86, 0x98, 0x00, 0x00, 0x00 };
  const int CaveLen = 71, CaveArea = 78, OldCaveLen = 28;

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

  static byte[] Jmp(uint from, uint to, int len) {
    var b = new byte[len];
    b[0] = 0xE9;
    BitConverter.GetBytes(unchecked((int)(to - (from + 5)))).CopyTo(b, 1);
    for (int i = 5; i < len; i++) b[i] = 0x90;
    return b;
  }

  static byte[] Hook() { return Jmp(HookVa, CaveVa, 9); }
  static byte[] OldHook() { return Jmp(HookVa, OldCaveVa, 9); }

  static byte[] Cave(int width, int left, int screenW) {
    var b = new byte[CaveLen];
    int o = 0;
    b[o++] = 0x83; b[o++] = 0xBE; BitConverter.GetBytes(0x88).CopyTo(b, o); o += 4; b[o++] = 0;  // cmp dword [esi+0x88], 0
    b[o++] = 0x74; b[o++] = 16;                                                                 // je noscale
    b[o++] = 0xC7; b[o++] = 0x46; b[o++] = 0x50; BitConverter.GetBytes(width).CopyTo(b, o); o += 4;
    b[o++] = 0xC7; b[o++] = 0x46; b[o++] = 0x48; BitConverter.GetBytes(left).CopyTo(b, o); o += 4;
    b[o++] = 0xEB; b[o++] = 32;                                                                 // jmp orig
    b[o++] = 0x81; b[o++] = 0x7E; b[o++] = 0x50; BitConverter.GetBytes(width).CopyTo(b, o); o += 4;  // noscale:
    b[o++] = 0x75; b[o++] = 23;                                                                 // jne orig
    b[o++] = 0x81; b[o++] = 0x7E; b[o++] = 0x48; BitConverter.GetBytes(left).CopyTo(b, o); o += 4;
    b[o++] = 0x75; b[o++] = 14;                                                                 // jne orig
    b[o++] = 0xC7; b[o++] = 0x46; b[o++] = 0x50; BitConverter.GetBytes(screenW).CopyTo(b, o); o += 4;
    b[o++] = 0xC7; b[o++] = 0x46; b[o++] = 0x48; BitConverter.GetBytes(0).CopyTo(b, o); o += 4;
    Array.Copy(HookOrig, 0, b, o, HookOrig.Length); o += HookOrig.Length;                       // orig:
    b[o++] = 0xE9; BitConverter.GetBytes(unchecked((int)(ReturnVa - (CaveVa + CaveLen)))).CopyTo(b, o); o += 4;
    if (o != CaveLen) throw new Exception("cave size " + o);
    return b;
  }

  static bool Eq(byte[] a, int off, byte[] b) { for (int i = 0; i < b.Length; i++) if (a[off + i] != b[i]) return false; return true; }
  static bool AllCc(byte[] a, int off, int n) { for (int i = 0; i < n; i++) if (a[off + i] != 0xCC) return false; return true; }
  static byte[] Cc(int n) { var b = new byte[n]; for (int i = 0; i < n; i++) b[i] = 0xCC; return b; }

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
    int ho = (int)FileOffset(d, HookVa), co = (int)FileOffset(d, CaveVa), oo = (int)FileOffset(d, OldCaveVa);
    bool oldFree = AllCc(d, oo, OldCaveLen), newFree = AllCc(d, co, CaveArea);
    if (Eq(d, ho, HookOrig) && oldFree && newFree) { off = true; return L.T("ki"); }
    int w, l;
    var probe = Cave(0, 0, 0);
    if (Eq(d, ho, Hook()) && oldFree && Eq(d, co, new byte[] { probe[0], probe[1], probe[2], probe[3], probe[4], probe[5], probe[6] })
        && AllCc(d, co + CaveLen, CaveArea - CaveLen)) {
      w = BitConverter.ToInt32(d, co + 12); l = BitConverter.ToInt32(d, co + 19);
    } else if (Eq(d, ho, OldHook()) && newFree) {   // installer 1.0.0/1.0.1
      w = BitConverter.ToInt32(d, oo + 3); l = BitConverter.ToInt32(d, oo + 10);
    } else return L.T("az exe ezen a helyen ismeretlen módon módosítva");
    on = true;
    return L.F("be ({0} széles, {1} eltolás)", w, l);
  }

  public static void Apply(string game, bool enable, int screenW, int screenH, Action<string> log) {
    var exe = ExePath(game);
    bool on, off;
    var state = State(game, out on, out off);
    if (!on && !off) throw new Exception(L.F("A 21:9-es javítás nem alkalmazható: {0}", state));
    var d = File.ReadAllBytes(exe);
    long ho = FileOffset(d, HookVa), co = FileOffset(d, CaveVa), oo = FileOffset(d, OldCaveVa);
    var bak = exe + ".bak_orig";
    if (!File.Exists(bak) && !on) File.Copy(exe, bak);
    using (var f = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite)) {
      // Back to the original bytes at every patched place, then write the requested state.
      f.Seek(ho, SeekOrigin.Begin); f.Write(HookOrig, 0, HookOrig.Length);
      f.Seek(co, SeekOrigin.Begin); f.Write(Cc(CaveArea), 0, CaveArea);
      f.Seek(oo, SeekOrigin.Begin); f.Write(Cc(OldCaveLen), 0, OldCaveLen);
      if (enable) {
        int w, l;
        Geometry(screenW, screenH, out w, out l);
        f.Seek(co, SeekOrigin.Begin); f.Write(Cave(w, l, screenW), 0, CaveLen);
        f.Seek(ho, SeekOrigin.Begin); f.Write(Hook(), 0, 9);
        log(L.F("21:9: a menük és a HUD egy középső {0}×{1}-es területre kerülnek (eltolás {2}).", w, screenH, l));
      } else {
        log(L.T("21:9: a TheBureau.exe visszaállítva az eredetire."));
      }
    }
  }
}
