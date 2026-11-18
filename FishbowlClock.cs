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
    private const float EventDuration = 16;
    private const float DropStart = 13;
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
    private readonly Random random = new Random();
    private readonly int[] colors = { 0, 1, 2 };
    private int selectedFish;
    private float nextEventStart;
    private bool replacementColored;
    private readonly Stopwatch swimming = new Stopwatch();
    private readonly Timer animation = new Timer { Interval = 33 };

    public FishbowlClock()
    {
        DoubleBuffered = true;
        ScheduleFishEvent(0);
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

        float eventTime = UpdateFishEvent(time);
        DrawBowl(canvas, time, eventTime);
        using (var color = new SolidBrush(ForeColor))
        using (var alignment = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            canvas.DrawString(Text, Font, color, new RectangleF(0, 8, 800, 120), alignment);
    }

    private float UpdateFishEvent(float time)
    {
        while (time >= nextEventStart + EventDuration)
        {
            ColorReplacement();
            ScheduleFishEvent(nextEventStart + EventDuration);
        }

        float eventTime = time - nextEventStart;
        if (eventTime >= DropStart) ColorReplacement();
        return eventTime;
    }

    private void ScheduleFishEvent(float after)
    {
        nextEventStart = after + random.Next(20, 66);
        selectedFish = random.Next(colors.Length);
        replacementColored = false;
    }

    private void ColorReplacement()
    {
        if (replacementColored) return;
        int choice = random.Next(FishColors.Length - colors.Length);
        for (int color = 0; color < FishColors.Length; color++)
        {
            if (Array.IndexOf(colors, color) >= 0) continue;
            if (choice-- != 0) continue;
            colors[selectedFish] = color;
            replacementColored = true;
            return;
        }
    }

    private void DrawBowl(Graphics canvas, float time, float eventTime)
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

            DrawFish(canvas, bowl, time, eventTime, 295, 0.82, 0.0, 1.0f, 0);
            DrawFish(canvas, bowl, time, eventTime, 345, 0.64, 2.0, 0.8f, 1);
            DrawFish(canvas, bowl, time, eventTime, 396, 0.91, 4.1, 0.70f, 2);

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

    private void DrawFish(Graphics canvas, GraphicsPath bowl, float time, float eventTime, float depth, double speed, double offset, float size, int style)
    {
        const float splashAt = 14.3f;
        const float waterline = 213;
        bool selected = selectedFish == style;
        bool floating = selected && eventTime >= 0 && eventTime < 9;
        if (selected && eventTime >= 9 && eventTime < DropStart) return;

        bool entering = selected && eventTime >= DropStart;
        Color color = FishColors[colors[style]];
        Color markings = FishMarkings[colors[style]];

        float swimTime = floating ? time - eventTime : time;
        double cycle = swimTime * speed + offset;
        float x = 400 + (float)Math.Sin(cycle) * 152;
        float y = depth + (float)Math.Sin(cycle * 1.7) * 13;
        float direction = Math.Cos(cycle) >= 0 ? 1 : -1;
        float tail = floating ? 0 : (float)Math.Sin(time * 10 + offset) * 5;
        float rotation = 0;
        int opacity = 255;
        if (floating)
        {
            float progress = Math.Min(1, eventTime / 4);
            float eased = progress * progress * (3 - 2 * progress);
            y += (231 - y) * eased;
            x += direction * eventTime * 3;
            rotation = direction * 180 * eased;
            if (eventTime > 6) opacity = (int)(255 * (9 - eventTime) / 3);
        }
        else if (entering)
        {
            double nextCycle = (time + EventDuration - eventTime) * speed + offset;
            float landingX = 400 + (float)Math.Sin(nextCycle) * 152;
            if (eventTime < splashAt)
            {
                float fall = (eventTime - DropStart) / (splashAt - DropStart);
                x = landingX;
                y = -55 + (waterline + 55) * fall * fall;
                direction = Math.Cos(nextCycle) >= 0 ? 1 : -1;
                rotation = 85;
            }
            else
            {
                float settle = (eventTime - splashAt) / (EventDuration - splashAt);
                float eased = settle * settle * (3 - 2 * settle);
                x = landingX + (x - landingX) * eased;
                y = waterline + (y - waterline) * eased;
                rotation = 85 * (1 - eased);
                DrawSplash(canvas, landingX, waterline, eventTime - splashAt);
            }
        }
        float bodyHeight = style == 1 ? 29 : style == 2 ? 34 : 42;
        GraphicsState transform = canvas.Save();
        if (!entering || eventTime >= splashAt) canvas.SetClip(bowl, CombineMode.Intersect);
        canvas.TranslateTransform(x, y);
        canvas.RotateTransform(rotation);
        canvas.ScaleTransform(direction * size, size);

        using (var fin = new SolidBrush(Color.FromArgb(opacity, markings)))
        using (var body = new SolidBrush(Color.FromArgb(opacity, color)))
        using (var dark = new SolidBrush(Color.FromArgb(opacity, DeepBlue)))
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
            if (floating)
            {
                using (var eye = new Pen(dark.Color, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    canvas.DrawLine(eye, 19, -8, 26, -1);
                    canvas.DrawLine(eye, 26, -8, 19, -1);
                }
            }
            else
            {
                canvas.FillEllipse(dark, 19, -8, 7, 7);
                using (var shine = new SolidBrush(Color.FromArgb(opacity, Color.White)))
                    canvas.FillEllipse(shine, 22, -7, 2, 2);
            }
        }

        canvas.Restore(transform);
    }

    private static void DrawSplash(Graphics canvas, float x, float y, float elapsed)
    {
        const float duration = 0.9f;
        if (elapsed >= duration) return;
        float progress = elapsed / duration;
        int opacity = (int)(230 * (1 - progress));
        using (var ripple = new Pen(Color.FromArgb(opacity, 174, 246, 255), 2.5f))
        using (var water = new SolidBrush(ripple.Color))
        {
            float radius = 6 + progress * 55;
            canvas.DrawEllipse(ripple, x - radius, y - radius * 0.17f, radius * 2, radius * 0.34f);
            for (int droplet = -3; droplet <= 3; droplet++)
            {
                float height = (4 - Math.Abs(droplet)) * 11 * (float)Math.Sin(progress * Math.PI);
                float spread = droplet * (5 + progress * 14);
                float diameter = 4 + (1 - progress) * 4;
                canvas.FillEllipse(water, x + spread - diameter / 2, y - height - diameter, diameter, diameter);
            }
        }
    }
}
