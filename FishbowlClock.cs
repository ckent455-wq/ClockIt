using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed class FishbowlClock : Button
{
    private static readonly Color DeepBlue = Color.FromArgb(10, 30, 45);
    private static readonly Color WaterTop = Color.FromArgb(30, 111, 130);
    private static readonly Color WaterBottom = Color.FromArgb(9, 48, 69);
    private static readonly Color[] FishColors =
    {
        Color.FromArgb(255, 175, 76), Color.FromArgb(111, 207, 231), Color.FromArgb(243, 129, 165),
        Color.FromArgb(153, 232, 92), Color.FromArgb(255, 208, 94), Color.FromArgb(167, 153, 250),
        Color.FromArgb(248, 116, 106), Color.FromArgb(90, 226, 189)
    };
    private static readonly Color[] FishMarkings =
    {
        Color.FromArgb(255, 232, 171), Color.FromArgb(189, 241, 247), Color.FromArgb(123, 55, 125),
        Color.FromArgb(222, 250, 174), Color.FromArgb(255, 241, 182), Color.FromArgb(119, 70, 175),
        Color.FromArgb(255, 208, 188), Color.FromArgb(176, 255, 232)
    };
    public int FishCount { get; set; }

    private readonly Stopwatch swimming = new Stopwatch();
    private readonly Timer animation = new Timer { Interval = 33 };

    public FishbowlClock()
    {
        DoubleBuffered = true;
        animation.Tick += (sender, args) => Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs args)
    {
        base.OnVisibleChanged(args);
        if (Visible)
        {
            swimming.Start();
            animation.Start();
        }
        else
        {
            animation.Stop();
            swimming.Stop();
        }
    }

    protected override void OnTextChanged(EventArgs args)
    {
        base.OnTextChanged(args);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) animation.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        DrawScene(args.Graphics, (float)swimming.Elapsed.TotalSeconds);
    }

    private void DrawScene(Graphics canvas, float time)
    {
        canvas.Clear(BackColor);
        float scale = Math.Min(ClientSize.Width / 800f, ClientSize.Height / 520f);
        if (scale <= 0) return;

        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        canvas.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        canvas.TranslateTransform((ClientSize.Width - 800 * scale) / 2, (ClientSize.Height - 520 * scale) / 2);
        canvas.ScaleTransform(scale, scale);

        using (var glow = new LinearGradientBrush(new Rectangle(75, 135, 650, 365), DeepBlue, BackColor, 90))
            canvas.FillEllipse(glow, 75, 135, 650, 365);

        DrawBowl(canvas, time);
        using (var color = new SolidBrush(ForeColor))
        using (var alignment = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            canvas.DrawString(Text, Font, color, new RectangleF(0, 8, 800, 120), alignment);
    }

    private void DrawBowl(Graphics canvas, float time)
    {
        using (var bowl = new GraphicsPath())
        {
            bowl.AddBezier(215, 151, 211, 177, 147, 239, 147, 326);
            bowl.AddBezier(147, 326, 147, 440, 247, 490, 400, 490);
            bowl.AddBezier(400, 490, 553, 490, 653, 440, 653, 326);
            bowl.AddBezier(653, 326, 653, 239, 589, 177, 585, 151);
            bowl.CloseFigure();

            using (var glass = new LinearGradientBrush(bowl.GetBounds(), Color.FromArgb(25, 51, 67), DeepBlue, 90))
                canvas.FillPath(glass, bowl);

            GraphicsState clipped = canvas.Save();
            canvas.SetClip(bowl);
            using (var waterShape = new GraphicsPath())
            {
                float wave = (float)Math.Sin(time * 1.3) * 4;
                waterShape.AddBezier(135, 213, 275, 197 + wave, 483, 229 - wave, 665, 211);
                waterShape.AddLine(665, 211, 665, 500);
                waterShape.AddLine(665, 500, 135, 500);
                waterShape.CloseFigure();
                using (var water = new LinearGradientBrush(waterShape.GetBounds(), WaterTop, WaterBottom, 90))
                    canvas.FillPath(water, waterShape);
            }

            DrawBowlFloor(canvas, time);
            DrawBubbles(canvas, time);
            canvas.Restore(clipped);

            float scale = (float)Math.Min(1, Math.Sqrt(3.0 / Math.Max(1, FishCount)));
            for (int index = 0; index < FishCount; index++)
            {
                float depth = 264 + 162f * (index + 0.5f) / FishCount;
                double speed = 0.64 + 0.09 * (index % 4);
                double offset = index * 2.4;
                float size = scale * (1.0f - 0.15f * (index % 3));
                DrawFish(canvas, bowl, time, depth, speed, offset, size, index);
            }

            using (var outline = new Pen(Color.FromArgb(163, 123, 217, 226), 3))
                canvas.DrawPath(outline, bowl);
            using (var rim = new Pen(Color.FromArgb(186, 157, 226, 231), 4))
                canvas.DrawEllipse(rim, 213, 137, 374, 29);
            using (var reflection = new Pen(Color.FromArgb(82, 218, 251, 255), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                canvas.DrawBezier(reflection, 201, 250, 183, 281, 180, 312, 186, 334);
        }
    }

    private static void DrawBowlFloor(Graphics canvas, float time)
    {
        using (var sand = new SolidBrush(Color.FromArgb(110, 78, 149, 152)))
            canvas.FillEllipse(sand, 190, 438, 420, 65);

        using (var pebbles = new SolidBrush(Color.FromArgb(89, 113, 165, 160)))
        {
            for (int index = 0; index < 12; index++)
                canvas.FillEllipse(pebbles, 223 + index * 31, 454 + index % 3 * 9, 14 + index % 4 * 3, 5 + index % 3);
        }

        using (var stem = new Pen(Color.FromArgb(147, 81, 185, 150), 4) { EndCap = LineCap.Round })
        {
            foreach (int root in new[] { 245, 552 })
            {
                for (int blade = -1; blade <= 1; blade++)
                {
                    float sway = (float)Math.Sin(time * 1.8 + blade + root) * 6;
                    canvas.DrawBezier(stem, root, 457, root - blade * 10, 420, root + blade * 31 + sway, 415,
                        root + blade * 23 + sway, 382 + Math.Abs(blade) * 19);
                }
            }
        }
    }

    private static void DrawBubbles(Graphics canvas, float time)
    {
        using (var ring = new Pen(Color.FromArgb(123, 181, 230, 241), 1.7f))
        {
            for (int index = 0; index < 10; index++)
            {
                float x = (index % 2 == 0 ? 252 : 539) + (float)Math.Sin(time * 1.5 + index) * 9;
                float y = 446 - (time * (19 + index % 3 * 4) + index * 43) % 228;
                float diameter = 4 + index % 4 * 3;
                canvas.DrawEllipse(ring, x, y, diameter, diameter);
            }
        }
    }

    private static void DrawFish(Graphics canvas, GraphicsPath bowl, float time, float depth, double speed, double offset, float size, int index)
    {
        int style = index % 3;
        int color = index % FishColors.Length;
        double cycle = time * speed + offset;
        float x = 400 + (float)Math.Sin(cycle) * 152;
        float y = depth + (float)Math.Sin(cycle * 1.7) * 13;
        float direction = Math.Cos(cycle) >= 0 ? 1 : -1;
        float tail = (float)Math.Sin(time * 10 + offset) * 5;
        float bodyHeight = style == 1 ? 29 : style == 2 ? 34 : 42;
        GraphicsState transform = canvas.Save();
        canvas.SetClip(bowl, CombineMode.Intersect);
        canvas.TranslateTransform(x, y);
        canvas.ScaleTransform(direction * size, size);

        using (var fin = new SolidBrush(FishMarkings[color]))
        using (var body = new SolidBrush(FishColors[color]))
        using (var dark = new SolidBrush(DeepBlue))
        {
            canvas.FillPolygon(fin, new[]
            {
                new PointF(-24, 0), new PointF(-52 + tail, -18), new PointF(-48 + tail, 0), new PointF(-52 + tail, 18)
            });
            if (style != 0)
                canvas.FillPolygon(fin, new[] { new PointF(-15, -7), new PointF(0, -30), new PointF(15, -7) });
            canvas.FillEllipse(body, -31, -bodyHeight / 2, 65, bodyHeight);

            if (style == 0)
            {
                using (var stripe = new Pen(fin.Color, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    canvas.DrawBezier(stripe, -15, -13, -8, -6, -8, 7, -14, 14);
                    canvas.DrawBezier(stripe, 3, -16, 9, -7, 9, 7, 3, 16);
                }
            }
            else if (style == 1)
                canvas.FillEllipse(fin, -23, 0, 34, 5);
            else
            {
                canvas.FillEllipse(fin, -18, -8, 9, 9);
                canvas.FillEllipse(fin, -3, 3, 7, 7);
                canvas.FillEllipse(fin, 6, -11, 5, 5);
            }
            canvas.FillEllipse(dark, 19, -8, 7, 7);
            using (var shine = new SolidBrush(Color.White))
                canvas.FillEllipse(shine, 22, -7, 2, 2);
        }

        canvas.Restore(transform);
    }
}
