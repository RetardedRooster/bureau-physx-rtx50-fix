// The Bureau: XCOM Declassified – AI-upscaled cutscenes (local AI upscale and the optional video pack).
// Built twice from this source (bureau/build.sh):
//   BureauVideoTool.exe (anycpu, 64-bit): orchestration, DOF mask, packs.
//   BureauBink32.exe   (x86): Bink decoding with the game's own bink2w32.dll (a 32-bit DLL).
//
// Commands (BureauVideoTool.exe):
//   get-esrgan <dir>                                  download Real-ESRGAN ncnn-vulkan 20220424 (SHA-256 checked)
//   upscale <game> <work> <esrgan dir> <radvideo64.exe> [movie ...]
//                                                     local upscale, per movie: frames -> Real-ESRGAN 4x -> Lanczos
//                                                     2560x1472 + DOF mask per recipe (Recipes.cs) -> Bink 1 + all audio
//                                                     tracks -> check -> install (original backed up). Resumable; stops
//                                                     cleanly when <work>\stop exists.
//   restore <game>                                    put the backed-up original videos back
//   status <game>                                     one line per movie: name, HD/original/missing, backup
//   pack <game> <outdir> [max part bytes]             the installed HD videos -> zip parts (stored) with a manifest
//   installpack <game> <any part .zip>                install a video pack (all parts next to the given one), SHA-256
//                                                     checked, original backed up
// Commands (BureauBink32.exe): dump <bink dir> <bik> <outdir> [ranges], audio <bink dir> <bik> <outdir>,
//   info <bink dir> <bik>  ->  "w h frames tracks rate rateDiv"
// Output lines starting with "@" are for the installer UI: "@MOVIE k n name", "@FRAMES done total", "@DONE ok|fail|stopped".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

static class Tool {
  public const int OutW = 2560, OutH = 1472;
  public const string BackupDir = "_original_cutscenes";
  public const string EsrganUrl = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip";
  public const string EsrganSha = "abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d";
  public const string PackManifest = "bureau_ai_cutscenes.txt";

  static int Main(string[] a) {
    try {
      if (a.Length == 0) { Console.WriteLine("see the header of BureauVideo.cs"); return 2; }
      switch (a[0]) {
        case "dump": return Bink.Dump(a[1], a[2], a[3], a.Length > 4 ? a[4] : null);
        case "audio": return Bink.Audio(a[1], a[2], a[3]);
        case "info": { var i = Bink.Info(a[1], a[2]); Console.WriteLine(i == null ? "error" : string.Join(" ", i)); return i == null ? 1 : 0; }
        case "get-esrgan": return GetEsrgan(a[1]);
        case "upscale": return Upscaler.Run(a[1], a[2], a[3], a[4], a.Skip(5).ToArray());
        case "restore": return Restore(a[1]);
        case "status": return Status(a[1]);
        case "pack": return Pack.Make(a[1], a[2], a.Length > 3 ? long.Parse(a[3]) : 1950000000L);
        case "installpack": return Pack.Install(a[1], a[2]);
        case "mask": {   // test: mask <orig.png> <ai.png (2560x1472 or 4x)> <out.png>
          var ai = Img.Load(a[2]); if (ai.W != OutW) ai = Ops.Resize(ai, OutW, OutH, 1);
          Dof.Compose(Img.Load(a[1]), ai).Save(a[3]); return 0; }
      }
      Console.WriteLine("unknown command " + a[0]); return 2;
    } catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); Console.WriteLine("@DONE fail"); return 1; }
  }

  public static void Log(string s) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + s); Console.Out.Flush(); }
  public static string MoviesDir(string game) { return Path.Combine(game, "XGame", "Movies"); }
  public static string BinDir(string game) { return Path.Combine(game, "Binaries", "Win32"); }

  public static string Sha256(string path) {
    using (var s = File.OpenRead(path)) using (var h = SHA256.Create()) return Hex(h.ComputeHash(s));
  }
  public static string Hex(byte[] b) { var sb = new StringBuilder(); foreach (var x in b) sb.Append(x.ToString("x2")); return sb.ToString(); }

  // Bink header: "BIK"/"KB2" + version, file size - 8, frames, largest frame, frames, width, height
  public static bool BinkHeader(string path, out int w, out int h, out int frames) {
    w = h = frames = 0;
    try {
      using (var r = new BinaryReader(File.OpenRead(path))) {
        var sig = r.ReadBytes(3);
        if (!((sig[0] == 'B' && sig[1] == 'I' && sig[2] == 'K') || (sig[0] == 'K' && sig[1] == 'B' && sig[2] == '2'))) return false;
        r.ReadByte(); r.ReadInt32(); frames = r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); w = r.ReadInt32(); h = r.ReadInt32();
        return true;
      }
    } catch { return false; }
  }

  public static string[] Recipe(string name) {
    foreach (var r in Recipes.Table) if (string.Equals(r[0], name, StringComparison.OrdinalIgnoreCase)) return r;
    return null;
  }

  // The current file is backed up once, before the first replacement, if it is an original (not already HD).
  public static void BackupOriginal(string game, string name) {
    var cur = Path.Combine(MoviesDir(game), name + ".bik");
    var bdir = Path.Combine(MoviesDir(game), BackupDir);
    var bak = Path.Combine(bdir, name + ".bik");
    if (File.Exists(bak) || !File.Exists(cur)) return;
    int w, h, f;
    if (BinkHeader(cur, out w, out h, out f) && w >= OutW) return;
    Directory.CreateDirectory(bdir);
    File.Copy(cur, bak + ".tmp", true); File.Move(bak + ".tmp", bak);
  }

  public static void Replace(string game, string name, string src) {
    BackupOriginal(game, name);
    var dst = Path.Combine(MoviesDir(game), name + ".bik");
    var tmp = dst + ".hdtmp";
    if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(tmp), StringComparison.OrdinalIgnoreCase)) File.Copy(src, tmp, true);
    if (File.Exists(dst)) { var attr = File.GetAttributes(dst); if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, attr & ~FileAttributes.ReadOnly); File.Delete(dst); }
    File.Move(tmp, dst);
  }

  static int Restore(string game) {
    var bdir = Path.Combine(MoviesDir(game), BackupDir);
    if (!Directory.Exists(bdir)) { Log("no backup, nothing to restore"); Console.WriteLine("@DONE ok"); return 0; }
    int n = 0;
    foreach (var f in Directory.GetFiles(bdir, "*.bik")) {
      var dst = Path.Combine(MoviesDir(game), Path.GetFileName(f));
      if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal);
      File.Copy(f, dst, true); File.Delete(f); n++;
    }
    try { Directory.Delete(bdir); } catch { }
    Log("restored " + n + " original videos"); Console.WriteLine("@DONE ok"); return 0;
  }

  static int Status(string game) {
    var bdir = Path.Combine(MoviesDir(game), BackupDir);
    foreach (var r in Recipes.Table) {
      var p = Path.Combine(MoviesDir(game), r[0] + ".bik");
      int w, h, f;
      string st = !File.Exists(p) ? "missing" : BinkHeader(p, out w, out h, out f) ? (w >= OutW ? "upscaled" : "original") : "unknown";
      Console.WriteLine(r[0] + " " + st + (File.Exists(Path.Combine(bdir, r[0] + ".bik")) ? " backup" : ""));
    }
    return 0;
  }

  static int GetEsrgan(string dir) {
    var exe = Path.Combine(dir, "realesrgan-ncnn-vulkan.exe");
    if (File.Exists(exe) && File.Exists(Path.Combine(dir, "models", "realesrgan-x4plus.bin"))) { Log("Real-ESRGAN already present"); Console.WriteLine("@DONE ok"); return 0; }
    Directory.CreateDirectory(dir);
    var zip = Path.Combine(dir, "realesrgan.zip");
    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
    Log("download " + EsrganUrl);
    using (var wc = new WebClient()) wc.DownloadFile(EsrganUrl, zip);
    var sha = Sha256(zip);
    if (sha != EsrganSha) { File.Delete(zip); throw new Exception("Real-ESRGAN download: wrong SHA-256 " + sha); }
    using (var z = ZipFile.OpenRead(zip))
      foreach (var e in z.Entries) {
        if (e.FullName.EndsWith("/")) continue;
        var dst = Path.GetFullPath(Path.Combine(dir, e.FullName));
        if (!dst.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        e.ExtractToFile(dst, true);
      }
    File.Delete(zip);
    Log("Real-ESRGAN ready: " + exe); Console.WriteLine("@DONE ok"); return 0;
  }
}

