using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed class FishbowlClock : Button
{
    private static readonly Color DeepBlue = Color.FromArgb(10, 30, 45);
    private enum Season { Winter, Spring, Summer, Fall }

    private static readonly Color[] WaterTops =
    {
        Color.FromArgb(43, 103, 151), Color.FromArgb(42, 122, 103),
        Color.FromArgb(27, 137, 160), Color.FromArgb(128, 87, 73)
    };
    private static readonly Color[] WaterBottoms =
    {
        Color.FromArgb(15, 42, 78), Color.FromArgb(15, 61, 72),
        Color.FromArgb(12, 62, 103), Color.FromArgb(55, 37, 56)
    };
    private static readonly Color[] FloorColors =
    {
        Color.FromArgb(156, 185, 210), Color.FromArgb(67, 125, 106),
        Color.FromArgb(194, 157, 103), Color.FromArgb(133, 79, 62)
    };
    private static readonly Color[] PetalColors =
    {
        Color.FromArgb(244, 136, 187), Color.FromArgb(174, 157, 245), Color.FromArgb(255, 184, 146)
    };
    private static readonly Color[] LeafColors =
    {
        Color.FromArgb(245, 152, 64), Color.FromArgb(224, 98, 72), Color.FromArgb(239, 193, 105)
    };
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
    private int[] fish = new int[0];
    private int supervisorFish = -1;
    private readonly Random costumeRandom = new Random();
    private readonly Dictionary<int, bool> costumes = new Dictionary<int, bool>();
    private int transitionFish = -1;
    private bool leaving;
    private readonly Stopwatch transition = new Stopwatch();
    private float transitionStart;

    public void SetFish(IEnumerable<int> employeeIndices)
    {
        fish = employeeIndices.ToArray();
    }

    public void SetSupervisorFish(int employeeIndex)
    {
        supervisorFish = employeeIndex;
    }

    public void BeginPunchAnimation(int employeeIndex, bool isLeaving)
    {
        transitionFish = employeeIndex;
        leaving = isLeaving;
        transitionStart = (float)swimming.Elapsed.TotalSeconds;
        transition.Restart();
        Invalidate();
    }
    private Season season;

    public void SetDate(DateTime localDate)
    {
        Season nextSeason = (Season)((localDate.Month % 12) / 3);
        if (nextSeason != season) costumes.Clear();
        season = nextSeason;
    }

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
            costumes.Clear();
            swimming.Start();
            animation.Start();
        }
        else
        {
            animation.Stop();
            swimming.Stop();
            transition.Reset();
            transitionFish = -1;
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

        using (var glow = new LinearGradientBrush(new Rectangle(75, 135, 650, 365), WaterBottoms[(int)season], BackColor, 90))
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
                using (var water = new LinearGradientBrush(waterShape.GetBounds(), WaterTops[(int)season], WaterBottoms[(int)season], 90))
                    canvas.FillPath(water, waterShape);
            }

            DrawBowlFloor(canvas, time, season);
            DrawBubbles(canvas, time);
            canvas.Restore(clipped);

            float transitionDuration = leaving ? 6f : 4.5f;
            bool animating = transitionFish >= 0 && transition.IsRunning && transition.Elapsed.TotalSeconds < transitionDuration;
            if (animating && leaving && season == Season.Winter)
                DrawIce(canvas, bowl);
            int count = fish.Length + (animating && leaving ? 1 : 0);
            float scale = (float)Math.Min(1, Math.Sqrt(3.0 / Math.Max(1, count)));
            foreach (int index in fish)
            {
                if (animating && !leaving && index == transitionFish) continue;
                DrawFish(canvas, bowl, time, scale, index, -1, false);
            }
            if (animating && !leaving && transitionFish == supervisorFish)
                DrawSupervisorLimo(canvas, (float)transition.Elapsed.TotalSeconds);
            if (animating)
                DrawFish(canvas, bowl, leaving ? transitionStart : time, scale, transitionFish,
                    (float)(transition.Elapsed.TotalSeconds / transitionDuration), leaving);

            if (animating && leaving) DrawFisherman(canvas, season == Season.Winter,
                (float)(transition.Elapsed.TotalSeconds / transitionDuration));

            using (var outline = new Pen(Color.FromArgb(163, 123, 217, 226), 3))
                canvas.DrawPath(outline, bowl);
            using (var rim = new Pen(Color.FromArgb(186, 157, 226, 231), 4))
                canvas.DrawEllipse(rim, 213, 137, 374, 29);
            using (var reflection = new Pen(Color.FromArgb(82, 218, 251, 255), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                canvas.DrawBezier(reflection, 201, 250, 183, 281, 180, 312, 186, 334);
        }
    }

    private static void DrawBowlFloor(Graphics canvas, float time, Season current)
    {
        using (var floor = new SolidBrush(FloorColors[(int)current]))
            canvas.FillEllipse(floor, 177, 436, 446, 73);

        using (var pebbles = new SolidBrush(Color.FromArgb(80, DeepBlue)))
        {
            for (int index = 0; index < 12; index++)
                canvas.FillEllipse(pebbles, 223 + index * 31, 454 + index % 3 * 9, 14 + index % 4 * 3, 5 + index % 3);
        }

        switch (current)
        {
            case Season.Winter: DrawChristmas(canvas, time); break;
            case Season.Spring: DrawFlowers(canvas, time); break;
            case Season.Summer: DrawBeach(canvas); break;
            case Season.Fall: DrawFall(canvas, time); break;
        }
    }

    private static void DrawChristmas(Graphics canvas, float time)
    {
        using (var trunk = new SolidBrush(Color.FromArgb(124, 79, 58)))
        using (var needles = new SolidBrush(Color.FromArgb(36, 151, 112)))
        using (var light = new SolidBrush(Color.FromArgb(255, 224, 140)))
        using (var red = new SolidBrush(Color.FromArgb(233, 98, 104)))
        {
            canvas.FillRectangle(trunk, 243, 419, 14, 33);
            for (int tier = 2; tier >= 0; tier--)
            {
                int top = 344 + tier * 25;
                int spread = 25 + tier * 10;
                canvas.FillPolygon(needles, new[] { new Point(250, top), new Point(250 - spread, top + 47), new Point(250 + spread, top + 47) });
            }
            canvas.FillEllipse(light, 244, 337, 12, 12);
            canvas.FillEllipse(red, 230, 390, 8, 8);
            canvas.FillEllipse(red, 268, 410, 9, 9);
            canvas.FillEllipse(light, 246, 404, 8, 8);
            canvas.FillEllipse(light, 222, 425, 7, 7);
            canvas.FillRectangle(red, 517, 425, 43, 31);
            canvas.FillRectangle(light, 535, 425, 7, 31);
            canvas.FillRectangle(light, 517, 435, 43, 5);
        }
        using (var snow = new SolidBrush(Color.FromArgb(180, 235, 247, 255)))
        {
            for (int index = 0; index < 17; index++)
            {
                float x = 202 + index * 23 + (float)Math.Sin(time + index) * 6;
                float y = 233 + (time * (9 + index % 4) + index * 37) % 190;
                int size = 2 + index % 4;
                canvas.FillEllipse(snow, x, y, size, size);
            }
        }
    }

    private static void DrawFlowers(Graphics canvas, float time)
    {
        using (var stem = new Pen(Color.FromArgb(123, 211, 146), 4))
        using (var center = new SolidBrush(Color.FromArgb(255, 221, 135)))
        {
            for (int index = 0; index < 6; index++)
            {
                float root = index < 3 ? 225 + index * 26 : 505 + (index - 3) * 26;
                float x = root + 9 * (float)Math.Sin(time * 1.4 + index);
                float y = 395 - 20 * (index % 3);
                canvas.DrawBezier(stem, root, 457, root - 12, 427, x + 7, y + 23, x, y);
                using (var petals = new SolidBrush(PetalColors[index % PetalColors.Length]))
                {
                    for (int petal = 0; petal < 5; petal++)
                    {
                        double angle = petal * Math.PI * 2 / 5;
                        canvas.FillEllipse(petals, x + 11 * (float)Math.Cos(angle) - 7,
                            y + 11 * (float)Math.Sin(angle) - 7, 14, 14);
                    }
                }
                canvas.FillEllipse(center, x - 6, y - 6, 12, 12);
            }
        }
    }

    private static void DrawBeach(Graphics canvas)
    {
        using (var pole = new Pen(Color.FromArgb(255, 234, 193), 5))
        using (var coral = new SolidBrush(Color.FromArgb(244, 115, 103)))
        using (var cream = new SolidBrush(Color.FromArgb(255, 229, 172)))
        using (var blue = new SolidBrush(Color.FromArgb(59, 154, 214)))
        {
            canvas.DrawLine(pole, 251, 389, 245, 455);
            canvas.FillPie(coral, 196, 355, 110, 76, 180, 180);
            canvas.FillPie(cream, 219, 355, 64, 76, 180, 180);
            canvas.FillPie(coral, 239, 355, 24, 76, 180, 180);
            canvas.FillEllipse(cream, 523, 409, 43, 43);
            canvas.FillPie(coral, 523, 409, 43, 43, 270, 110);
            canvas.FillPie(blue, 523, 409, 43, 43, 120, 83);
            canvas.FillEllipse(cream, 540, 426, 9, 9);
        }
    }

    private static void DrawFall(Graphics canvas, float time)
    {
        using (var orange = new SolidBrush(Color.FromArgb(244, 146, 64)))
        using (var shadow = new SolidBrush(Color.FromArgb(194, 84, 54)))
        using (var stem = new Pen(Color.FromArgb(127, 152, 88), 5))
        {
            for (int index = 0; index < 3; index++)
            {
                int x = index == 0 ? 241 : 501 + index * 24;
                int y = index == 2 ? 437 : 424;
                canvas.DrawLine(stem, x, y, x + 4, y - 12);
                canvas.FillEllipse(shadow, x - 31, y - 2, 62, 33);
                canvas.FillEllipse(orange, x - 19, y - 3, 38, 35);
            }
        }
        for (int index = 0; index < 12; index++)
        {
            float x = 209 + index * 34 + 12 * (float)Math.Sin(time * 1.3 + index);
            float y = 236 + (time * (9 + index % 4) + index * 47) % 177;
            GraphicsState leaf = canvas.Save();
            canvas.TranslateTransform(x, y);
            canvas.RotateTransform((float)Math.Sin(time + index) * 48);
            using (var color = new SolidBrush(LeafColors[index % LeafColors.Length]))
            {
                canvas.FillPolygon(color, new[]
                {
                    new Point(-7, 0), new Point(-9, -5), new Point(-4, -4), new Point(0, -10),
                    new Point(4, -4), new Point(9, -5), new Point(7, 0), new Point(0, 8)
                });
            }
            canvas.Restore(leaf);
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

    private void DrawFish(Graphics canvas, GraphicsPath bowl, float time, float scale, int index, float progress, bool leaving)
    {
        int style = index % 3;
        int color = index % FishColors.Length;
        float depth = 275 + (index * 47 % 140);
        double speed = 0.64 + 0.09 * (index % 4);
        double offset = index * 2.4;
        float size = scale * (1.0f - 0.15f * (index % 3));
        double cycle = time * speed + offset;
        float x = 400 + (float)Math.Sin(cycle) * 152;
        float y = depth + (float)Math.Sin(cycle * 1.7) * 13;
        float direction = Math.Cos(cycle) >= 0 ? 1 : -1;
        float tail = (float)Math.Sin(time * 10 + offset) * 5;
        float bodyHeight = style == 1 ? 29 : style == 2 ? 34 : 42;
        int alpha = 255;
        float rotation = 0;
        if (progress >= 0)
        {
            if (leaving)
            {
                float seconds = progress * 6f;
                float rise = Math.Max(0, Math.Min(1, (seconds - 1.5f) / 3f));
                rise = rise * rise * (3 - 2 * rise);
                if (season == Season.Winter)
                {
                    x += (400 - direction * 27 * size - x) * Math.Min(1, rise * 2);
                    y += (185 - y) * rise;
                    float walk = Math.Max(0, Math.Min(1, (seconds - 4.7f) / 1.3f));
                    x += 420 * walk;
                }
                else
                {
                    x += (570 - x) * rise;
                    y += (137 - y) * rise;
                    float walk = Math.Max(0, Math.Min(1, (seconds - 4.7f) / 1.3f));
                    x += 420 * walk;
                }
                rotation = 0;
                tail = (float)Math.Sin(seconds * 9 + offset) * 5;
                float hookX = x + direction * 27 * size;
                float hookY = y - 4 * size;
                float cast = Math.Min(1, seconds / 1.5f);
                float walkOffset = 420 * Math.Max(0, Math.Min(1, (seconds - 4.7f) / 1.3f));
                float rodX = (season == Season.Winter ? 375 : 520) + walkOffset;
                float rodY = season == Season.Winter ? 154 : 104;
                using (var line = new Pen(Color.FromArgb(220, 237, 239, 224), 1.5f))
                {
                    if (season == Season.Winter && hookY > 208)
                    {
                        canvas.DrawLine(line, rodX, rodY, 400, 208);
                        canvas.DrawLine(line, 400, 208, 400 + (hookX - 400) * cast, 208 + (hookY - 208) * cast);
                    }
                    else
                        canvas.DrawLine(line, rodX, rodY, rodX + (hookX - rodX) * cast, rodY + (hookY - rodY) * cast);
                }
            }
            else
            {
                // Hold above the rim briefly, then accelerate into the water.
                float seconds = progress * 4.5f;
                bool limo = index == supervisorFish;
                if (limo && seconds < 1.8f) return;
                float drop = Math.Max(0, Math.Min(1, (seconds - (limo ? 1.8f : 0.35f)) / 0.8f));
                float settle = Math.Max(0, Math.Min(1, (seconds - (limo ? 2.6f : 1.15f)) / 0.8f));
                x = 400 + (x - 400) * settle;
                y = 100 + (270 - 100) * drop * drop + (y - 270) * settle;
                rotation = 70 * direction * (1 - settle);
                if (settle < 1) tail = 0;
            }
        }
        GraphicsState transform = canvas.Save();
        if (progress < 0 || (!leaving && progress * 4.5f >= (index == supervisorFish ? 2.6f : 1.15f))) canvas.SetClip(bowl, CombineMode.Intersect);
        canvas.TranslateTransform(x, y);
        canvas.RotateTransform(rotation);
        canvas.ScaleTransform(direction * size, size);

        using (var fin = new SolidBrush(Color.FromArgb(alpha, FishMarkings[color])))
        using (var body = new SolidBrush(Color.FromArgb(alpha, FishColors[color])))
        using (var dark = new SolidBrush(Color.FromArgb(alpha, DeepBlue)))
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
            using (var shine = new SolidBrush(Color.FromArgb(alpha, Color.White)))
                canvas.FillEllipse(shine, 22, -7, 2, 2);
        }

        bool dressed;
        if (!costumes.TryGetValue(index, out dressed))
        {
            dressed = costumeRandom.Next(4) == 0;
            costumes[index] = dressed;
        }
        if (index == supervisorFish) DrawCrown(canvas, bodyHeight, alpha);
        else if (dressed) DrawCostume(canvas, bodyHeight, alpha);
        canvas.Restore(transform);
    }

    private static void DrawSupervisorLimo(Graphics canvas, float seconds)
    {
        float arrival = Math.Min(1, seconds / 1.1f);
        arrival = 1 - (1 - arrival) * (1 - arrival);
        float departure = Math.Max(0, Math.Min(1, (seconds - 2.8f) / 1.7f));
        float x = -200 + 600 * arrival + 550 * departure * departure;
        GraphicsState position = canvas.Save();
        canvas.TranslateTransform(x, 98);
        using (var body = new SolidBrush(Color.FromArgb(42, 50, 66)))
        using (var roof = new SolidBrush(Color.FromArgb(60, 72, 89)))
        using (var glass = new SolidBrush(Color.FromArgb(147, 196, 208)))
        using (var trim = new Pen(Color.FromArgb(240, 210, 125), 2))
        using (var wheels = new SolidBrush(Color.FromArgb(24, 32, 44)))
        using (var hubs = new SolidBrush(Color.FromArgb(197, 214, 214)))
        using (var opening = new SolidBrush(Color.FromArgb(19, 27, 39)))
        using (var outline = new Pen(Color.FromArgb(26, 34, 48), 1.5f))
        using (var chrome = new Pen(Color.FromArgb(205, 220, 222), 1.5f) { StartCap = LineCap.Round })
        {
            using (var silhouette = new GraphicsPath())
            {
                silhouette.AddBezier(-104, -7, -103, -14, -90, -15, -68, -16);
                silhouette.AddBezier(-68, -16, -58, -30, -48, -32, -33, -32);
                silhouette.AddLine(-33, -32, 35, -32);
                silhouette.AddBezier(35, -32, 50, -32, 60, -20, 68, -16);
                silhouette.AddBezier(68, -16, 93, -15, 103, -12, 105, -5);
                silhouette.AddLine(105, -5, 105, 8);
                silhouette.AddBezier(105, 8, 105, 15, 97, 17, 87, 17);
                silhouette.AddLine(87, 17, -94, 17);
                silhouette.AddBezier(-94, 17, -104, 17, -107, 12, -107, 6);
                silhouette.CloseFigure();
                canvas.FillPath(body, silhouette);
                canvas.DrawPath(outline, silhouette);
            }
            using (var cabin = new GraphicsPath())
            {
                cabin.AddBezier(-60, -16, -52, -27, -45, -28, -32, -28);
                cabin.AddLine(-32, -28, 34, -28);
                cabin.AddBezier(34, -28, 45, -28, 54, -20, 58, -16);
                cabin.CloseFigure();
                canvas.FillPath(roof, cabin);
            }
            canvas.FillRectangle(glass, -42, -25, 31, 10);
            canvas.FillRectangle(glass, -5, -25, 29, 10);
            canvas.FillPolygon(glass, new[] { new PointF(29, -25), new PointF(39, -25), new PointF(50, -15), new PointF(29, -15) });
            canvas.DrawLine(trim, -100, 3, 100, 3);
            canvas.DrawLine(chrome, -95, 13, -82, 13);
            canvas.DrawLine(chrome, 80, 13, 100, 13);
            canvas.DrawLine(chrome, -56, -13, 54, -13);
            canvas.FillEllipse(hubs, 97, -7, 6, 5);
            canvas.FillEllipse(wheels, -78, 7, 23, 23);
            canvas.FillEllipse(wheels, 55, 7, 23, 23);
            canvas.FillEllipse(hubs, -71, 14, 9, 9);
            canvas.FillEllipse(hubs, 62, 14, 9, 9);
            float swing = Math.Min(Math.Max(0, (seconds - 1.1f) / 0.4f), Math.Max(0, (2.8f - seconds) / 0.4f));
            swing = Math.Min(1, swing);
            if (swing > 0)
            {
                canvas.FillRectangle(opening, -18, -24, 29, 38);
                float edge = 11 + 27 * swing;
                var door = new[] { new PointF(11, -24), new PointF(edge, -20 - 4 * swing),
                    new PointF(edge, 14 + 5 * swing), new PointF(11, 14) };
                canvas.FillPolygon(roof, door);
                canvas.DrawPolygon(outline, door);
                if (swing > 0.1f)
                    canvas.FillPolygon(glass, new[] { new PointF(13, -21), new PointF(edge - 2, -19 - 4 * swing),
                        new PointF(edge - 2, -10 - 3 * swing), new PointF(13, -11) });
                canvas.DrawLine(trim, 13, 3, edge - 2, 3 + 3 * swing);
                canvas.DrawLine(chrome, edge - 9, -5, edge - 4, -5);
            }
            else canvas.DrawLine(outline, -18, -14, -18, 13);
        }
        canvas.Restore(position);
    }

    private static void DrawCrown(Graphics canvas, float bodyHeight, int alpha)
    {
        float top = -bodyHeight / 2;
        using (var gold = new SolidBrush(Color.FromArgb(alpha, 255, 207, 72)))
        using (var jewel = new SolidBrush(Color.FromArgb(alpha, 230, 88, 104)))
        using (var outline = new Pen(Color.FromArgb(alpha, 165, 109, 37), 1.2f))
        {
            var points = new[]
            {
                new PointF(-11, top - 16), new PointF(-5, top - 10),
                new PointF(1, top - 20), new PointF(7, top - 10),
                new PointF(14, top - 16), new PointF(11, top), new PointF(-9, top)
            };
            canvas.FillPolygon(gold, points);
            canvas.DrawPolygon(outline, points);
            canvas.FillEllipse(jewel, -1, top - 7, 4, 4);
        }
    }

    private static void DrawIce(Graphics canvas, GraphicsPath bowl)
    {
        GraphicsState clipped = canvas.Save();
        canvas.SetClip(bowl, CombineMode.Intersect);
        using (var ice = new SolidBrush(Color.FromArgb(225, 207, 236, 245)))
        using (var edge = new Pen(Color.FromArgb(157, 201, 219), 3))
        using (var hole = new SolidBrush(Color.FromArgb(25, 75, 107)))
        {
            canvas.FillRectangle(ice, 145, 201, 510, 16);
            canvas.DrawLine(edge, 145, 217, 655, 217);
            canvas.FillEllipse(hole, 373, 200, 54, 17);
            canvas.DrawEllipse(edge, 373, 200, 54, 17);
        }
        canvas.Restore(clipped);
    }

    private static void DrawFisherman(Graphics canvas, bool winter, float progress)
    {
        GraphicsState position = canvas.Save();
        float walk = Math.Max(0, Math.Min(1, (progress * 6f - 4.7f) / 1.3f));
        canvas.TranslateTransform((winter ? -145 : 0) + 420 * walk, winter ? 50 : 0);
        using (var skin = new SolidBrush(Color.FromArgb(242, 190, 145)))
        using (var shirt = new SolidBrush(winter ? Color.FromArgb(211, 84, 76) : Color.FromArgb(231, 132, 86)))
        using (var overalls = new SolidBrush(Color.FromArgb(66, 113, 133)))
        using (var hat = new SolidBrush(Color.FromArgb(100, 126, 78)))
        using (var hatBand = new SolidBrush(Color.FromArgb(74, 92, 62)))
        using (var dark = new SolidBrush(DeepBlue))
        using (var detail = new Pen(Color.FromArgb(73, 83, 67), 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var boots = new Pen(Color.FromArgb(55, 61, 57), 7) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var rod = new Pen(Color.FromArgb(195, 150, 91), 3) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var sleeve = new Pen(winter ? Color.FromArgb(211, 84, 76) : Color.FromArgb(231, 132, 86), 8) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            // Braced feet, a round jacket and blue overalls make the pose readable at a glance.
            float stride = walk > 0 ? 5 * (float)Math.Sin(progress * 6f * 12) : 0;
            canvas.DrawLine(boots, 615, 147, 608 + stride, 157);
            canvas.DrawLine(boots, 628, 147, 635 - stride, 157);
            canvas.FillEllipse(shirt, 606, 117, 31, 33);
            canvas.FillPie(overalls, 607, 124, 29, 27, 0, 180);
            canvas.FillRectangle(overalls, 615, 122, 13, 15);
            canvas.FillEllipse(skin, 610, 96, 23, 24);
            canvas.FillEllipse(skin, 607, 106, 6, 6); // nose, facing the fish
            canvas.FillEllipse(dark, 615, 105, 2.5f, 2.5f);
            canvas.DrawArc(detail, 614, 108, 8, 6, 15, 115);
            canvas.FillEllipse(hat, 604, 94, 35, 8);
            canvas.FillPie(hat, 610, 84, 24, 23, 180, 180);
            canvas.FillRectangle(hatBand, 610, 94, 23, 3);
            if (winter)
            {
                using (var knit = new SolidBrush(Color.FromArgb(211, 84, 76)))
                using (var snow = new SolidBrush(Color.FromArgb(245, 245, 235)))
                {
                    canvas.FillPie(knit, 609, 84, 25, 25, 180, 180);
                    canvas.FillRectangle(knit, 608, 93, 27, 6);
                    canvas.FillEllipse(snow, 619, 81, 6, 6);
                    canvas.FillRectangle(snow, 609, 96, 25, 3);
                }
            }
            canvas.DrawLine(sleeve, 611, 124, 602, 131);
            canvas.DrawLine(sleeve, 602, 131, 615, 133);
            canvas.DrawBezier(rod, 618, 131, 591, 111, 555, 86, 520, 104);
            canvas.FillEllipse(skin, 612, 128, 8, 8);
            canvas.DrawEllipse(detail, 619, 132, 7, 7); // little fishing reel
        }
        canvas.Restore(position);
    }

    private void DrawCostume(Graphics canvas, float bodyHeight, int alpha)
    {
        float top = -bodyHeight / 2;
        using (var dark = new SolidBrush(Color.FromArgb(alpha, 48, 47, 57)))
        using (var light = new SolidBrush(Color.FromArgb(alpha, 255, 242, 212)))
        using (var red = new SolidBrush(Color.FromArgb(alpha, 220, 65, 73)))
        using (var gold = new SolidBrush(Color.FromArgb(alpha, 255, 207, 89)))
        using (var pink = new SolidBrush(Color.FromArgb(alpha, 245, 136, 180)))
        {
            switch (season)
            {
                case Season.Winter:
                    canvas.FillPolygon(red, new[] { new PointF(-8, top + 2), new PointF(1, top - 22), new PointF(19, top + 2) });
                    canvas.FillRectangle(light, -10, top - 2, 31, 6);
                    canvas.FillEllipse(light, -3, top - 25, 8, 8);
                    break;
                case Season.Spring:
                    for (int petal = 0; petal < 5; petal++)
                    {
                        double angle = petal * Math.PI * 2 / 5;
                        canvas.FillEllipse(pink, 3 + 7 * (float)Math.Cos(angle) - 5, top - 9 + 7 * (float)Math.Sin(angle) - 5, 10, 10);
                    }
                    canvas.FillEllipse(gold, 0, top - 12, 7, 7);
                    break;
                case Season.Summer:
                    canvas.FillEllipse(gold, -16, top - 6, 40, 10);
                    canvas.FillPie(gold, -8, top - 19, 25, 25, 180, 180);
                    canvas.FillRectangle(red, -8, top - 8, 25, 4);
                    break;
                case Season.Fall:
                    canvas.FillRectangle(dark, -10, top - 20, 25, 21);
                    canvas.FillRectangle(dark, -15, top - 3, 35, 6);
                    canvas.FillRectangle(light, -10, top - 8, 25, 5);
                    canvas.FillRectangle(gold, -1, top - 10, 7, 8);
                    canvas.FillRectangle(dark, 1, top - 8, 3, 4);
                    break;
            }
        }
    }
}
