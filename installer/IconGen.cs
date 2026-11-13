// Build-time tool: draws the installer icon (original artwork, inspired by the game's 1960s Cold War agency mood:
// a dark steel-blue seal with an amber ring and an orange smoke/particle vortex for GPU PhysX) and writes a
// multi-resolution .ico (16..256 px, PNG-compressed entries). Usage: IconGen <out.ico> [preview.png]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class IconGen {
  static Bitmap Draw(int s) {
    var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;
      g.Clear(Color.Transparent);
      float c = s / 2f, R = s * 0.47f;
      bool small = s <= 24;

      // seal background: steel blue, darker at the rim
      using (var path = new GraphicsPath()) {
        path.AddEllipse(c - R, c - R, 2 * R, 2 * R);
        using (var br = new PathGradientBrush(path)) {
          br.CenterColor = Color.FromArgb(255, 52, 72, 82);
          br.SurroundColors = new[] { Color.FromArgb(255, 14, 22, 27) };
          g.FillPath(br, path);
        }
      }
      // amber outer ring + thin dark inner ring
      float rw = Math.Max(1.5f, s * 0.065f);
      using (var pen = new Pen(Color.FromArgb(255, 222, 146, 48), rw)) g.DrawEllipse(pen, c - R + rw / 2, c - R + rw / 2, 2 * R - rw, 2 * R - rw);
      if (!small) {
        float r2 = R - rw * 1.9f;
        using (var pen = new Pen(Color.FromArgb(200, 120, 78, 30), Math.Max(1f, s * 0.015f))) g.DrawEllipse(pen, c - r2, c - r2, 2 * r2, 2 * r2);
        // 8 retro tick marks on the ring
        using (var pen = new Pen(Color.FromArgb(255, 240, 190, 110), Math.Max(1f, s * 0.02f))) {
          for (int i = 0; i < 8; i++) {
            double a = i * Math.PI / 4;
            float r0 = R - rw * 1.15f, r1 = R - rw * 0.3f;
            g.DrawLine(pen, c + (float)Math.Cos(a) * r0, c + (float)Math.Sin(a) * r0, c + (float)Math.Cos(a) * r1, c + (float)Math.Sin(a) * r1);
          }
        }
      }
      // smoke / particle vortex: three swept arcs, 120 degrees apart, growing outwards
      float inner = R * 0.62f;
      for (int k = 0; k < 3; k++) {
        float start = k * 120f - 20f;
        for (int layer = 0; layer < (small ? 1 : 3); layer++) {
          float rr = inner * (0.45f + 0.22f * layer);
          float w = Math.Max(1.5f, s * (small ? 0.09f : 0.055f - layer * 0.008f));
          int alpha = small ? 255 : 255 - layer * 55;
          var col = layer == 0 ? Color.FromArgb(alpha, 255, 170, 60) : Color.FromArgb(alpha, 236, 118, 36);
          using (var pen = new Pen(col, w)) {
            pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
            g.DrawArc(pen, c - rr, c - rr, 2 * rr, 2 * rr, start + layer * 18f, 95f);
          }
        }
      }
      // particles along the vortex
      if (!small) {
        var rnd = new Random(7);
        for (int i = 0; i < 18; i++) {
          double a = i * 2.4 + rnd.NextDouble() * 0.4;
          float rr = inner * (0.35f + 0.65f * (i / 18f));
          float pr = s * (0.012f + 0.014f * (float)rnd.NextDouble());
          float x = c + (float)Math.Cos(a) * rr, y = c + (float)Math.Sin(a) * rr;
          using (var br = new SolidBrush(i % 3 == 0 ? Color.FromArgb(230, 255, 240, 210) : Color.FromArgb(210, 255, 180, 90)))
            g.FillEllipse(br, x - pr, y - pr, 2 * pr, 2 * pr);
        }
      }
      // glowing core
      float core = Math.Max(1.5f, s * 0.07f);
      using (var path = new GraphicsPath()) {
        path.AddEllipse(c - core * 2.2f, c - core * 2.2f, core * 4.4f, core * 4.4f);
        using (var br = new PathGradientBrush(path)) {
          br.CenterColor = Color.FromArgb(255, 255, 236, 190);
          br.SurroundColors = new[] { Color.FromArgb(0, 255, 160, 60) };
          g.FillPath(br, path);
        }
      }
      using (var br = new SolidBrush(Color.FromArgb(255, 255, 248, 225))) g.FillEllipse(br, c - core * 0.7f, c - core * 0.7f, core * 1.4f, core * 1.4f);
    }
    return bmp;
  }

  static void Main(string[] a) {
    var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
    // 256 px as PNG, smaller sizes as classic 32-bit DIBs (BITMAPINFOHEADER + BGRA bottom-up + empty AND mask):
    // PNG entries below 256 px are not decoded by GDI+/older shell code.
    var pngs = new List<byte[]>();
    foreach (var s in sizes) using (var b = Draw(s)) using (var ms = new MemoryStream()) {
      if (s >= 256) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); continue; }
      var w = new BinaryWriter(ms);
      w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32); w.Write(0);
      int maskStride = ((s + 31) / 32) * 4;
      w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
      for (int y = s - 1; y >= 0; y--)
        for (int x = 0; x < s; x++) { var p = b.GetPixel(x, y); w.Write(p.B); w.Write(p.G); w.Write(p.R); w.Write(p.A); }
      w.Write(new byte[maskStride * s]);
      w.Flush();
      pngs.Add(ms.ToArray());
    }
    using (var f = new BinaryWriter(File.Create(a[0]))) {
      f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
      int offset = 6 + 16 * sizes.Length;
      for (int i = 0; i < sizes.Length; i++) {
        f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        f.Write((byte)0); f.Write((byte)0); f.Write((short)1); f.Write((short)32);
        f.Write(pngs[i].Length); f.Write(offset);
        offset += pngs[i].Length;
      }
      foreach (var p in pngs) f.Write(p);
    }
    if (a.Length > 1) using (var b = Draw(256)) b.Save(a[1], ImageFormat.Png);
  }
}