// ---------------------------------------------------------------------------------------------------------------- Bink
static class Bink {
  [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string d);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkOpen@8")] static extern IntPtr BinkOpen(string name, uint flags);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkDoFrame@4")] static extern int BinkDoFrame(IntPtr b);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkNextFrame@4")] static extern void BinkNextFrame(IntPtr b);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkCopyToBuffer@28")] static extern int BinkCopyToBuffer(IntPtr b, IntPtr dest, int pitch, uint h, uint x, uint y, uint flags);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkClose@4")] static extern void BinkClose(IntPtr b);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkGetError@0")] static extern IntPtr BinkGetError();
  [DllImport("bink2w32.dll", EntryPoint = "_BinkSetSoundOnOff@8")] static extern int BinkSetSoundOnOff(IntPtr b, int on);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkSetSoundTrack@8")] static extern void BinkSetSoundTrack(uint total, IntPtr ids);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkGetTrackID@8")] static extern uint BinkGetTrackID(IntPtr b, uint i);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkOpenTrack@8")] static extern IntPtr BinkOpenTrack(IntPtr b, uint i);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkCloseTrack@4")] static extern void BinkCloseTrack(IntPtr t);
  [DllImport("bink2w32.dll", EntryPoint = "_BinkGetTrackData@8")] static extern uint BinkGetTrackData(IntPtr t, IntPtr dest);

  static void Need32() { if (IntPtr.Size != 4) throw new Exception("Bink decoding needs the 32-bit helper (BureauBink32.exe)"); }

  static IntPtr Open(string dllDir, string bik, uint flags) {
    Need32(); SetDllDirectoryW(dllDir);
    var b = BinkOpen(bik, flags);
    if (b == IntPtr.Zero) throw new Exception("BinkOpen failed: " + Marshal.PtrToStringAnsi(BinkGetError()));
    return b;
  }

  public static string[] Info(string dllDir, string bik) {
    var b = Open(dllDir, bik, 0);
    var r = new[] { Marshal.ReadInt32(b, 0), Marshal.ReadInt32(b, 4), Marshal.ReadInt32(b, 8), Marshal.ReadInt32(b, 14 * 4),
                    Marshal.ReadInt32(b, 20), Marshal.ReadInt32(b, 24) }.Select(x => x.ToString()).ToArray();
    BinkClose(b);
    return r;
  }

  public static int Dump(string dllDir, string bik, string outdir, string ranges) {
    Directory.CreateDirectory(outdir);
    var rl = new List<int[]>();
    if (!string.IsNullOrEmpty(ranges))
      foreach (var r in ranges.Split(',')) { var p = r.Split('-'); rl.Add(new[] { int.Parse(p[0]), int.Parse(p[p.Length - 1]) }); }
    var b = Open(dllDir, bik, 0);
    int w = Marshal.ReadInt32(b, 0), h = Marshal.ReadInt32(b, 4), frames = Marshal.ReadInt32(b, 8);
    int max = frames; if (rl.Count > 0) max = Math.Min(frames, rl.Max(q => q[1]) + 1);
    BinkSetSoundOnOff(b, 0);
    var buf = Marshal.AllocHGlobal(w * h * 4);
    for (int i = 0; i < max; i++) {
      BinkDoFrame(b);
      if (rl.Count == 0 || rl.Exists(q => i >= q[0] && i <= q[1])) {
        BinkCopyToBuffer(b, buf, w * 4, (uint)h, 0, 0, 3 | 0x80000000u);   // BINKSURFACE32 | BINKCOPYALL
        using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppRgb, buf))
          bmp.Save(Path.Combine(outdir, string.Format("f{0:00000}.png", i)), ImageFormat.Png);
        if (i % 100 == 0) Console.WriteLine("@DECODE " + i + " " + frames);
      }
      BinkNextFrame(b);
    }
    Marshal.FreeHGlobal(buf); BinkClose(b);
    Console.WriteLine("frames " + frames + " w " + w + " h " + h);
    return 0;
  }

  // every audio track -> track_<index>_id<id>.wav (the track data is read after BinkDoFrame; buffers >= 8 MB)
  public static int Audio(string dllDir, string bik, string outdir) {
    Directory.CreateDirectory(outdir);
    Need32(); SetDllDirectoryW(dllDir);
    BinkSetSoundTrack(0, IntPtr.Zero);
    var b = Open(dllDir, bik, 0x00004000);   // BINKSNDTRACK
    int frames = Marshal.ReadInt32(b, 8), n = Marshal.ReadInt32(b, 14 * 4);
    var tr = new IntPtr[n]; var bufs = new IntPtr[n]; var outs = new MemoryStream[n]; var fmt = new int[n, 3];
    for (uint i = 0; i < n; i++) {
      tr[i] = BinkOpenTrack(b, i);
      if (tr[i] == IntPtr.Zero) throw new Exception("audio track " + i + ": open failed");
      fmt[i, 0] = Marshal.ReadInt32(tr[i], 0); fmt[i, 1] = Marshal.ReadInt32(tr[i], 4); fmt[i, 2] = Marshal.ReadInt32(tr[i], 8);
      bufs[i] = Marshal.AllocHGlobal(8 << 20); outs[i] = new MemoryStream();
    }
    var tmp = new byte[1 << 20];
    for (int f = 0; f < frames; f++) {
      BinkDoFrame(b);
      for (int i = 0; i < n; i++) {
        int got = (int)BinkGetTrackData(tr[i], bufs[i]);
        if (got > 0) { if (got > tmp.Length) tmp = new byte[got]; Marshal.Copy(bufs[i], tmp, 0, got); outs[i].Write(tmp, 0, got); }
      }
      BinkNextFrame(b);
    }
    for (uint i = 0; i < n; i++) {
      var data = outs[i].ToArray();
      var path = Path.Combine(outdir, string.Format("track_{0}_id{1}.wav", i, BinkGetTrackID(b, i)));
      using (var w = new BinaryWriter(File.Create(path))) {
        int ch = fmt[i, 2], bits = fmt[i, 1], rate = fmt[i, 0], align = ch * bits / 8;
        w.Write(0x46464952); w.Write(36 + data.Length); w.Write(0x45564157);
        w.Write(0x20746d66); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate); w.Write(rate * align); w.Write((short)align); w.Write((short)bits);
        w.Write(0x61746164); w.Write(data.Length); w.Write(data);
      }
      BinkCloseTrack(tr[i]); Marshal.FreeHGlobal(bufs[i]);
    }
    BinkClose(b);
    Console.WriteLine("tracks " + n);
    return 0;
  }
}

