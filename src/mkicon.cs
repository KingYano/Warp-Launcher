// Dessine l'icône de l'app (un anneau décentré en dégradé, sur une tuile sombre) et l'écrit en .ico multi-tailles.
// Utilisé une seule fois par build.bat : mkicon.exe app.ico
// Le même dessin existe en SVG dans ui\index.html (classe « brand-mark »).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

static class MkIcon
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    static void Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "app.ico";
        List<byte[]> images = new List<byte[]>();
        foreach (int n in Sizes)
            using (Bitmap bmp = Draw(n)) images.Add(n >= 256 ? Png(bmp) : Dib(bmp));

        using (BinaryWriter w = new BinaryWriter(File.Create(output)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int n = Sizes[i];
                w.Write((byte)(n >= 256 ? 0 : n)); w.Write((byte)(n >= 256 ? 0 : n));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] img in images) w.Write(img);
        }

        // Aperçu facultatif : mkicon.exe app.ico apercu.png (grande icône + petites tailles côte à côte)
        if (args.Length > 1)
            using (Bitmap sheet = new Bitmap(420, 256, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(sheet))
                {
                    g.Clear(Color.FromArgb(255, 32, 34, 40));
                    using (Bitmap big = Draw(256)) g.DrawImageUnscaled(big, 0, 0);
                    int y = 16;
                    foreach (int n in new int[] { 48, 32, 24, 20, 16 })
                        using (Bitmap small = Draw(n)) { g.DrawImageUnscaled(small, 300, y); y += n + 14; }
                }
                sheet.Save(args[1], ImageFormat.Png);
            }
    }

    static Bitmap Draw(int n)
    {
        Bitmap bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float u = n / 100f;

            // Tuile : carré arrondi presque noir, légèrement violet.
            using (GraphicsPath tile = Rounded(0, 0, 100 * u, 100 * u, 24 * u))
            using (SolidBrush dark = new SolidBrush(Color.FromArgb(255, 21, 18, 27)))
                g.FillPath(dark, tile);

            // Anneau décentré : un disque, moins un trou décalé vers la droite (un portail vu de biais).
            // Un peu plus gros aux petites tailles pour rester lisible dans la barre des tâches.
            bool tiny = n <= 24;
            float R = tiny ? 37f : 33f, r = tiny ? 19.5f : 17.5f, shift = tiny ? 7.5f : 6.5f;
            using (GraphicsPath ring = new GraphicsPath(FillMode.Alternate))
            using (LinearGradientBrush grad = new LinearGradientBrush(new PointF(6 * u, 2 * u), new PointF(94 * u, 98 * u), Color.White, Color.Black))
            {
                ring.AddEllipse((50 - R) * u, (50 - R) * u, 2 * R * u, 2 * R * u);
                ring.AddEllipse((50 + shift - r) * u, (50 - r) * u, 2 * r * u, 2 * r * u);
                Color cream = Color.FromArgb(255, 255, 233, 184), orange = Color.FromArgb(255, 255, 143, 74);
                Color pink = Color.FromArgb(255, 255, 63, 130), violet = Color.FromArgb(255, 122, 77, 255);
                ColorBlend blend = new ColorBlend();
                blend.Colors = new Color[] { cream, cream, orange, pink, violet, violet };
                blend.Positions = new float[] { 0f, 0.2f, 0.42f, 0.64f, 0.84f, 1f };
                grad.InterpolationColors = blend;
                g.FillPath(grad, ring);
            }
        }
        return bmp;
    }

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        GraphicsPath p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static byte[] Png(Bitmap bmp)
    {
        using (MemoryStream ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    /// <summary>Image d'icône classique : en-tête BITMAPINFOHEADER, pixels BGRA de bas en haut, masque vide.</summary>
    static byte[] Dib(Bitmap bmp)
    {
        int n = bmp.Width;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), pixels, (n - 1 - y) * n * 4, n * 4);
        bmp.UnlockBits(data);

        int maskRow = ((n + 31) / 32) * 4;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(pixels.Length + maskRow * n); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            w.Write(pixels);
            w.Write(new byte[maskRow * n]);
            return ms.ToArray();
        }
    }
}
