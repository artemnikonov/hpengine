// The Launcher's own icon, drawn in code: the same red and gold shield with a wand and sparkles as launcher/icon.swift
// (the macOS app icon; coordinates on its 1024 grid, y up). No image file in the repository. Author 2026-11-13: the
// Launcher keeps its own icon; the game's hp.ico is the game window's.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

public static class ShieldIcon
{
    public static WindowIcon Make() => new(Draw(256));

    public static RenderTargetBitmap Draw(int px)
    {
        double k = px / 1024.0;
        Point P(double x, double y) => new(x * k, (1024 - y) * k);
        var red = new SolidColorBrush(Color.FromRgb(140, 18, 23));
        var gold = new SolidColorBrush(Color.FromRgb(237, 184, 56));
        var dark = new SolidColorBrush(Color.FromRgb(46, 26, 15));
        StreamGeometry Poly(params Point[] pts)
        {
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(pts[0], true); for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i]); c.EndFigure(true); }
            return g;
        }
        var shield = new StreamGeometry();
        using (var c = shield.Open())
        {
            c.BeginFigure(P(172, 900), true); c.LineTo(P(852, 900));
            c.CubicBezierTo(P(852, 470), P(700, 230), P(512, 96));
            c.CubicBezierTo(P(324, 230), P(172, 470), P(172, 900));
            c.EndFigure(true);
        }
        var bmp = new RenderTargetBitmap(new PixelSize(px, px));
        using (var d = bmp.CreateDrawingContext())
        {
            using (d.PushGeometryClip(shield))
            {
                d.FillRectangle(red, new Rect(0, 0, px, px));
                d.FillRectangle(gold, new Rect(px / 2.0, 0, px / 2.0, px));
            }
            d.DrawGeometry(null, new Pen(dark, 40 * k), shield);
            d.DrawGeometry(dark, new Pen(new SolidColorBrush(Colors.White, 0.9), 10 * k), Poly(P(318, 300), P(352, 270), P(706, 660), P(690, 676)));
            foreach (var (cx, cy, r) in new[] { (728.0, 700.0, 92.0), (612.0, 790.0, 44.0), (800.0, 600.0, 36.0) })
            {
                var pts = new Point[8];
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4 + Math.PI / 2, rr = i % 2 == 0 ? r : r * 0.32;
                    pts[i] = P(cx + Math.Cos(a) * rr, cy + Math.Sin(a) * rr);
                }
                d.DrawGeometry(Brushes.White, null, Poly(pts));
            }
        }
        return bmp;
    }
}