// ------------------------------------------------------------------------------------------------------------- Images
// 8-bit planes (1 or 3 channels), row-major. Ports of the PIL operations used by dofmask.py.
class Img {
  public int W, H, C; public byte[] P;
  public Img(int w, int h, int c) { W = w; H = h; C = c; P = new byte[w * h * c]; }

  public static Img Load(string path) {
    using (var bmp = new Bitmap(path)) {
      var im = new Img(bmp.Width, bmp.Height, 3);
      var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
      var row = new byte[d.Stride];
      for (int y = 0; y < im.H; y++) {
        Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
        int o = y * im.W * 3;
        for (int x = 0; x < im.W; x++) { im.P[o + x * 3] = row[x * 3 + 2]; im.P[o + x * 3 + 1] = row[x * 3 + 1]; im.P[o + x * 3 + 2] = row[x * 3]; }
      }
      bmp.UnlockBits(d);
      return im;
    }
  }

  public void Save(string path) {
    using (var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb)) {
      var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
      var row = new byte[d.Stride];
      for (int y = 0; y < H; y++) {
        int o = y * W * 3;
        for (int x = 0; x < W; x++) { row[x * 3 + 2] = P[o + x * 3]; row[x * 3 + 1] = P[o + x * 3 + 1]; row[x * 3] = P[o + x * 3 + 2]; }
        Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, d.Stride);
      }
      bmp.UnlockBits(d);
      bmp.Save(path, ImageFormat.Png);
    }
  }

  // PIL convert('L'): ITU-R 601-2 luma, fixed point
  public Img Luma() {
    var r = new Img(W, H, 1);
    for (int i = 0, n = W * H; i < n; i++)
      r.P[i] = (byte)((P[i * 3] * 19595 + P[i * 3 + 1] * 38470 + P[i * 3 + 2] * 7471 + 0x8000) >> 16);
    return r;
  }
}

static class Ops {
  // |y - offset(y,1,0)| + |y - offset(y,0,1)| (offset wraps around, sum clamped to 255)
  public static Img Step(Img y) {
    int w = y.W, h = y.H; var r = new Img(w, h, 1);
    for (int j = 0; j < h; j++) {
      int up = (j == 0 ? h - 1 : j - 1) * w, row = j * w;
      for (int i = 0; i < w; i++) {
        int v = y.P[row + i];
        int dx = Math.Abs(v - y.P[row + (i == 0 ? w - 1 : i - 1)]), dy = Math.Abs(v - y.P[up + i]);
        r.P[row + i] = (byte)Math.Min(255, dx + dy);
      }
    }
    return r;
  }

