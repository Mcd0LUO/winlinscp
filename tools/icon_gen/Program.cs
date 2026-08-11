using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private const int S = 512; // 主画布尺寸（矢量坐标空间）

    [STAThread]
    private static void Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(outDir);

        var group = BuildDrawing();

        // 渲染 256/512 PNG 供查看迭代 + 高清图标（关于对话框用，避免 ICO 多帧缩放发糊）
        SavePng(group, Path.Combine(outDir, "preview_256.png"), 256);
        SavePng(group, Path.Combine(outDir, "preview_512.png"), 512);
        SavePng(group, Path.Combine(outDir, "WinLinScp.png"), 256);

        // 多尺寸 ICO
        var icoPath = Path.Combine(outDir, "WinLinScp.ico");
        WriteIco(group, icoPath, new[] { 256, 64, 48, 32, 16 });
        Console.WriteLine("preview: " + Path.Combine(outDir, "preview_256.png"));
        Console.WriteLine("ico:     " + icoPath);
    }

    private static DrawingGroup BuildDrawing()
    {
        var g = new DrawingGroup();
        using var dc = g.Open();

        // ---- 背景：圆角方块 + 蓝色渐变 ----
        var bg = new RectangleGeometry(new Rect(0, 0, S, S), 92, 92);
        var bgBrush = new LinearGradientBrush(
            Color.FromRgb(0x23, 0x78, 0xE6), Color.FromRgb(0x0B, 0x3B, 0x92), 70);
        dc.DrawGeometry(bgBrush, null, bg);

        // ---- 握手：两条前臂 + 中间扣握（先画阴影，再画白色主体，形成立体感） ----
        const double armW = 76;
        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 0x0A, 0x33, 0x84)), armW + 6)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var armPen = new Pen(new SolidColorBrush(Color.FromRgb(0xF7, 0xFA, 0xFF)), armW)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        void DrawArm(Pen pen, Point a, Point b, double off) =>
            dc.DrawLine(pen, new Point(a.X + off, a.Y + off), new Point(b.X + off, b.Y + off));

        var leftArmB = new Point(118, 492); var leftArmE = new Point(250, 272);
        var rightArmB = new Point(394, 492); var rightArmE = new Point(262, 272);

        // 阴影
        DrawArm(shadowPen, leftArmB, leftArmE, 4);
        DrawArm(shadowPen, rightArmB, rightArmE, 4);

        // 白色前臂
        DrawArm(armPen, leftArmB, leftArmE, 0);
        DrawArm(armPen, rightArmB, rightArmE, 0);

        // 中间扣握：两条手交叉的胶囊（右手在里先画，左手在外后画稍亮）
        const double handW = 48;
        var rightHand = new Pen(new SolidColorBrush(Color.FromRgb(0xFB, 0xFD, 0xFF)), handW) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var leftHand = new Pen(new SolidColorBrush(Color.FromRgb(0xE9, 0xF0, 0xFB)), handW) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        DrawArm(shadowPen, new Point(266, 288), new Point(238, 246), 3);
        DrawArm(shadowPen, new Point(246, 288), new Point(274, 246), 3);
        dc.DrawLine(rightHand, new Point(266, 288), new Point(238, 246));
        dc.DrawLine(leftHand, new Point(246, 288), new Point(274, 246));

        // 指缝线（横跨扣握处，暗示手指互握）
        var groovePen = new Pen(new SolidColorBrush(Color.FromRgb(0x9B, 0xB6, 0xDD)), 4);
        foreach (var x in new[] { 249.0, 257.0, 265.0 })
            dc.DrawLine(groovePen, new Point(x, 242), new Point(x, 268));

        // ---- 左臂徽章：白圆角方块 + Windows 四格 ----
        DrawWindowsTile(dc, 151, 434);

        // ---- 右臂徽章：白圆角方块 + Tux 头 ----
        DrawTuxTile(dc, 361, 434);

        return g;
    }

    /// <summary>白底圆角方块里画 Windows 四格 Logo（左下臂徽章）。</summary>
    private static void DrawWindowsTile(DrawingContext dc, double cx, double cy)
    {
        const double tile = 74, pad = 9, cell = (tile - pad * 2 - 6) / 2;
        var tileBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        var r = new Rect(cx - tile / 2, cy - tile / 2, tile, tile);
        dc.DrawGeometry(tileBrush, null, new RectangleGeometry(r, 16, 16));

        var x0 = cx - tile / 2 + pad;
        var y0 = cy - tile / 2 + pad;
        var shades = new[]
        {
            (Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0x00, 0x67, 0xB8)),
            (Color.FromRgb(0x4A, 0xA8, 0xF2), Color.FromRgb(0x2E, 0x8B, 0xE0)),
            (Color.FromRgb(0x2E, 0x8B, 0xE0), Color.FromRgb(0x00, 0x78, 0xD4)),
            (Color.FromRgb(0x00, 0x5A, 0x9E), Color.FromRgb(0x00, 0x4B, 0x87)),
        };
        for (int row = 0; row < 2; row++)
        for (int col = 0; col < 2; col++)
        {
            var idx = row * 2 + col;
            var br = new LinearGradientBrush(shades[idx].Item1, shades[idx].Item2, 135);
            var cr = new Rect(x0 + col * (cell + 6), y0 + row * (cell + 6), cell, cell);
            dc.DrawGeometry(br, null, new RectangleGeometry(cr, 4, 4));
        }
    }

    /// <summary>白底圆角方块里画 Tux 企鹅头（右臂徽章）。</summary>
    private static void DrawTuxTile(DrawingContext dc, double cx, double cy)
    {
        const double tile = 74;
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), null,
            new RectangleGeometry(new Rect(cx - tile / 2, cy - tile / 2, tile, tile), 16, 16));

        double bx = cx, by = cy + 4, bw = 46, bh = 52;
        // 头+身（黑色）
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x1F, 0x21, 0x24)), null, new Point(bx, by - 2), bw / 2, bh / 2);
        // 白肚（下半）
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), null, new Point(bx, by + 6), bw / 2 - 9, bh / 2 - 12);
        // 嘴（橙色三角）
        var beak = new StreamGeometry();
        using (var b = beak.Open())
        {
            b.BeginFigure(new Point(bx - 9, by - 8), true, true);
            b.LineTo(new Point(bx + 9, by - 8), true, false);
            b.LineTo(new Point(bx, by + 5), true, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xF5, 0x9C, 0x28)), null, beak);
        // 眼（白+黑）
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), null, new Point(bx - 12, by - 16), 6.5, 6.5);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x1F, 0x21, 0x24)), null, new Point(bx - 11, by - 15), 3, 3);
        // 腮红（橙点，左脸颊）
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF5, 0x9C, 0x28)), null, new Point(bx - 17, by - 4), 4, 3);
    }

    private static void SavePng(DrawingGroup g, string path, int size)
    {
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var vdc = visual.RenderOpen())
            vdc.DrawDrawing(g);
        visual.Transform = new ScaleTransform(size / (double)S, size / (double)S);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void WriteIco(DrawingGroup g, string path, int[] sizes)
    {
        var pngs = sizes.Select(s => RenderPng(g, s)).ToList();
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((short)0);          // reserved
        bw.Write((short)1);          // type: icon
        bw.Write((short)sizes.Length);

        long offset = 6 + 16L * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            int sz = sizes[i];
            bw.Write((byte)(sz >= 256 ? 0 : sz)); // 0 表示 256
            bw.Write((byte)(sz >= 256 ? 0 : sz));
            bw.Write((byte)0);                    // 颜色数
            bw.Write((byte)0);                    // reserved
            bw.Write((short)1);                   // planes
            bw.Write((short)32);                  // bpp
            bw.Write(pngs[i].Length);
            bw.Write((int)offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) bw.Write(png);
    }

    private static byte[] RenderPng(DrawingGroup g, int size)
    {
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var vdc = visual.RenderOpen())
            vdc.DrawDrawing(g);
        visual.Transform = new ScaleTransform(size / (double)S, size / (double)S);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