  // PIL RankFilter(size, rank) with edge replication (image.expand): sliding histogram per row
  public static Img Rank(Img s, int size, int rank) {
    int w = s.W, h = s.H, rr = size / 2; var r = new Img(w, h, 1);
    Parallel.For(0, h, j => {
      var hist = new int[256];
      var rows = new int[size];
      for (int k = 0; k < size; k++) rows[k] = Math.Min(h - 1, Math.Max(0, j - rr + k)) * w;
      for (int dx = -rr; dx <= rr; dx++) { int c = Math.Min(w - 1, Math.Max(0, dx)); foreach (var ro in rows) hist[s.P[ro + c]]++; }
      int cur = 0, below = 0;
      for (int i = 0; i < w; i++) {
        if (i > 0) {
          int cOut = Math.Max(0, i - rr - 1), cIn = Math.Min(w - 1, i + rr);
          foreach (var ro in rows) {
            int vo = s.P[ro + cOut]; hist[vo]--; if (vo < cur) below--;
            int vi = s.P[ro + cIn]; hist[vi]++; if (vi < cur) below++;
          }
        }
        while (below > rank) { cur--; below -= hist[cur]; }
        while (below + hist[cur] <= rank) { below += hist[cur]; cur++; }
        r.P[j * w + i] = (byte)cur;
      }
    });
    return r;
  }

  // square max / min filter (separable), edge replication
  public static Img MaxMin(Img s, int size, bool max) {
    int w = s.W, h = s.H, rr = size / 2;
    var t = new Img(w, h, 1); var r = new Img(w, h, 1);
    Parallel.For(0, h, j => {
      int o = j * w;
      for (int i = 0; i < w; i++) {
        int m = max ? 0 : 255;
        for (int k = -rr; k <= rr; k++) { int v = s.P[o + Math.Min(w - 1, Math.Max(0, i + k))]; m = max ? Math.Max(m, v) : Math.Min(m, v); }
        t.P[o + i] = (byte)m;
      }
    });
    Parallel.For(0, h, j => {
      for (int i = 0; i < w; i++) {
        int m = max ? 0 : 255;
        for (int k = -rr; k <= rr; k++) { int v = t.P[Math.Min(h - 1, Math.Max(0, j + k)) * w + i]; m = max ? Math.Max(m, v) : Math.Min(m, v); }
        r.P[j * w + i] = (byte)m;
      }
    });
    return r;
  }

  public static Img Map(Img a, Func<int, int> f) {
    var lut = new byte[256]; for (int v = 0; v < 256; v++) lut[v] = (byte)Math.Max(0, Math.Min(255, f(v)));
    var r = new Img(a.W, a.H, 1); for (int i = 0; i < a.P.Length; i++) r.P[i] = lut[a.P[i]]; return r;
  }

  public static Img Zip(Img a, Img b, Func<int, int, int> f) {
    var r = new Img(a.W, a.H, 1);
    for (int i = 0; i < a.P.Length; i++) r.P[i] = (byte)Math.Max(0, Math.Min(255, f(a.P[i], b.P[i])));
    return r;
  }

  // gaussian blur (PIL GaussianBlur(radius): radius = standard deviation), edge replication
  public static Img Gauss(Img s, double sigma) {
    int rr = (int)Math.Ceiling(sigma * 3), w = s.W, h = s.H;
    var k = new float[2 * rr + 1]; float sum = 0;
    for (int i = -rr; i <= rr; i++) { k[i + rr] = (float)Math.Exp(-i * i / (2 * sigma * sigma)); sum += k[i + rr]; }
    for (int i = 0; i < k.Length; i++) k[i] /= sum;
    var t = new float[w * h]; var r = new Img(w, h, 1);
    Parallel.For(0, h, j => {
      int o = j * w;
      for (int i = 0; i < w; i++) { float a = 0; for (int q = -rr; q <= rr; q++) a += k[q + rr] * s.P[o + Math.Min(w - 1, Math.Max(0, i + q))]; t[o + i] = a; }
    });
    Parallel.For(0, h, j => {
      for (int i = 0; i < w; i++) { float a = 0; for (int q = -rr; q <= rr; q++) a += k[q + rr] * t[Math.Min(h - 1, Math.Max(0, j + q)) * w + i]; r.P[j * w + i] = (byte)Math.Max(0, Math.Min(255, (int)(a + 0.5f))); }
    });
    return r;
  }

  // PIL-style separable resampling (filter support scaled when downsampling). kind: 0 = bilinear, 1 = Lanczos-3, 2 = box
  public static Img Resize(Img s, int dw, int dh, int kind) {
    var tmp = new float[dw * s.H * s.C];
    var wx = Weights(s.W, dw, kind); var wy = Weights(s.H, dh, kind);
    int c = s.C;
    Parallel.For(0, s.H, j => {
      int so = j * s.W * c, to = j * dw * c;
      for (int i = 0; i < dw; i++) {
        var ww = wx[i];
        for (int ch = 0; ch < c; ch++) { float a = 0; for (int q = 0; q < ww.N; q++) a += ww.W[q] * s.P[so + (ww.First + q) * c + ch]; tmp[to + i * c + ch] = a; }
      }
    });
    var r = new Img(dw, dh, c);
    Parallel.For(0, dh, j => {
      var ww = wy[j];
      for (int i = 0; i < dw; i++)
        for (int ch = 0; ch < c; ch++) {
          float a = 0; for (int q = 0; q < ww.N; q++) a += ww.W[q] * tmp[((ww.First + q) * dw + i) * c + ch];
          r.P[(j * dw + i) * c + ch] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(a)));
        }
    });
    return r;
  }

  class Wt { public int First, N; public float[] W; }

  static Wt[] Weights(int src, int dst, int kind) {
    double scale = (double)src / dst, fs = Math.Max(1.0, scale);
    double support = (kind == 1 ? 3.0 : kind == 0 ? 1.0 : 0.5) * fs;
    var r = new Wt[dst];
    for (int i = 0; i < dst; i++) {
      double center = (i + 0.5) * scale;
      int lo = Math.Max(0, (int)(center - support + 0.5)), hi = Math.Min(src, (int)(center + support + 0.5));
      var w = new float[Math.Max(1, hi - lo)]; double sum = 0;
      for (int x = lo; x < hi; x++) {
        double t = (x - center + 0.5) / fs, v;
        if (kind == 1) v = Math.Abs(t) < 3 ? Sinc(t) * Sinc(t / 3) : 0;
        else if (kind == 0) v = Math.Max(0, 1 - Math.Abs(t));
        else v = (t >= -0.5 && t < 0.5) ? 1 : 0;
        w[x - lo] = (float)v; sum += v;
      }
      if (sum != 0) for (int q = 0; q < w.Length; q++) w[q] = (float)(w[q] / sum);
      r[i] = new Wt { First = lo, N = hi - lo, W = w };
    }
    return r;
  }

  static double Sinc(double x) { if (x == 0) return 1; x *= Math.PI; return Math.Sin(x) / x; }

  public static Img Nearest(Img s, int dw, int dh) {
    var r = new Img(dw, dh, 1);
    for (int j = 0; j < dh; j++) {
      int sy = Math.Min(s.H - 1, (int)((j + 0.5) * s.H / dh));
      for (int i = 0; i < dw; i++) r.P[j * dw + i] = s.P[sy * s.W + Math.Min(s.W - 1, (int)((i + 0.5) * s.W / dw))];
    }
    return r;
  }
}

// DOF protection (port of dofmask.py): soft areas of the original and edges the AI invented, outside in-focus regions,
// get the original's Lanczos enlargement instead of the AI; feathered mask.
static class Dof {
  const int SOFT = 10, SPAN = 15; const double PCT = 0.95, RATIO = 0.2, FEATHER = 6.0;
  const double INV_GAIN = 1.6; const int INV_ADD = 4, INV_MAX = 28, FOCUS = 50, FOCUS_SPAN = 51;
  static readonly int R95 = (int)(PCT * SPAN * SPAN);

  static Img Step95(Img y) { return Ops.Rank(Ops.Step(y), SPAN, R95); }

  static Img SoftRaw(Img y, Img g) {
    var small = Ops.Map(g, v => v < SOFT ? 255 : 0);
    var r = Ops.Zip(Ops.MaxMin(y, SPAN, true), Ops.MaxMin(y, SPAN, false), (a, b) => a - b);
    var wide = Ops.Zip(r, Ops.Map(g, v => Math.Min(255, (int)(v / RATIO))), (a, b) => a - b > 0 ? 255 : 0);
    return Ops.Zip(small, wide, Math.Max);
  }

  static Img Invented(Img go, Img aiSmall) {
    var ga = Step95(aiSmall.Luma());
    var lim = Ops.Map(go, v => Math.Min(255, (int)(v * INV_GAIN + INV_ADD)));
    var added = Ops.Zip(ga, lim, (a, b) => a - b > 0 ? 255 : 0);
    var notsharp = Ops.Map(go, v => v < INV_MAX ? 255 : 0);
    return Ops.Zip(added, notsharp, Math.Min);
  }

  static Img Focus(Img y) {
    int w = y.W, h = y.H;
    var g = Ops.Rank(Ops.Step(y), 5, 22);
    var q = Ops.Nearest(Ops.MaxMin(g, 5, true), w / 4, h / 4);
    q = Ops.Map(Ops.MaxMin(q, FOCUS_SPAN / 4 | 1, true), v => v >= FOCUS ? 255 : 0);
    return Ops.Nearest(q, w, h);
  }

  public static Img Mask(Img orig, Img ai) {
    var y = orig.Luma();
    var go = Step95(y);
    var m = Ops.Zip(SoftRaw(y, go), Invented(go, Ops.Resize(ai, orig.W, orig.H, 2)), Math.Max);
    m = Ops.Zip(m, Focus(y), (a, b) => a - b);
    m = Ops.MaxMin(Ops.MaxMin(m, 5, true), 5, false);
    m = Ops.MaxMin(m, 5, false);
    return Ops.Gauss(m, FEATHER);
  }

  public static Img Compose(Img orig, Img ai) {
    var lz = Ops.Resize(orig, ai.W, ai.H, 1);
    var m = Ops.Resize(Mask(orig, ai), ai.W, ai.H, 0);
    var r = new Img(ai.W, ai.H, 3);
    for (int i = 0, n = ai.W * ai.H; i < n; i++) {
      int k = m.P[i];
      for (int c = 0; c < 3; c++) { int a = ai.P[i * 3 + c], l = lz.P[i * 3 + c]; r.P[i * 3 + c] = (byte)((l * k + a * (255 - k) + 127) / 255); }
    }
    return r;
  }
}

// ------------------------------------------------------------------------------------------------------------ Upscale
static class Upscaler {
  static string game, work, esrganDir, radvideo, helper;
  static long totalAll, doneAll;   // frames still to upscale in this run, and done so far ("@PROGRESS done total")
  static void Progress() { Console.WriteLine("@PROGRESS " + doneAll + " " + totalAll); }

  static int Remaining(string name) {
    var movies = Tool.MoviesDir(game);
    var cur = Path.Combine(movies, name + ".bik"); var bak = Path.Combine(movies, Tool.BackupDir, name + ".bik");
    int w, h, f;
    if (File.Exists(cur) && Tool.BinkHeader(cur, out w, out h, out f) && w >= Tool.OutW) return 0;
    var src = File.Exists(bak) ? bak : cur;
    if (!File.Exists(src) || !Tool.BinkHeader(src, out w, out h, out f)) return 0;
    var up = Path.Combine(work, name, "up");
    return Math.Max(0, f - (Directory.Exists(up) ? Directory.GetFiles(up, "f*.png").Count(x => !x.EndsWith(".tmp.png")) : 0));
  }

  static bool StopRequested() { return File.Exists(Path.Combine(work, "stop")); }

  // runs a program, kills it when a stop is requested; returns the exit code (-1 = stopped)
  // timeoutSec > 0: killed after that long (a RAD error dialog would otherwise wait forever) -> -2
  static int RunProc(string exe, string args, string cwd, bool shell, Action<string> onLine, int timeoutSec = 0) {
    var started = DateTime.Now;
    var psi = new ProcessStartInfo(exe, args) { WorkingDirectory = cwd ?? Path.GetDirectoryName(exe) };
    if (shell) { psi.UseShellExecute = true; psi.WindowStyle = ProcessWindowStyle.Hidden; }
    else { psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }
    using (var p = Process.Start(psi)) {
      if (!shell) {
        p.OutputDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); };
        p.ErrorDataReceived += (s, e) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
      }
      while (!p.WaitForExit(1000))
      {
        if (StopRequested()) { try { p.Kill(); } catch { } p.WaitForExit(); return -1; }
        if (timeoutSec > 0 && (DateTime.Now - started).TotalSeconds > timeoutSec) { try { p.Kill(); } catch { } p.WaitForExit(); return -2; }
      }
      p.WaitForExit();
      return p.ExitCode;
    }
  }

  static string Q(string s) { return "\"" + s + "\""; }

  static string[] Info(string bik) {
    string line = null;
    int rc = RunProc(helper, "info " + Q(Tool.BinDir(game)) + " " + Q(bik), null, false, l => { if (line == null) line = l; });
    if (rc != 0 || line == null || line == "error") return null;
    return line.Split(' ');
  }

  public static int Run(string g, string w, string esr, string rad, string[] only) {
    game = Path.GetFullPath(g); work = Path.GetFullPath(w); esrganDir = esr; radvideo = rad;
    helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BureauBink32.exe");
    Directory.CreateDirectory(work);
    File.Delete(Path.Combine(work, "stop"));
    var esrExe = Path.Combine(esrganDir, "realesrgan-ncnn-vulkan.exe");
    foreach (var f in new[] { esrExe, radvideo, helper, Path.Combine(Tool.BinDir(game), "bink2w32.dll") })
      if (!File.Exists(f)) throw new Exception("missing: " + f);
    var names = Recipes.Table.Select(r => r[0]).Where(n => only.Length == 0 || only.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
    totalAll = names.Sum(n => (long)Remaining(n)); doneAll = 0;
    Console.WriteLine("@TOTAL " + totalAll); Progress();
    int k = 0, failed = 0;
    foreach (var name in names) {
      k++;
      Console.WriteLine("@MOVIE " + k + " " + names.Count + " " + name);
      if (StopRequested()) { Console.WriteLine("@DONE stopped"); return 3; }
      try {
        var r = Movie(name);
        if (r < 0) { Console.WriteLine("@DONE stopped"); return 3; }
        if (r > 0) failed++;
      } catch (Exception ex) { Tool.Log(name + ": FAILED: " + ex.Message); failed++; }
    }
    Tool.Log("all done, " + failed + " failed");
    Console.WriteLine("@DONE " + (failed == 0 ? "ok" : "fail"));
    return failed == 0 ? 0 : 1;
  }

  static HashSet<int> MaskFrames(string mode, int frames) {
    var s = new HashSet<int>();
    if (mode == "plain") return s;
    if (mode == "all") { for (int i = 0; i < frames; i++) s.Add(i); return s; }
    foreach (var seg in mode.Split(',')) { var p = seg.Split('-'); int a = int.Parse(p[0]), b = int.Parse(p[p.Length - 1]); for (int i = a; i <= b; i++) s.Add(i); }
    return s;
  }

  // 0 = done (installed or already HD), 1 = failed, -1 = stopped
  static int Movie(string name) {
    var movies = Tool.MoviesDir(game);
    var cur = Path.Combine(movies, name + ".bik");
    var bak = Path.Combine(movies, Tool.BackupDir, name + ".bik");
    int w0, h0, f0;
    if (File.Exists(cur) && Tool.BinkHeader(cur, out w0, out h0, out f0) && w0 >= Tool.OutW) { Tool.Log(name + ": already upscaled, skip"); return 0; }
    var src = File.Exists(bak) ? bak : cur;
    if (!File.Exists(src)) { Tool.Log(name + ": not in the game, skip"); return 0; }
    var W = Path.Combine(work, name);
    Directory.CreateDirectory(W);
    var recipe = Tool.Recipe(name);
    string mode = "all"; int rate = 4000000;
    if (File.Exists(Path.Combine(W, "mode.txt"))) { var l = File.ReadAllLines(Path.Combine(W, "mode.txt")); mode = l[0]; rate = int.Parse(l[1]); }
    else {
      var sha = Tool.Sha256(src);
      if (recipe != null && recipe[1] == sha) { mode = recipe[2]; rate = int.Parse(recipe[3]); }
      else Tool.Log(name + ": unknown original (other game version) - DOF mask on every frame");
      File.WriteAllLines(Path.Combine(W, "mode.txt"), new[] { mode, rate.ToString() });
    }
    var info = Info(src);
    if (info == null) throw new Exception("cannot open " + src);
    int frames = int.Parse(info[2]), tracks = int.Parse(info[3]);
    Tool.Log(name + ": " + info[0] + "x" + info[1] + ", " + frames + " frames, " + tracks + " audio tracks, mode " + (mode.Length > 40 ? "segments" : mode));
    var mask = MaskFrames(mode, frames);

    string fr = Path.Combine(W, "frames"), chunk = Path.Combine(W, "chunk"), x4 = Path.Combine(W, "x4"), up = Path.Combine(W, "up"), audio = Path.Combine(W, "audio");
    foreach (var d in new[] { fr, chunk, x4, up, audio }) Directory.CreateDirectory(d);
    if (!File.Exists(Path.Combine(W, "frames.done"))) {
      Tool.Log(name + ": decoding");
      foreach (var f in Directory.GetFiles(fr)) File.Delete(f);
      foreach (var f in Directory.GetFiles(audio)) File.Delete(f);
      int rc = RunProc(helper, "dump " + Q(Tool.BinDir(game)) + " " + Q(src) + " " + Q(fr), null, false, null);
      if (rc == -1) return -1;
      if (rc != 0 || Directory.GetFiles(fr, "*.png").Length != frames) throw new Exception("frame decoding failed");
      rc = RunProc(helper, "audio " + Q(Tool.BinDir(game)) + " " + Q(src) + " " + Q(audio), null, false, null);
      if (rc == -1) return -1;
      if (rc != 0 || Directory.GetFiles(audio, "*.wav").Length != tracks) throw new Exception("audio extraction failed");
      File.WriteAllText(Path.Combine(W, "frames.done"), "");
    }
    // resume: an interrupted chunk goes back to the queue, half-written outputs are dropped
    foreach (var f in Directory.GetFiles(chunk, "*.png")) { var d = Path.Combine(fr, Path.GetFileName(f)); if (File.Exists(d)) File.Delete(f); else File.Move(f, d); }
    foreach (var f in Directory.GetFiles(x4)) File.Delete(f);
    foreach (var f in Directory.GetFiles(up, "*.tmp.png")) File.Delete(f);
    var esrExe = Path.Combine(esrganDir, "realesrgan-ncnn-vulkan.exe");
    var t0 = DateTime.Now; int startDone = Directory.GetFiles(up, "*.png").Length;
    while (true) {
      var list = Directory.GetFiles(fr, "*.png").OrderBy(x => x).Take(100).ToList();
      if (list.Count == 0) break;
      foreach (var f in list) File.Move(f, Path.Combine(chunk, Path.GetFileName(f)));
      bool ok = false;
      for (int t = 0; t < 2 && !ok; t++) {
        int rc = RunProc(esrExe, "-i " + Q(chunk) + " -o " + Q(x4) + " -n realesrgan-x4plus -s 4 -f png", esrganDir, false, null);
        if (rc == -1) return -1;
        ok = Directory.GetFiles(x4, "*.png").Length == list.Count;
        if (!ok) { Tool.Log(name + ": Real-ESRGAN output incomplete, retry"); foreach (var f in Directory.GetFiles(x4)) File.Delete(f); }
      }
      if (!ok) throw new Exception("Real-ESRGAN failed (does the GPU support Vulkan?)");
      var files = Directory.GetFiles(chunk, "*.png").Select(Path.GetFileName).ToList();
      var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 4)) };
      Parallel.ForEach(files, po, n => {
        int idx = int.Parse(Path.GetFileNameWithoutExtension(n).Substring(1));
        var ai = Img.Load(Path.Combine(x4, n));
        if (ai.W != Tool.OutW || ai.H != Tool.OutH) ai = Ops.Resize(ai, Tool.OutW, Tool.OutH, 1);
        if (mask.Contains(idx)) ai = Dof.Compose(Img.Load(Path.Combine(chunk, n)), ai);
        var tmp = Path.Combine(up, n + ".tmp.png");
        ai.Save(tmp);
        File.Move(tmp, Path.Combine(up, n));
        File.Delete(Path.Combine(x4, n));
        File.Delete(Path.Combine(chunk, n));
      });
      int done = Directory.GetFiles(up, "*.png").Length;
      doneAll += files.Count;
      Console.WriteLine("@FRAMES " + done + " " + frames); Progress();
      double per = (DateTime.Now - t0).TotalSeconds / Math.Max(1, done - startDone);
      Tool.Log(string.Format("{0}: {1} / {2} frames ({3:0.0} s/frame, about {4:0} min left)", name, done, frames, per, per * (frames - done) / 60));
      if (StopRequested()) return -1;
    }
    if (Directory.GetFiles(up, "*.png").Length != frames) throw new Exception("frame count mismatch");

    // Bink 1 + every audio track (radvideo64 is a GUI program: started through the shell)
    Tool.Log(name + ": encoding Bink 1");
    var video = Path.Combine(W, "video.bik");
    int erc = RunProc(radvideo, "binkc " + Q(Path.Combine(up, "f?????.png") + "*0-" + (frames - 1)) + " " + Q(video) +
                      " /Bink1 /F" + FrameRate(info) + " /D" + rate + " /O /#", null, true, null, Math.Max(900, frames * 2));
    if (erc == -1) return -1;
    if (erc != 0 || !File.Exists(video)) throw new Exception("Bink encoding failed (" + erc + ")");
    var input = video; int mi = 0;
    foreach (var wav in Directory.GetFiles(audio, "track_*.wav").OrderBy(x => x)) {
      var id = Path.GetFileNameWithoutExtension(wav); id = id.Substring(id.LastIndexOf("_id") + 3);
      var outp = Path.Combine(W, "mix" + mi + ".bik");
      int mrc = RunProc(radvideo, "binkmix " + Q(input) + " " + Q(wav) + " " + Q(outp) + " /T" + id + " /L4 /O /#", null, true, null, Math.Max(600, frames));
      if (mrc == -1) return -1;
      if (mrc != 0 || !File.Exists(outp)) throw new Exception("audio mix failed (" + mrc + ")");
      if (input != video) File.Delete(input);
      input = outp; mi++;
    }
    var check = Info(input);
    if (check == null || check[0] != Tool.OutW.ToString() || check[1] != Tool.OutH.ToString() || check[2] != info[2] || check[3] != info[3])
      throw new Exception("check failed: " + (check == null ? "cannot open the result" : string.Join(" ", check)));
    if (System.Diagnostics.Process.GetProcessesByName("TheBureau").Length > 0) {
      Tool.Log(name + ": waiting until the game is closed");
      while (System.Diagnostics.Process.GetProcessesByName("TheBureau").Length > 0) { if (StopRequested()) return -1; Thread.Sleep(5000); }
    }
    Tool.Replace(game, name, input);
    Tool.Log(name + ": installed (" + new FileInfo(cur).Length / 1048576 + " MB)");
    try { Directory.Delete(W, true); } catch { }
    return 0;
  }

  static string FrameRate(string[] info) {
    // stored as a fraction (e.g. 5000000/208333 = 24.0); binkc takes "/F24" or "/F23.976"
    double fps = double.Parse(info[4]) / Math.Max(1, double.Parse(info[5]));
    return Math.Abs(fps - Math.Round(fps)) < 0.001 ? ((int)Math.Round(fps)).ToString()
                                                     : fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
  }
}

// ------------------------------------------------------------------------------------------------------------- Packs
// Video pack = zip parts (stored, no compression: Bink does not compress) named <prefix>.partN.zip; every part holds
// some of the videos under XGame/Movies/ and the full manifest (name|size|sha256 per line).
static class Pack {
  public static int Make(string game, string outDir, long maxPart) {
    Directory.CreateDirectory(outDir);
    var movies = Tool.MoviesDir(game);
    var items = new List<string[]>();
    foreach (var r in Recipes.Table) {
      var p = Path.Combine(movies, r[0] + ".bik"); int w, h, f;
      if (!File.Exists(p) || !Tool.BinkHeader(p, out w, out h, out f) || w < Tool.OutW) { Tool.Log(r[0] + ": not HD, left out"); continue; }
      Tool.Log("hash " + r[0]);
      items.Add(new[] { r[0], new FileInfo(p).Length.ToString(), Tool.Sha256(p) });
    }
    var manifest = string.Join("\n", items.Select(i => string.Join("|", i))) + "\n";
    // greedy: largest first into the first part with room
    var parts = new List<List<string[]>>(); var sizes = new List<long>();
    foreach (var it in items.OrderByDescending(i => long.Parse(i[1]))) {
      long s = long.Parse(it[1]); int k = sizes.FindIndex(x => x + s <= maxPart);
      if (k < 0) { parts.Add(new List<string[]>()); sizes.Add(0); k = parts.Count - 1; }
      parts[k].Add(it); sizes[k] += s;
    }
    for (int k = 0; k < parts.Count; k++) {
      var zp = Path.Combine(outDir, "TheBureau_AI_Upscaled_Cutscenes.part" + (k + 1) + ".zip");
      if (File.Exists(zp)) File.Delete(zp);
      using (var z = ZipFile.Open(zp, ZipArchiveMode.Create)) {
        using (var s = new StreamWriter(z.CreateEntry(Tool.PackManifest, CompressionLevel.Optimal).Open())) s.Write(manifest);
        foreach (var it in parts[k].OrderBy(i => i[0])) z.CreateEntryFromFile(Path.Combine(movies, it[0] + ".bik"), "XGame/Movies/" + it[0] + ".bik", CompressionLevel.NoCompression);
      }
      Tool.Log(Path.GetFileName(zp) + ": " + parts[k].Count + " videos, " + new FileInfo(zp).Length / 1048576 + " MB");
    }
    Console.WriteLine("@DONE ok"); return 0;
  }

  public static int Install(string game, string anyPart) {
    var dir = Path.GetDirectoryName(Path.GetFullPath(anyPart));
    var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(anyPart), @"^(.*)\.part\d+\.zip$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var parts = m.Success ? Directory.GetFiles(dir, m.Groups[1].Value + ".part*.zip") : new[] { anyPart };
    if (System.Diagnostics.Process.GetProcessesByName("TheBureau").Length > 0) throw new Exception("close the game first");
    Dictionary<string, string[]> manifest = null; var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in parts)
      using (var z = ZipFile.OpenRead(p)) {
        var me = z.GetEntry(Tool.PackManifest);
        if (me == null) throw new Exception(Path.GetFileName(p) + " is not a video pack (no manifest)");
        if (manifest == null) {
          manifest = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
          using (var r = new StreamReader(me.Open())) foreach (var l in r.ReadToEnd().Split('\n')) { var f = l.Trim().Split('|'); if (f.Length == 3) manifest[f[0]] = f; }
        }
        foreach (var e in z.Entries) if (e.FullName.StartsWith("XGame/Movies/") && e.FullName.EndsWith(".bik")) found[Path.GetFileNameWithoutExtension(e.Name)] = p;
      }
    var missing = manifest.Keys.Where(x => !found.ContainsKey(x)).ToList();
    if (missing.Count > 0) throw new Exception("missing parts: " + missing.Count + " videos are not in the parts found (" + string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? ", ..." : "") + ")");
    var movies = Tool.MoviesDir(game);
    if (!Directory.Exists(movies)) throw new Exception("no XGame\\Movies folder in " + game);
    int k = 0, failed = 0;
    foreach (var p in parts)
      using (var z = ZipFile.OpenRead(p))
        foreach (var e in z.Entries) {
          if (!(e.FullName.StartsWith("XGame/Movies/") && e.FullName.EndsWith(".bik"))) continue;
          var name = Path.GetFileNameWithoutExtension(e.Name); string[] mf;
          if (!manifest.TryGetValue(name, out mf)) continue;
          k++; Console.WriteLine("@MOVIE " + k + " " + manifest.Count + " " + name);
          var dst = Path.Combine(movies, name + ".bik");
          if (File.Exists(dst) && new FileInfo(dst).Length.ToString() == mf[1] && Tool.Sha256(dst) == mf[2]) { Tool.Log(name + ": already installed"); continue; }
          var tmp = dst + ".hdtmp";
          string sha;
          using (var s = e.Open()) using (var o = File.Create(tmp)) using (var h = SHA256.Create()) {
            var buf = new byte[1 << 20]; int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) { h.TransformBlock(buf, 0, n, null, 0); o.Write(buf, 0, n); }
            h.TransformFinalBlock(buf, 0, 0); sha = Tool.Hex(h.Hash);
          }
          if (sha != mf[2]) { File.Delete(tmp); Tool.Log(name + ": FAILED (damaged in the pack, SHA-256 mismatch) - original kept"); failed++; continue; }
          Tool.Replace(game, name, tmp);
          Tool.Log(name + ": installed");
        }
    Tool.Log("video pack: " + (k - failed) + " / " + manifest.Count + " installed" + (failed > 0 ? ", " + failed + " failed" : ""));
    Console.WriteLine("@DONE " + (failed == 0 ? "ok" : "fail")); return failed == 0 ? 0 : 1;
  }
}
